using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Firmware.VendorFirmware.Pac;

/// <summary>
/// Resolves the two FDL loader entries and their RAM load addresses from an opened PAC package, entirely offline:
/// the loaders are the unique <c>FDL</c>/<c>FDL1</c> and <c>FDL2</c> entries by identifier, and the addresses come
/// from the <c>BMAConfig</c> XML carried in the package. Nothing is extracted, no device is contacted and no file
/// is written, so a rejected package never reaches the flash path.
/// <para>从一个已打开的 PAC 包中离线解析两级 FDL 加载器条目与其 RAM 加载地址：加载器是按标识唯一匹配的
/// <c>FDL</c>/<c>FDL1</c> 与 <c>FDL2</c> 条目，地址来自包内携带的 <c>BMAConfig</c> XML。整个过程不提取、不接触
/// 设备、不写文件，被拒绝的包不会进入刷写路径。</para>
/// </summary>
/// <remarks>
/// The reference ResearchDownload scheme layout is the only accepted shape: exactly one <c>SchemeList</c>, schemes
/// that carry FDL roles must present exactly one first-stage and one second-stage file, and every role file has a
/// single <c>ID</c> and a single <c>Block/Base</c> address. DTDs are prohibited, XML candidates are bounded in count
/// and size, loaders are bounded to 32 MiB, and an address plus its loader length must stay inside a 32-bit RAM
/// space. Duplicate roles, ambiguous schemes or overflowing addresses fail the whole resolution.
/// <para>只接受 ResearchDownload 参考的 scheme 布局：恰有一个 <c>SchemeList</c>，携带 FDL 角色的 scheme 必须给出
/// 恰好一个一级与一个二级文件，每个角色文件只有一个 <c>ID</c> 与一个 <c>Block/Base</c> 地址。禁止 DTD，XML 候选
/// 数量与大小有上限，加载器上限 32 MiB，地址加加载器长度不得越出 32 位 RAM 空间。重复角色、歧义 scheme 或溢出
/// 地址都会让整体解析失败。</para>
/// </remarks>
public static class SprdPacLoaders
{
    private const int MaximumXmlCandidates = 16;
    private const long MaximumXmlBytes = 2 * 1024 * 1024;
    private const long MaximumLoaderBytes = 32 * 1024 * 1024;
    private const ulong RamSpaceEnd = 0x1_0000_0000UL;

    /// <summary>Resolves the two-stage loader set of the package.
    /// <para>解析包的两级加载器集合。</para></summary>
    /// <param name="image">The opened PAC package. <para>已打开的 PAC 包。</para></param>
    /// <returns>The loader entries with their confirmed load addresses.
    /// <para>加载器条目与其确认的加载地址。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.
    /// <para><paramref name="image"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">
    /// The package has zero or duplicate <c>FDL</c>/<c>FDL1</c>/<c>FDL2</c> identifiers, carries no valid
    /// <c>BMAConfig</c> scheme with FDL roles, or any loader/address violates the documented bounds.
    /// <para>包内 <c>FDL</c>/<c>FDL1</c>/<c>FDL2</c> 标识缺失或重复、没有任何携带 FDL 角色的有效
    /// <c>BMAConfig</c> scheme，或某个加载器/地址违反文档边界。</para></exception>
    public static SprdPacLoaderSet Resolve(PacImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var firstStage = UniqueById(image, "FDL", "FDL1");
        var secondStage = UniqueById(image, "FDL2");
        var (firstAddress, secondAddress) = ResolveSchemeAddresses(image);
        ValidateLoader(firstStage, firstAddress);
        ValidateLoader(secondStage, secondAddress);
        return new SprdPacLoaderSet(firstStage, firstAddress, secondStage, secondAddress);
    }

