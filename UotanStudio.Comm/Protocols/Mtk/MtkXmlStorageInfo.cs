using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Block-storage families described by XML DA hardware information.</summary>
public enum MtkXmlStorageKind
{
    /// <summary>Embedded MultiMediaCard storage.</summary>
    Emmc,
    /// <summary>Universal Flash Storage with XML LUA identifiers, not XFlash partition numbers.</summary>
    Ufs
}

/// <summary>A device-reported XML physical region, not a logical GPT partition.</summary>
public sealed class MtkXmlStorageRegion
{
    internal MtkXmlStorageRegion(string name, long capacityBytes, bool authenticated)
    {
        Name = name;
        CapacityBytes = capacityBytes;
        RequiresAuthenticatedAccess = authenticated;
    }

    /// <summary>Gets the exact XML physical-region token.</summary>
    public string Name { get; }
    /// <summary>Gets the reported byte capacity.</summary>
    public long CapacityBytes { get; }
    /// <summary>Gets whether this profile reserves the region for a separate authenticated protocol.</summary>
    public bool RequiresAuthenticatedAccess { get; }
}

/// <summary>Immutable, bounded XML hardware geometry; neither authentication nor successful storage I/O is implied.</summary>
public sealed class MtkXmlStorageInfo
{
    internal const int MaximumDocumentLength = 64 * 1024;

    private MtkXmlStorageInfo(MtkXmlStorageKind kind, int blockSize, MtkXmlStorageRegion[] regions,
        string? reportedIdHex, string? cidHex)
    {
        Kind = kind;
        BlockSize = blockSize;
        Regions = Array.AsReadOnly(regions);
        ReportedIdHex = reportedIdHex;
        CidHex = cidHex;
    }

    /// <summary>Gets the reported block-storage family.</summary>
    public MtkXmlStorageKind Kind { get; }
    /// <summary>Gets the reported logical block size in bytes.</summary>
    public int BlockSize { get; }
    /// <summary>Gets physical regions, including reported zero-capacity and authenticated regions.</summary>
    public IReadOnlyList<MtkXmlStorageRegion> Regions { get; }
    /// <summary>Gets the optional id field, which need not equal a separate family-specific CID.</summary>
    public string? ReportedIdHex { get; }
    /// <summary>Gets the family-specific CID when present, otherwise the optional id field.
    /// Neither value is an authenticated device identity.</summary>
    public string? CidHex { get; }

    /// <summary>Validates ordinary block access without clamping, rounding or permitting authenticated regions.</summary>
    /// <param name="regionName">An exact XML region token from this geometry.</param>
    /// <param name="offset">Nonnegative block-aligned byte offset.</param>
    /// <param name="length">Positive block-aligned byte count.</param>
    public void ValidateRange(string regionName, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(regionName);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var region = Regions.FirstOrDefault(region => region.Name == regionName)
            ?? throw new ArgumentOutOfRangeException(nameof(regionName), "The XML physical region was not reported.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException("The selected XML region requires a separate authenticated protocol.");
        }
        if (offset % BlockSize != 0 || length % BlockSize != 0
            || offset > region.CapacityBytes || length > region.CapacityBytes - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The requested XML range is unaligned or exceeds reported capacity.");
        }
    }

