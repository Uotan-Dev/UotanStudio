using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Block-storage families described by XML DA hardware information.
/// <para>由 XML DA 硬件信息描述的块存储家族。</para></summary>
public enum MtkXmlStorageKind
{
    /// <summary>Embedded MultiMediaCard storage.
    /// <para>嵌入式多媒体卡（eMMC）存储。</para></summary>
    Emmc,
    /// <summary>Universal Flash Storage with XML LUA identifiers, not XFlash partition numbers.
    /// <para>使用 XML LUA 标识而非 XFlash 分区号的通用闪存（UFS）存储。</para></summary>
    Ufs
}

/// <summary>A device-reported XML physical region, not a logical GPT partition.
/// <para>设备上报的 XML 物理区域，并非逻辑 GPT 分区。</para></summary>
public sealed class MtkXmlStorageRegion
{
    internal MtkXmlStorageRegion(string name, long capacityBytes, bool authenticated)
    {
        Name = name;
        CapacityBytes = capacityBytes;
        RequiresAuthenticatedAccess = authenticated;
    }

    /// <summary>Gets the exact XML physical-region token.
    /// <para>获取精确的 XML 物理区域标记。</para></summary>
    /// <value>The canonical region name derived from the storage family, such as <c>EMMC-USER</c>.
    /// <para>由存储家族推导的规范区域名称，例如 <c>EMMC-USER</c>。</para></value>
    public string Name { get; }
    /// <summary>Gets the reported byte capacity.
    /// <para>获取上报的字节容量。</para></summary>
    /// <value>The region size in bytes as parsed from the hex XML field; may be 0.
    /// <para>从十六进制 XML 字段解析的区域字节大小；可能为 0。</para></value>
    public long CapacityBytes { get; }
    /// <summary>Gets whether this profile reserves the region for a separate authenticated protocol.
    /// <para>获取此配置是否将该区域保留给单独的已认证协议。</para></summary>
    /// <value>
    /// <see langword="true"/> for the RPMB region under eMMC and the last LUA region under UFS; ordinary
    /// range validation rejects these regions.
    /// <para>eMMC 下的 RPMB 区域与 UFS 下的最后一个 LUA 区域为 <see langword="true"/>；常规范围校验会拒绝这些区域。</para></value>
    public bool RequiresAuthenticatedAccess { get; }
}

/// <summary>Immutable, bounded XML hardware geometry; neither authentication nor successful storage I/O is implied.
/// <para>不可变、有长度上限的 XML 硬件几何信息；既不意味着已通过认证，也不意味着存储 I/O 已成功。</para></summary>
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

    /// <summary>Gets the reported block-storage family.
    /// <para>获取上报的块存储家族。</para></summary>
    /// <value>
    /// <see cref="MtkXmlStorageKind.Emmc"/> or <see cref="MtkXmlStorageKind.Ufs"/> as parsed from the
    /// <c>storage</c> field.
    /// <para>由 <c>storage</c> 字段解析得到的 <see cref="MtkXmlStorageKind.Emmc"/> 或
    /// <see cref="MtkXmlStorageKind.Ufs"/>。</para></value>
    public MtkXmlStorageKind Kind { get; }
    /// <summary>Gets the reported logical block size in bytes.
    /// <para>获取上报的逻辑块大小（字节）。</para></summary>
    /// <value>A power-of-two block size between 512 and 65536 inclusive, used for alignment checks.
    /// <para>介于 512 与 65536（含）之间的 2 的幂块大小，用于对齐检查。</para></value>
    public int BlockSize { get; }
    /// <summary>Gets physical regions, including reported zero-capacity and authenticated regions.
    /// <para>获取物理区域，包括上报容量为 0 的区域和需认证的区域。</para></summary>
    /// <value>A read-only list of the family's regions in fixed report order.
    /// <para>按固定上报顺序排列的该家族区域只读列表。</para></value>
    public IReadOnlyList<MtkXmlStorageRegion> Regions { get; }
    /// <summary>Gets the optional id field, which need not equal a separate family-specific CID.
    /// <para>获取可选的 id 字段，它未必等于按家族区分的 CID。</para></summary>
    /// <value>The upper-case hex <c>id</c> text, or <see langword="null"/> when absent or empty.
    /// <para>大写的十六进制 <c>id</c> 文本；缺失或为空时为 <see langword="null"/>。</para></value>
    public string? ReportedIdHex { get; }
    /// <summary>Gets the family-specific CID when present, otherwise the optional id field.
    /// Neither value is an authenticated device identity.
    /// <para>存在时获取按家族区分的 CID，否则获取可选的 id 字段。两者都不是经过认证的设备标识。</para></summary>
    /// <value>The upper-case hex <c>&lt;section&gt;_cid</c> text when present, otherwise
    /// <see cref="ReportedIdHex"/>; <see langword="null"/> when neither is available.
    /// <para>存在时为大写的十六进制 <c>&lt;section&gt;_cid</c> 文本，否则为 <see cref="ReportedIdHex"/>；
    /// 两者都没有时为 <see langword="null"/>。</para></value>
    public string? CidHex { get; }

    /// <summary>Gets the canonical region token that carries the file-system partition table for this family.
    /// <para>获取该家族中承载文件系统分区表的规范区域标记。</para></summary>
    /// <value>
    /// <c>EMMC-USER</c> for eMMC and <c>UFS-LUA2</c> for UFS, mirroring the tokens the flashing tool sends for
    /// the "user" partition of each family. The region is present in <see cref="Regions"/> only when the device
    /// reported it, so callers still select a region through range validation.
    /// <para>eMMC 为 <c>EMMC-USER</c>，UFS 为 <c>UFS-LUA2</c>，与该工具为各家族 “user” 分区发送的标记一致。
    /// 仅当设备上报该区域时它才出现在 <see cref="Regions"/> 中，因此调用方仍需通过范围校验选择区域。</para>
    /// </value>
    public string UserDataRegionName => GetUserDataRegionName(Kind);

    /// <summary>Gets the canonical user-data region token for a block-storage family.
    /// <para>获取块存储家族对应的规范用户数据区域标记。</para></summary>
    /// <param name="kind">The storage family. <para>存储家族。</para></param>
    /// <returns><c>EMMC-USER</c> or <c>UFS-LUA2</c>. <para><c>EMMC-USER</c> 或 <c>UFS-LUA2</c>。</para></returns>
    public static string GetUserDataRegionName(MtkXmlStorageKind kind) =>
        kind == MtkXmlStorageKind.Emmc ? "EMMC-USER" : "UFS-LUA2";

    /// <summary>Validates ordinary block access without clamping, rounding or permitting authenticated regions.
    /// <para>校验常规块访问，不进行截断或取整，也不允许访问需认证的区域。</para></summary>
    /// <param name="regionName">An exact XML region token from this geometry.
    /// <para>来自本几何信息的精确 XML 区域标记。</para></param>
    /// <param name="offset">Nonnegative block-aligned byte offset.<para>非负、按块对齐的字节偏移。</para></param>
    /// <param name="length">Positive block-aligned byte count.<para>为正、按块对齐的字节数。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="regionName"/> is <see langword="null"/>.
    /// <para><paramref name="regionName"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, <paramref name="length"/> is not positive, the region was not reported, or the range is unaligned or exceeds reported capacity.
    /// <para><paramref name="offset"/> 为负、<paramref name="length"/> 非正、区域未上报，或范围未对齐或超出上报容量。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="regionName"/> names a region reserved for a separate authenticated protocol.
    /// <para><paramref name="regionName"/> 指向为单独已认证协议保留的区域。</para></exception>
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