    private static PacEntry UniqueById(PacImage image, params string[] identifiers)
    {
        PacEntry? match = null;
        foreach (var entry in image.Entries)
        {
            foreach (var identifier in identifiers)
            {
                if (!string.Equals(entry.Id, identifier, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (match is not null)
                {
                    throw new InvalidDataException($"The PAC package declares more than one '{identifier}' entry.");
                }

                match = entry;
            }
        }

        return match ?? throw new InvalidDataException(
            $"The PAC package carries no '{string.Join("'/'", identifiers)}' loader entry.");
    }

    private static (uint First, uint Second) ResolveSchemeAddresses(PacImage image)
    {
        var candidates = image.Entries
            .Where(entry => string.Equals(entry.Id, "XML", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            candidates = image.Entries
                .Where(entry => entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (candidates.Length is < 1 or > MaximumXmlCandidates)
        {
            throw new InvalidDataException(
                $"The PAC package carries {candidates.Length} XML candidates; expected 1 to {MaximumXmlCandidates}.");
        }

        (uint First, uint Second)? addresses = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Length is < 1 or > MaximumXmlBytes)
            {
                throw new InvalidDataException(
                    $"The PAC XML entry '{candidate.Name}' declares an out-of-range length of {candidate.Length} bytes.");
            }

            using var stream = image.OpenEntryStream(candidate);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes,
                IgnoreComments = true
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            if (document.Root?.Name != "BMAConfig")
            {
                continue;
            }

            var schemeLists = document.Root.Elements("SchemeList").ToArray();
            if (schemeLists.Length != 1)
            {
                throw new InvalidDataException($"The PAC XML '{candidate.Name}' must carry exactly one SchemeList.");
            }

            foreach (var scheme in schemeLists[0].Elements("Scheme"))
            {
                var files = scheme.Elements("File").ToArray();
                var first = files.Where(file => Role(file) is "FDL" or "FDL1").ToArray();
                var second = files.Where(file => Role(file) == "FDL2").ToArray();
                if (first.Length == 0 && second.Length == 0)
                {
                    continue;
                }

                if (first.Length != 1 || second.Length != 1 || addresses is not null)
                {
                    throw new InvalidDataException(
                        $"The PAC XML '{candidate.Name}' declares ambiguous FDL loader roles.");
                }

                addresses = (Address(first[0]), Address(second[0]));
            }
        }

        return addresses ?? throw new InvalidDataException(
            "No BMAConfig scheme in the PAC package declares the FDL loader addresses.");
    }

    private static string Role(XElement file)
    {
        var roles = file.Elements("ID").ToArray();
        if (roles.Length != 1 || roles[0].HasElements)
        {
            throw new InvalidDataException("A scheme File element must carry exactly one ID element.");
        }

        return roles[0].Value.Trim();
    }

    private static uint Address(XElement file)
    {
        var blocks = file.Elements("Block").ToArray();
        if (blocks.Length != 1)
        {
            throw new InvalidDataException("A scheme File element must carry exactly one Block element.");
        }

        var values = blocks[0].Elements("Base").ToArray();
        if (values.Length != 1 || values[0].HasElements)
        {
            throw new InvalidDataException("A scheme Block element must carry exactly one Base element.");
        }

        var value = values[0].Value.Trim();
        if (value.Length is < 1 or > 16)
        {
            throw new InvalidDataException($"The loader address '{value}' is not a bounded numeric literal.");
        }

        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? value[2..] : value,
                hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out var address))
        {
            throw new InvalidDataException($"The loader address '{value}' is not a 32-bit unsigned integer.");
        }

        return address;
    }

    private static void ValidateLoader(PacEntry entry, uint address)
    {
        if (entry.Length is < 1 or > MaximumLoaderBytes)
        {
            throw new InvalidDataException(
                $"The loader entry '{entry.Name}' declares an out-of-range length of {entry.Length} bytes.");
        }

        if (address + (ulong)entry.Length > RamSpaceEnd)
        {
            throw new InvalidDataException(
                $"The loader entry '{entry.Name}' at 0x{address:X8} exceeds the 32-bit RAM space.");
        }
    }
}

/// <summary>
/// The confirmed two-stage loader set of one PAC package: entry payloads are read through
/// <see cref="PacImage.OpenEntryStream"/> at upload time, never staged to disk.
/// <para>一个 PAC 包确认的两级加载器集合：条目负载在上传时经 <see cref="PacImage.OpenEntryStream"/> 读取，
/// 从不落盘暂存。</para>
/// </summary>
/// <param name="firstStage">The first-stage loader entry (<c>FDL</c>/<c>FDL1</c>).
/// <para>一级加载器条目（<c>FDL</c>/<c>FDL1</c>）。</para></param>
/// <param name="firstStageAddress">Confirmed RAM load address of the first stage.
/// <para>一级加载器确认的 RAM 加载地址。</para></param>
/// <param name="secondStage">The second-stage loader entry (<c>FDL2</c>).
/// <para>二级加载器条目（<c>FDL2</c>）。</para></param>
/// <param name="secondStageAddress">Confirmed RAM load address of the second stage.
/// <para>二级加载器确认的 RAM 加载地址。</para></param>
public sealed class SprdPacLoaderSet(
    PacEntry firstStage,
    uint firstStageAddress,
    PacEntry secondStage,
    uint secondStageAddress)
{
    /// <summary>Gets the first-stage loader entry. <para>获取一级加载器条目。</para></summary>
    public PacEntry FirstStage { get; } = firstStage;

    /// <summary>Gets the confirmed RAM load address of the first stage. <para>获取一级加载器确认的 RAM 加载地址。</para></summary>
    public uint FirstStageAddress { get; } = firstStageAddress;

    /// <summary>Gets the second-stage loader entry. <para>获取二级加载器条目。</para></summary>
    public PacEntry SecondStage { get; } = secondStage;

    /// <summary>Gets the confirmed RAM load address of the second stage. <para>获取二级加载器确认的 RAM 加载地址。</para></summary>
    public uint SecondStageAddress { get; } = secondStageAddress;
}