    internal static MtkXmlStorageInfo Parse(string text)
    {
        if (text.Length is 0 or > MaximumDocumentLength)
        {
            throw new InvalidDataException("The XML hardware document exceeds its supported length.");
        }
        // Bound depth/node count before allocating the XML tree; device fields are not trusted.
        using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentLength,
            IgnoreWhitespace = true
        }))
        {
            var nodes = 0;
            while (reader.Read())
            {
                if (++nodes > 256 || reader.Depth > 3
                    || (reader.NodeType == XmlNodeType.Element && reader.AttributeCount != 0)
                    || reader.NodeType is XmlNodeType.Comment or XmlNodeType.ProcessingInstruction)
                {
                    throw new InvalidDataException("The XML hardware structure exceeds its supported profile.");
                }
            }
        }
        var root = XDocument.Parse(text).Root;
        if (root?.Name != "da_hw_info")
        {
            throw new InvalidDataException("Expected an XML DA hardware-info document.");
        }
        var fields = ReadFields(root);
        // Upstream cmd_get_hw_info may report version 1.0 (with no storage section) on
        // devices without eMMC/UFS; only 1.2 documents carry the extended geometry fields.
        var version = RequiredScalar(fields, "version");
        if (version is not ("1.0" or "1.2"))
        {
            throw new MtkProtocolException("Unsupported XML hardware-info version.");
        }
        if (version == "1.0" && !fields.ContainsKey("storage"))
        {
            throw new MtkProtocolException("The XML hardware-info reports no storage device.");
        }
        var kind = RequiredScalar(fields, "storage") switch
        {
            "EMMC" => MtkXmlStorageKind.Emmc,
            "UFS" => MtkXmlStorageKind.Ufs,
            _ => throw new MtkProtocolException("The XML storage family is absent or unsupported.")
        };
        var section = kind == MtkXmlStorageKind.Emmc ? "emmc" : "ufs";
        if (!fields.TryGetValue(section, out var geometry))
        {
            throw new MtkProtocolException("The XML physical geometry is missing.");
        }
        foreach (var field in fields)
        {
            if (field.Key != section && (field.Value.HasElements || field.Key is "emmc" or "ufs" or "nand"))
            {
                throw new InvalidDataException("Conflicting or nested XML hardware metadata.");
            }
        }
        var values = ReadFields(geometry);
        if (values.Values.Any(value => value.HasElements))
        {
            throw new InvalidDataException("XML geometry fields must be scalar.");
        }
        var blockSize = ReadHex(values, "block_size");
        if (blockSize is < 512 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new MtkProtocolException("Invalid XML storage block size.");
        }
        string[] sizeFields = kind == MtkXmlStorageKind.Emmc
            ? ["boot1_size", "boot2_size", "rpmb_size", "user_size", "gp1_size", "gp2_size", "gp3_size", "gp4_size"]
            : ["lua0_size", "lua1_size", "lua2_size", "lua3_size"];
        string[] names = kind == MtkXmlStorageKind.Emmc
            ? ["EMMC-BOOT1", "EMMC-BOOT2", "EMMC-RPMB", "EMMC-USER", "EMMC-GP1", "EMMC-GP2", "EMMC-GP3", "EMMC-GP4"]
            : ["UFS-LUA0", "UFS-LUA1", "UFS-LUA2", "UFS-LUA3"];
        var regions = new MtkXmlStorageRegion[names.Length];
        for (var index = 0; index < regions.Length; index++)
        {
            var size = ReadHex(values, sizeFields[index]);
            if (size > long.MaxValue || size % blockSize != 0)
            {
                throw new MtkProtocolException("The XML physical capacity overflows or is not block-aligned.");
            }
            // The selected XML profile reserves LUA3/RPMB for authenticated access.
            regions[index] = new MtkXmlStorageRegion(names[index], (long)size,
                kind == MtkXmlStorageKind.Emmc ? index == 2 : index == 3);
        }
        if (!regions.Any(region => region.CapacityBytes > 0 && !region.RequiresAuthenticatedAccess))
        {
            throw new MtkProtocolException("The XML geometry has no ordinary addressable region.");
        }
        var id = OptionalIdentity(values, "id");
        var familyId = OptionalIdentity(values, section + "_cid");
        return new MtkXmlStorageInfo(kind, (int)blockSize, regions, id, familyId ?? id);
    }

    private static Dictionary<string, XElement> ReadFields(XElement parent)
    {
        if (parent.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
        {
            throw new InvalidDataException("Mixed XML hardware content is unsupported.");
        }
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var field in parent.Elements())
        {
            if (result.Count >= 32 || field.Name.NamespaceName.Length != 0
                || field.Name.LocalName.Length > 128 || (!field.HasElements && field.Value.Length > 4096)
                || !result.TryAdd(field.Name.LocalName, field))
            {
                throw new InvalidDataException("Unsupported, duplicate or oversized XML hardware field.");
            }
        }
        return result;
    }

    private static string RequiredScalar(Dictionary<string, XElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value) || value.HasElements || value.Value.Length == 0)
        {
            throw new MtkProtocolException($"Missing or nonscalar XML hardware field: {name}.");
        }
        return value.Value;
    }

    private static ulong ReadHex(Dictionary<string, XElement> fields, string name)
    {
        var text = RequiredScalar(fields, name);
        if (text == "0")
        {
            return 0;
        }
        if (text.Length is < 3 or > 18 || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            throw new MtkProtocolException($"Invalid hexadecimal XML geometry field: {name}.");
        }
        return value;
    }

    private static string? OptionalIdentity(Dictionary<string, XElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var field) || field.Value.Length == 0)
        {
            return null;
        }
        var value = field.Value;
        if (value.Length > 512 || value.Length % 2 != 0 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new MtkProtocolException("Invalid XML card-identifier encoding.");
        }
        return value.ToUpperInvariant();
    }
}