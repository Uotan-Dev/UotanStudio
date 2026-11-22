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
/// The PAC scheme layout is the only accepted shape: exactly one <c>SchemeList</c>, schemes
/// that carry FDL roles must present exactly one first-stage and one second-stage file, and every role file has a
/// single <c>ID</c> and a single <c>Block/Base</c> address. DTDs are prohibited, XML candidates are bounded in count
/// and size, loaders are bounded to 32 MiB, and an address plus its loader length must stay inside a 32-bit RAM
/// space. Duplicate roles, ambiguous schemes or overflowing addresses fail the whole resolution.
/// <para>只接受 PAC 包内的 scheme 布局：恰有一个 <c>SchemeList</c>，携带 FDL 角色的 scheme 必须给出
/// 恰好一个一级与一个二级文件，每个角色文件只有一个 <c>ID</c> 与一个 <c>Block/Base</c> 地址。禁止 DTD，XML 候选
/// 数量与大小有上限，加载器上限 32 MiB，地址加加载器长度不得越出 32 位 RAM 空间。重复角色、歧义 scheme 或溢出
/// 地址都会让整体解析失败。</para>
/// </remarks>
public static class SprdPacLoaders
{
    private const uint NeedsFileBit = 0x1;
    private const uint MandatoryCheckFlag = 1;
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
        var addresses = ParseScheme(image);
        ValidateLoader(firstStage, addresses.First);
        ValidateLoader(secondStage, addresses.Second);
        return new SprdPacLoaderSet(firstStage, addresses.First, secondStage, addresses.Second);
    }

    /// <summary>
    /// Resolves the official flashing sequence of the package from the same <c>BMAConfig</c> scheme that carries the
    /// loader addresses: every scheme <c>File</c> whose <c>Flag</c> has bit 0 set names a partition the official
    /// tool writes, in declaration order, and is matched to its payload entry by <c>ID</c>.
    /// <para>从携带加载器地址的同一份 <c>BMAConfig</c> scheme 中解析包的官方刷写序列：<c>Flag</c> 第 0 位被置位的每个
    /// scheme <c>File</c> 按声明顺序给出官方工具写入的分区，并按 <c>ID</c> 匹配其负载条目。</para>
    /// </summary>
    /// <remarks>
    /// The flag is read the way the official tool reads it: the reference masks the scheme flag with
    /// <c>DI_IS_NEED_FILE</c> (<c>0x1</c>), so bit 0 alone decides whether a step needs a file. A step without that
    /// bit is an operation performed without a payload (for example <c>EraseMisc</c>); those steps are reported with
    /// a null entry so callers can keep the sequence intact. The two loader roles are reported separately by
    /// <see cref="Resolve"/> and are therefore excluded here.
    /// <para>标志按官方工具的方式读取：参考实现用 <c>DI_IS_NEED_FILE</c>（<c>0x1</c>）掩码 scheme 标志，因此仅由
    /// 第 0 位决定某个步骤是否需要文件。未置该位的步骤是不携带负载的操作（例如 <c>EraseMisc</c>）；这类步骤以
    /// <see langword="null"/> 条目上报，好让调用方保持序列完整。两级加载器角色由 <see cref="Resolve"/> 单独给出，
    /// 因此在此排除。</para>
    /// </remarks>
    /// <param name="image">The opened PAC package. <para>已打开的 PAC 包。</para></param>
    /// <returns>The scheme steps in declaration order, excluding the two loader roles.
    /// <para>按声明顺序排列的 scheme 步骤，不含两级加载器角色。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.
    /// <para><paramref name="image"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The package carries no usable <c>BMAConfig</c> scheme, a step names an
    /// identifier twice, or a payload step has no matching package entry.
    /// <para>包内没有可用的 <c>BMAConfig</c> scheme、某步骤重复声明标识，或某个负载步骤在包内没有匹配条目。</para></exception>
    public static IReadOnlyList<SprdPacSchemeStep> ResolveScheme(PacImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ParseScheme(image).Steps;
    }

    private static SprdPacScheme ParseScheme(PacImage image)
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

        SprdPacScheme? parsed = null;
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

            var schemes = schemesForProduct(document, image.ProductName, candidate.Name);
            foreach (var scheme in schemes)
            {
                var files = scheme.Elements("File").ToArray();
                var first = files.Where(file => Role(file) is "FDL" or "FDL1").ToArray();
                var second = files.Where(file => Role(file) == "FDL2").ToArray();
                if (first.Length == 0 && second.Length == 0)
                {
                    continue;
                }

                if (first.Length != 1 || second.Length != 1)
                {
                    throw new InvalidDataException(
                        $"The PAC XML '{candidate.Name}' declares ambiguous FDL loader roles.");
                }

                parsed = new SprdPacScheme(
                    Address(first[0]),
                    Address(second[0]),
                    BuildSteps(image, files, candidate.Name));
                break;
            }

            if (parsed is not null)
            {
                // More than one scheme can qualify when the package name matches no product entry; the official reader
                // resolves one scheme deterministically, so the first scheme carrying both loader roles wins instead of
                // the whole package failing as ambiguous.
                // 当包名匹配不到产品条目时可能有多个 scheme 合格；官方读取器确定性地选出一个 scheme，因此以第一个
                // 同时带有两个加载器角色的 scheme 为准，而不是判定整包歧义并失败。
                break;
            }
        }

        return parsed ?? throw new InvalidDataException(
            "No BMAConfig scheme in the PAC package declares the FDL loader addresses.");
    }

    private static IEnumerable<XElement> schemesForProduct(XDocument document, string productName, string candidateName)
    {
        var schemeList = document.Root!.Elements("SchemeList").Single();
        var declared = schemeList.Elements("Scheme").ToArray();
        if (string.IsNullOrWhiteSpace(productName))
        {
            return declared;
        }

        var productLists = document.Root.Elements("ProductList").ToArray();
        if (productLists.Length != 1)
        {
            return declared;
        }

        var products = productLists[0]
            .Elements("Product")
            .Where(product => string.Equals(
                (string?)product.Attribute("name"), productName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (products.Length != 1)
        {
            return declared;
        }

        var schemeNames = products[0]
            .Elements("SchemeName")
            .Select(element => element.Value.Trim())
            .Where(name => name.Length > 0)
            .ToArray();
        if (schemeNames.Length != 1)
        {
            return declared;
        }

        var selected = declared
            .Where(scheme => string.Equals(
                (string?)scheme.Attribute("name"), schemeNames[0], StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length != 1)
        {
            throw new InvalidDataException(
                $"The PAC XML '{candidateName}' does not declare the product scheme '{schemeNames[0]}' exactly once.");
        }

        return selected;
    }

    private static IReadOnlyList<SprdPacSchemeStep> BuildSteps(PacImage image, XElement[] files, string candidateName)
    {
        var steps = new List<SprdPacSchemeStep>(files.Length);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var identifier = Role(file);
            if (identifier is "FDL" or "FDL1" or "FDL2")
            {
                continue;
            }

            if (!identifiers.Add(identifier))
            {
                throw new InvalidDataException(
                    $"The PAC XML '{candidateName}' declares the scheme step '{identifier}' twice.");
            }

            if (!TryReadFlag(file, out var carriesPayload))
            {
                throw new InvalidDataException(
                    $"The scheme step '{identifier}' must carry exactly one numeric Flag element.");
            }

            if (!TryReadCheckFlag(file, out var mandatory))
            {
                throw new InvalidDataException(
                    $"The scheme step '{identifier}' must carry exactly one numeric CheckFlag element.");
            }

            var partition = BlockIdentifier(file);
            var operation = OperationType(file);
            if (carriesPayload)
            {
                var payload = UniquePayload(image, identifier);
                steps.Add(new SprdPacSchemeStep(identifier, partition, payload, mandatory, operation));
            }
            else
            {
                steps.Add(new SprdPacSchemeStep(identifier, partition, null, mandatory, operation));
            }
        }

        return steps.AsReadOnly();
    }

    private static PacEntry UniquePayload(PacImage image, string identifier)
    {
        PacEntry? match = null;
        foreach (var entry in image.Entries)
        {
            if (entry.Length <= 0 || !string.Equals(entry.Id, identifier, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidDataException(
                    $"The PAC package declares more than one payload entry for the scheme step '{identifier}'.");
            }

            match = entry;
        }

        return match ?? throw new InvalidDataException(
            $"The scheme step '{identifier}' has no matching payload entry in the package.");
    }

    private static bool TryReadFlag(XElement file, out bool carriesPayload)
    {
        var flags = file.Elements("Flag").ToArray();
        carriesPayload = false;
        if (flags.Length != 1 || flags[0].HasElements)
        {
            return false;
        }

        if (!uint.TryParse(flags[0].Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var flag))
        {
            return false;
        }

        carriesPayload = (flag & NeedsFileBit) != 0;
        return true;
    }

    private static bool TryReadCheckFlag(XElement file, out bool mandatory)
    {
        mandatory = false;
        var flags = file.Elements("CheckFlag").ToArray();
        if (flags.Length == 0)
        {
            // The official reader takes the flag as it finds it, so a scheme that omits CheckFlag simply yields the
            // zero default instead of failing the whole package.
            // 官方读取器按实际内容取值，因此省略 CheckFlag 的 scheme 只得到零默认值，不会让整包解析失败。
            return true;
        }

        if (flags.Length != 1 || flags[0].HasElements)
        {
            return false;
        }

        if (!uint.TryParse(flags[0].Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var flag))
        {
            return false;
        }

        mandatory = flag == MandatoryCheckFlag;
        return true;
    }

    private static string OperationType(XElement file)
    {
        var types = file.Elements("Type").ToArray();
        return types.Length == 1 && !types[0].HasElements ? types[0].Value.Trim() : string.Empty;
    }

    private static string BlockIdentifier(XElement file)
    {
        var blocks = file.Elements("Block").ToArray();
        if (blocks.Length != 1)
        {
            return string.Empty;
        }

        var target = blocks[0].Attribute("id");
        return target is null ? string.Empty : target.Value.Trim();
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

/// <summary>
/// One step of the official flashing sequence declared by the package scheme.
/// <para>包内 scheme 声明的官方刷写序列中的一个步骤。</para>
/// </summary>
/// <param name="identifier">The scheme <c>ID</c> of the step. <para>该步骤的 scheme <c>ID</c>。</para></param>
/// <param name="partition">The block identifier the step targets, as declared by the scheme <c>Block</c>.
/// <para>该步骤指向的块标识，来自 scheme 的 <c>Block</c> 声明。</para></param>
/// <param name="entry">The payload entry the step writes, or <see langword="null"/> for an erase-style step that
/// runs without a payload. <para>该步骤写入的负载条目；不含负载的擦除类步骤为 <see langword="null"/>。</para></param>
/// <param name="mandatory">Whether the scheme <c>CheckFlag</c> marks this step as one that must stay selected.
/// <para>scheme 的 <c>CheckFlag</c> 是否把该步骤标记为必须保持勾选。</para></param>
/// <param name="operation">The scheme <c>Type</c> of the step, such as <c>EraseFlash2</c> or <c>CODE2</c>, or an empty
/// string when the scheme declares none. <para>该步骤的 scheme <c>Type</c>，例如 <c>EraseFlash2</c> 或 <c>CODE2</c>；
/// scheme 未声明时为<see cref="string.Empty"/>。</para></param>
public sealed class SprdPacSchemeStep(
    string identifier,
    string partition,
    PacEntry? entry,
    bool mandatory,
    string operation)
{
    /// <summary>Gets the scheme identifier of the step. <para>获取该步骤的 scheme 标识。</para></summary>
    public string Identifier { get; } = identifier;

    /// <summary>Gets the block identifier the step targets. <para>获取该步骤指向的块标识。</para></summary>
    public string Partition { get; } = partition;

    /// <summary>Gets the payload entry the step writes, or <see langword="null"/> when the step carries no payload.
    /// <para>获取该步骤写入的负载条目；步骤不含负载时为 <see langword="null"/>。</para></summary>
    public PacEntry? Entry { get; } = entry;

    /// <summary>Gets whether the step writes a payload rather than performing a payload-free operation.
    /// <para>获取该步骤是否写入负载，而不是执行无负载操作。</para></summary>
    public bool CarriesPayload => Entry is not null;

    /// <summary>Gets whether the scheme marks this step as mandatory. The official tool ticks every row in the product
    /// list and treats <c>CheckFlag</c> of <c>1</c> as "must stay selected", so a mandatory step cannot be cleared by
    /// the select-all action while <c>0</c> and <c>2</c> can.
    /// <para>获取 scheme 是否把该步骤标记为必须执行。官方工具会勾选产品列表的每一行，并把 <c>CheckFlag</c> 为 <c>1</c> 视为
    /// “必须保持勾选”，因此必须执行的步骤不会被全选动作取消，而 <c>0</c> 与 <c>2</c> 可以。</para></summary>
    public bool IsMandatory { get; } = mandatory;

    /// <summary>Gets the scheme <c>Type</c> of the step, or an empty string when the scheme declares none.
    /// <para>获取该步骤的 scheme <c>Type</c>；scheme 未声明时为<see cref="string.Empty"/>。</para></summary>
    public string Operation { get; } = operation;

    /// <summary>Gets whether the step erases storage instead of writing a payload. The official tool dispatches the
    /// scheme <c>Type</c> through its <c>EraseFlash</c>/<c>EraseFlash2</c>/<c>EraseFlashByID</c> handlers, which destroy
    /// partition contents, so a payload-free step of any other type is an inspection or bookkeeping operation rather
    /// than an erase. <para>获取该步骤是否擦除存储而不是写入负载。官方工具把 scheme <c>Type</c> 分派到
    /// <c>EraseFlash</c>/<c>EraseFlash2</c>/<c>EraseFlashByID</c> 处理器，这些处理器会销毁分区内容，因此其它类型的
    /// 无负载步骤属于检查或记录操作而非擦除。</para></summary>
    public bool IsErase => Operation.StartsWith("EraseFlash", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The parsed scheme of one PAC package: the two loader addresses and the official flashing sequence, produced from a
/// single pass over the <c>BMAConfig</c> document so the loaders and the sequence can never disagree.
/// <para>一个 PAC 包解析出的 scheme：两级加载器地址与官方刷写序列，由对 <c>BMAConfig</c> 文档的单次解析产出，
/// 因此加载器与该序列不可能互相矛盾。</para>
/// </summary>
internal sealed class SprdPacScheme(uint first, uint second, IReadOnlyList<SprdPacSchemeStep> steps)
{
    internal uint First { get; } = first;

    internal uint Second { get; } = second;

    internal IReadOnlyList<SprdPacSchemeStep> Steps { get; } = steps;
}