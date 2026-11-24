using System.Buffers.Binary;
using System.Globalization;
using System.Text;
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

    /// <summary>
    /// Resolves the official flashing sequence together with the storage medium the package declares, from the same
    /// single parse of the <c>BMAConfig</c> document.
    /// <para>从对 <c>BMAConfig</c> 文档的同一次解析中，同时解析官方刷写序列与包声明的存储介质。</para>
    /// </summary>
    /// <param name="image">The opened PAC package. <para>已打开的 PAC 包。</para></param>
    /// <returns>The steps in declaration order and the <c>Product/FlashTypeID</c> value the scheme declares, or
    /// <c>0</c> when it declares none, which the official XML header documents as Nor flash.
    /// <para>按声明顺序的步骤，以及 scheme 声明的 <c>Product/FlashTypeID</c> 值；未声明时为 <c>0</c>，官方 XML 头注释
    /// 将其记为 Nor flash。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.
    /// <para><paramref name="image"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The package carries no usable <c>BMAConfig</c> scheme, a step names an
    /// identifier twice, or a payload step has no matching package entry.
    /// <para>包内没有可用的 <c>BMAConfig</c> scheme、某步骤重复声明标识，或某个负载步骤在包内没有匹配条目。</para></exception>
    public static SprdPacSchemeLayout ResolveLayout(PacImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var scheme = ParseScheme(image);
        return new SprdPacSchemeLayout(scheme.Steps, scheme.FlashType, scheme.Partitions, scheme.ExtTable);
    }

    private static byte[]? BuildExtTable(XDocument document, string productName)
    {
        // The official tool serializes the ExtTable as: 32-bit table count, one 12-byte header per table
        // (4-byte tag, 32-bit data offset, 32-bit data size), then each table's data records.
        var product = document.Root?.Elements("ProductList").Elements("Product")
            .FirstOrDefault(p => string.Equals((string?)p.Attribute("name"), productName, StringComparison.OrdinalIgnoreCase));
        var ext = product?.Element("ExtTable");
        if (ext is null)
        {
            return null;
        }

        var groups = ext.Elements("PartitionType").ToArray();
        if (groups.Length == 0 || groups.Length > 16)
        {
            throw new InvalidDataException("The PAC ExtTable must declare 1 to 16 partition-type tables.");
        }

        const int headerSize = 12;
        const int recordSize = 152; // 2 * 36 UTF-16 code units + DWORD size + DWORD type = 152 bytes
        var headers = new byte[groups.Length * headerSize];
        var records = new List<byte>();
        var dataOffset = 4 + headers.Length;
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var tag = ((string?)group.Attribute("tag") ?? string.Empty).Trim();
            var tagBytes = Encoding.ASCII.GetBytes(tag);
            Array.Copy(tagBytes, 0, headers, i * headerSize, Math.Min(4, tagBytes.Length));
            var parts = group.Elements("Partition").ToArray();
            if (parts.Length > 4096)
            {
                throw new InvalidDataException("A PAC ExtTable type declares too many partitions.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(i * headerSize + 4), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(i * headerSize + 8), (uint)(parts.Length * recordSize));
            foreach (var part in parts)
            {
                var record = new byte[recordSize];
                WriteExtName(record.AsSpan(0, 72), (string?)part.Attribute("id"));
                WriteExtName(record.AsSpan(72, 72), (string?)part.Attribute("id2"));
                var rawSize = ((string?)part.Attribute("size"))?.Trim() ?? string.Empty;
                uint size = 0;
                if (rawSize.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    uint.TryParse(rawSize[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size);
                }
                else if (rawSize.Length > 0)
                {
                    uint.TryParse(rawSize, NumberStyles.None, CultureInfo.InvariantCulture, out size);
                }

                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(144), size);
                var rawType = ((string?)part.Attribute("type"))?.Trim() ?? string.Empty;
                record[148] = rawType.Length > 0 && char.IsDigit(rawType[0]) ? (byte)(rawType[0] - '0') : (byte)0;
                records.AddRange(record);
            }

            dataOffset += parts.Length * recordSize;
        }

        var table = new byte[dataOffset];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)groups.Length);
        headers.CopyTo(table, 4);
        records.CopyTo(table, 4 + headers.Length);
        return table;
    }

    private static void WriteExtName(Span<byte> target, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var limit = Math.Min(36, value.Length);
        for (var i = 0; i < limit; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(i * 2, 2), value[i]);
        }
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
                    BuildSteps(image, files, candidate.Name),
                    FlashTypeOf(document, image.ProductName),
                    PartitionsOf(document, image.ProductName),
                    BuildExtTable(document, image.ProductName));
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

    /// <summary>
    /// Reads the partition table the product declares and derives each partition's offset by accumulating the declared
    /// sizes in declaration order, which is the layout the official tool rebuilds from this table. The table carries no
    /// offset field — <c>Partition</c> has only <c>id</c> and <c>size</c> — so the accumulation is the only way to
    /// express a position from package data alone; a size of <c>0xFFFFFFFF</c> or <c>-1</c> means "the rest of the
    /// device", whose end only the device itself knows, so that partition and everything after it get no derivable end.
    /// <para>读取产品声明的分区表，并按声明顺序累加声明大小推导每个分区的偏移，这正是官方工具据以重建的布局。该表没有
    /// 偏移字段——<c>Partition</c> 只有 <c>id</c> 与 <c>size</c>——因此累加是仅凭包内数据表达位置的唯一方式；
    /// <c>0xFFFFFFFF</c> 或 <c>-1</c> 表示「设备剩余空间」，其终点只有设备自己知道，因此该分区及其之后的分区没有可
    /// 推导的终点。</para>
    /// </summary>
    /// <param name="document">The parsed <c>BMAConfig</c> document. <para>已解析的 <c>BMAConfig</c> 文档。</para></param>
    /// <param name="productName">The product the package names. <para>包所命名的产品。</para></param>
    /// <returns>The declared partitions in order, each with its derived offset in bytes; empty when the package
    /// declares no table. <para>按顺序声明的分区，各带推导出的字节偏移；包未声明该表时为空。</para></returns>
    private static IReadOnlyList<SprdPacPartition> PartitionsOf(XDocument document, string productName)
    {
        var productLists = document.Root?.Elements("ProductList").ToArray() ?? [];
        if (productLists.Length != 1)
        {
            return [];
        }

        var declared = productLists[0].Elements("Product").ToArray();
        var products = declared
            .Where(product => string.Equals(
                (string?)product.Attribute("name"), productName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (products.Length != 1 && declared.Length == 1)
        {
            products = declared;
        }

        if (products.Length != 1)
        {
            return [];
        }

        var tables = products[0].Elements("Partitions").ToArray();
        if (tables.Length != 1)
        {
            return [];
        }

        var unit = PartitionUnitBytes(tables[0]);
        List<SprdPacPartition> partitions = [];
        long offset = 0;
        var unbounded = false;
        foreach (var element in tables[0].Elements("Partition"))
        {
            var id = ((string?)element.Attribute("id"))?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                continue;
            }

            var raw = ((string?)element.Attribute("size"))?.Trim() ?? string.Empty;
            var fill = raw.Equals("0xFFFFFFFF", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("-1", StringComparison.Ordinal);
            var size = fill ? 0 : checked(ParsePartitionSize(raw) * unit);
            partitions.Add(new SprdPacPartition(id, unbounded || fill ? null : offset, size, fill));
            if (fill)
            {
                unbounded = true;
                continue;
            }

            offset = checked(offset + size);
        }

        return partitions.AsReadOnly();
    }

    private static long PartitionUnitBytes(XElement table)
    {
        var values = table.Attribute("unit");
        if (values is null || !int.TryParse(values.Value.Trim(), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var unit))
        {
            // Real vendor PACs omit the attribute and declare "size unit is MBytes" in the XML comment, so a
            // missing or unparseable unit is read as 1 MiB. This table is display-only; the flash pass addresses
            // partitions by name, so this choice never changes what is written to the device.
            // 真实厂商 PAC 省略该属性并在 XML 注释里声明「size unit is MBytes」，因此缺失或无法解析时按 1 MiB 读取。
            // 该表仅用于显示；刷写按分区名寻址，所以这一选择不会改变写入设备的内容。
            return 1024 * 1024;
        }

        return unit switch
        {
            0 => 1024 * 1024,
            1 => 512 * 1024,
            2 => 1024,
            _ => 1
        };
    }

    private static long ParsePartitionSize(string raw)
    {
        if (raw.Length is < 1 or > 18)
        {
            return 0;
        }

        var hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? raw[2..] : raw,
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out var size)
            ? size
            : 0;
    }

    private static int FlashTypeOf(XDocument document, string productName)
    {
        var productLists = document.Root?.Elements("ProductList").ToArray() ?? [];
        if (productLists.Length != 1)
        {
            return 0;
        }

        var declared = productLists[0].Elements("Product").ToArray();

        // The product named by the package header is the one the official tool reads, but a package whose name matches
        // no product entry still declares exactly one medium through its single product element, so that value is used
        // instead of reporting an unknown medium.
        // 官方工具读取由包头命名的产品；但包名匹配不到产品条目时，其唯一的产品元素仍然声明了一个介质，因此用该值而
        // 不是上报未知介质。
        var products = declared
            .Where(product => string.Equals(
                (string?)product.Attribute("name"), productName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (products.Length != 1 && declared.Length == 1)
        {
            products = declared;
        }

        if (products.Length != 1)
        {
            return 0;
        }

        var values = products[0].Elements("FlashTypeID").ToArray();
        if (values.Length != 1 || values[0].HasElements)
        {
            return 0;
        }

        return int.TryParse(values[0].Value.Trim(), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var flashType)
            ? flashType
            : 0;
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
            var alias = IdentifierAlias(file);
            var blockBase = BlockValue(file, "Base");
            var blockSize = BlockValue(file, "Size");
            var payload = carriesPayload ? UniquePayload(image, identifier) : null;
            steps.Add(new SprdPacSchemeStep(
                identifier, partition, payload, mandatory, operation, alias, blockBase, blockSize));
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

    private static long BlockValue(XElement file, string name)
    {
        var blocks = file.Elements("Block").ToArray();
        if (blocks.Length != 1)
        {
            return 0;
        }

        var values = blocks[0].Elements(name).ToArray();
        if (values.Length != 1 || values[0].HasElements)
        {
            return 0;
        }

        var value = values[0].Value.Trim();
        if (value.Length is < 1 or > 18)
        {
            return 0;
        }

        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? value[2..] : value,
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static string IdentifierAlias(XElement file)
    {
        var aliases = file.Elements("IDAlias").ToArray();
        return aliases.Length == 1 && !aliases[0].HasElements ? aliases[0].Value.Trim() : string.Empty;
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
/// <param name="alias">The scheme <c>IDAlias</c>, which is the label the official product list shows, or an empty
/// string when the scheme declares none. <para>scheme 的 <c>IDAlias</c>，即官方产品列表显示的标签；scheme 未声明时
/// 为空字符串。</para></param>
/// <param name="blockBase">The <c>Block/Base</c> offset the scheme declares, or <c>0</c> when it declares none.
/// <para>scheme 声明的 <c>Block/Base</c> 偏移；未声明时为 <c>0</c>。</para></param>
/// <param name="blockSize">The <c>Block/Size</c> the scheme declares, or <c>0</c> when it declares none or declares
/// <c>0x0</c>. <para>scheme 声明的 <c>Block/Size</c>；未声明或声明为 <c>0x0</c> 时为 <c>0</c>。</para></param>
public sealed class SprdPacSchemeStep(
    string identifier,
    string partition,
    PacEntry? entry,
    bool mandatory,
    string operation,
    string alias = "",
    long blockBase = 0,
    long blockSize = 0)
{
    /// <summary>Gets the scheme <c>IDAlias</c> of the step, which is the label the official product list shows, or an
    /// empty string when the scheme declares none. <para>获取该步骤的 scheme <c>IDAlias</c>，即官方产品列表显示的标签；
    /// scheme 未声明时为空字符串。</para></summary>
    public string Alias { get; } = alias;

    /// <summary>Gets the <c>Block/Base</c> the scheme declares for this step, or <c>0</c> when it declares none.
    /// <para>获取 scheme 为该步骤声明的 <c>Block/Base</c>；未声明时为 <c>0</c>。</para></summary>
    public long BlockBase { get; } = blockBase;

    /// <summary>Gets the <c>Block/Size</c> the scheme declares for this step, or <c>0</c> when it declares none.
    /// <para>获取 scheme 为该步骤声明的 <c>Block/Size</c>；未声明时为 <c>0</c>。</para></summary>
    public long BlockSize { get; } = blockSize;

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
internal sealed class SprdPacScheme(
    uint first,
    uint second,
    IReadOnlyList<SprdPacSchemeStep> steps,
    int flashType = 0,
    IReadOnlyList<SprdPacPartition>? partitions = null,
    byte[]? extTable = null)
{
    internal uint First { get; } = first;

    internal uint Second { get; } = second;

    internal IReadOnlyList<SprdPacSchemeStep> Steps { get; } = steps;

    internal int FlashType { get; } = flashType;

    internal IReadOnlyList<SprdPacPartition> Partitions { get; } = partitions ?? [];

    internal byte[]? ExtTable { get; } = extTable;
}
/// <summary>
/// The layout one PAC scheme declares for the main grid: the official flashing sequence plus the storage medium the
/// product entry names.
/// <para>一份 PAC scheme 为主网格声明的布局：官方刷写序列，以及产品条目给出的存储介质。</para>
/// </summary>
/// <param name="steps">The scheme steps in declaration order, excluding the two loader roles.
/// <para>按声明顺序的 scheme 步骤，不含两级加载器角色。</para></param>
/// <param name="flashType">The declared <c>FlashTypeID</c>; <c>0</c> is Nor flash and <c>1</c> is Nand flash per the
/// official XML header, and any other value is reported as declared without interpretation.
/// <para>声明的 <c>FlashTypeID</c>；按官方 XML 头注释 <c>0</c> 为 Nor flash、<c>1</c> 为 Nand flash，其它取值按原样上报
/// 而不作解释。</para></param>
/// <param name="partitions">The partitions the product declares in order, each with the offset derived from the
/// declared sizes, or <see langword="null"/> when the package declares no table.
/// <para>产品按顺序声明的分区，各带按声明大小推导出的偏移；包未声明该表时为 <see langword="null"/>。</para></param>
/// <param name="extTable">Serialized vendor ExtTable from the product's <c>ExtTable/PartitionType</c> declaration,
/// or <see langword="null"/> when none is declared.
/// <para>产品 <c>ExtTable/PartitionType</c> 声明序列化后的厂商 ExtTable；未声明时为 <see langword="null"/>。</para></param>
public sealed class SprdPacSchemeLayout(
    IReadOnlyList<SprdPacSchemeStep> steps,
    int flashType,
    IReadOnlyList<SprdPacPartition>? partitions = null,
    byte[]? extTable = null)
{
    /// <summary>Gets the partitions the product declares, in declaration order, each carrying the offset derived by
    /// accumulating the declared sizes; empty when the package declares no partition table.
    /// <para>获取产品声明的分区，按声明顺序排列，各带按声明大小累加推导出的偏移；包未声明分区表时为空。</para></summary>
    public IReadOnlyList<SprdPacPartition> Partitions { get; } = partitions ?? [];

    /// <summary>Gets the scheme steps in declaration order. <para>获取按声明顺序的 scheme 步骤。</para></summary>
    public IReadOnlyList<SprdPacSchemeStep> Steps { get; } = steps;

    /// <summary>Gets the declared <c>FlashTypeID</c> value. <para>获取声明的 <c>FlashTypeID</c> 值。</para></summary>
    public int FlashType { get; } = flashType;

    /// <summary>Gets the serialized ExtTable from the product's <c>ExtTable/PartitionType</c> declaration, or
    /// <see langword="null"/> when the package declares none. The bytes follow the official layout: a 32-bit
    /// table count, one 12-byte header per table, then the partition records.
    /// <para>获取产品 <c>ExtTable/PartitionType</c> 声明序列化后的 ExtTable；包未声明时为 <see langword="null"/>。字节
    /// 采用官方布局：32 位表计数、每表一个 12 字节头、随后是分区记录。</para></summary>
    public byte[]? ExtTable { get; } = extTable;
}
/// <summary>
/// One partition of the table a PAC product declares: its identifier, the size the package states, and the offset
/// derived by accumulating the sizes declared before it.
/// <para>PAC 产品声明的一个分区：标识、包声明的大小，以及累加此前声明大小得到的偏移。</para>
/// </summary>
/// <param name="id">The partition identifier. <para>分区标识。</para></param>
/// <param name="offset">The derived byte offset, or <see langword="null"/> when a preceding partition declared
/// "the rest of the device" so no position can be derived from package data. <para>推导出的字节偏移；当某个前置分区声明
/// 了「设备剩余空间」而无法仅凭包内数据推导位置时为 <see langword="null"/>。</para></param>
/// <param name="size">The declared size in bytes, or <c>0</c> for the partition that fills the rest of the device.
/// <para>声明的字节大小；占满设备剩余空间的分区为 <c>0</c>。</para></param>
/// <param name="fillsRemainingSpace">Whether the package declares this partition as the rest of the device through
/// <c>0xFFFFFFFF</c> or <c>-1</c>. <para>包是否以 <c>0xFFFFFFFF</c> 或 <c>-1</c> 把该分区声明为设备剩余空间。</para></param>
public sealed record SprdPacPartition(string id, long? offset, long size, bool fillsRemainingSpace);