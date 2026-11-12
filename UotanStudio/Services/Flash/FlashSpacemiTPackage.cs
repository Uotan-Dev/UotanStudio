using UotanStudio.Comm.Protocols.SpacemiT;

namespace UotanStudio.Services.Flash;

/// <summary>
/// One partition row of a SpacemiT flash package, attributed to the storage media whose layout tables list it.
/// <para>SpacemiT 烧录包中的一个分区行，并标注把它列入布局的分区表所属的存储介质。</para>
/// </summary>
/// <param name="Partition">Partition name from the table. <para>分区表中的分区名。</para></param>
/// <param name="FilePath">Resolved image file path, or <see langword="null"/> when the package carries no such
/// file. <para>解析出的镜像文件路径；包内不存在该文件时为 <see langword="null"/>。</para></param>
/// <param name="ImageReference">Raw image reference text from the table, empty when the entry declares none.
/// <para>分区表中的原始镜像引用文本；条目未声明时为空。</para></param>
/// <param name="FileLength">Resolved image length in bytes; 0 when unresolved.
/// <para>解析出的镜像字节数；未解析时为 0。</para></param>
/// <param name="OffsetBytes">Partition offset in bytes; negative when the table omits it.
/// <para>分区偏移字节数；分区表未给出时为负。</para></param>
/// <param name="SizeBytes">Partition size in bytes; negative when the table leaves it open-ended.
/// <para>分区大小字节数；分区表声明"直到存储末尾"时为负。</para></param>
/// <param name="TableFormats">Formats of every layout table that lists this partition, in load order.
/// <para>列出该分区的每张布局表的格式，按加载顺序排列。</para></param>
public sealed record SpacemiTPackagePartition(
    string Partition,
    string? FilePath,
    string ImageReference,
    long FileLength,
    long OffsetBytes,
    long SizeBytes,
    IReadOnlyList<string> TableFormats)
{
    /// <summary>
    /// The storage media this row is written to, derived from <see cref="TableFormats"/>. A <c>gpt</c> table is a
    /// block-device layout, and the package does not say which block device the board reports, so it reads
    /// <c>eMMC/SD</c> rather than naming one; an <c>mtd</c> table is the SPI NOR layout and reads <c>SPI NOR</c>.
    /// Both are joined when the package writes the partition to both, and the cell stays empty when no layout table
    /// places it — a blank cell states "not known" where a filled one would state a guess.
    /// <para>该行被写入的存储介质，由 <see cref="TableFormats"/> 推导。<c>gpt</c> 表是块设备布局，而包本身不说明板卡上报的
    /// 是哪一种块设备，因此显示 <c>eMMC/SD</c> 而不指定其一；<c>mtd</c> 表是 SPI NOR 布局，显示 <c>SPI NOR</c>。包把该分区
    /// 写入两者时并列显示；没有任何布局表收录它时保持为空——空格表示“未知”，填上则是在陈述一个猜测。</para>
    /// </summary>
    /// <remarks>
    /// The medium answers "where does this row's write go", and every row a layout table places does get written:
    /// an entry with an image flashes that package file, an entry without one flashes the file picked in its own
    /// cell (the flash run carries host-picked images for exactly those partitions), and a regex reference is
    /// resolved to a package file before it is written.
    /// <para>介质回答的是“该行的写入落在哪里”，而布局表收录的每一行都会被写入：有镜像的条目刷包内文件；没有镜像的
    /// 条目刷其文件列选择的文件（烧录运行恰好为这些分区携带宿主选择的镜像）；正则引用在写入前解析为包内文件。</para>
    /// </remarks>
    public string StorageMedium
    {
        get
        {
            var block = TableFormats.Contains(SpacemiTConstants.PartitionFormatGpt, StringComparer.Ordinal);
            var spi = TableFormats.Contains(SpacemiTConstants.PartitionFormatMtd, StringComparer.Ordinal);
            return (block, spi) switch
            {
                (true, true) => "eMMC/SD + SPI NOR",
                (true, false) => "eMMC/SD",
                (false, true) => "SPI NOR",
                _ => string.Empty
            };
        }
    }

    /// <summary>
    /// Whether the entry names a concrete package file rather than nothing or a vendor regex marker.
    /// <para>判断条目是否给出具体的包内文件，而不是留空或只给厂商正则标记。</para>
    /// </summary>
    /// <param name="reference">Raw image reference from the table. <para>分区表中的原始镜像引用。</para></param>
    /// <returns><see langword="true"/> when the reference names a file. <para>引用指向文件时为 <see langword="true"/>。</para></returns>
    internal static bool NamesConcreteImage(string reference) =>
        reference.Length > 0 && !FlashSpacemiTPackage.IsRegexReference(reference);
}

/// <summary>
/// One device-facing step of the package script shown as its own list row: a <c>stage</c> transfer that only loads a
/// package file into device RAM, or a storage-phase <c>oem</c> command such as the K3 <c>oem ec:flash</c>. Control-flow
/// actions (<c>getvar</c>, <c>continue</c>) and the table write behind <c>multi_flash</c> are not steps — the latter is
/// represented by the partition rows themselves.
/// <para>包脚本中作为独立列表行展示的一个面向设备步骤：只把包内文件载入设备内存的 <c>stage</c> 传输，或存储段落的
/// <c>oem</c> 命令（如 K3 的 <c>oem ec:flash</c>）。控制流动作（<c>getvar</c>、<c>continue</c>）与 <c>multi_flash</c>
/// 背后的写表动作不是步骤——后者已由分区行本身表达。</para>
/// </summary>
/// <param name="Command">The fastboot command the step runs: <c>stage</c> for a staging transfer, or the full OEM
/// command such as <c>oem ec:flash</c> whose body has no column of its own. A staged file lives in
/// <see cref="FileReference"/>, so the <c>stage</c> command text carries no arguments.
/// <para>该步骤执行的 fastboot 命令：暂存传输为 <c>stage</c>；OEM 命令体没有属于自己的列，因此保留完整命令，
/// 如 <c>oem ec:flash</c>。暂存的文件由 <see cref="FileReference"/> 承载，<c>stage</c> 命令文本不携带参数。</para></param>
/// <param name="FileReference">Package-relative file the step stages, empty when the step names no file.
/// <para>该步骤暂存的包内相对文件；步骤不涉及文件时为空。</para></param>
/// <param name="FilePath">Resolved file path, or <see langword="null"/> when the package carries no such file.
/// <para>解析出的文件路径；包内不存在该文件时为 <see langword="null"/>。</para></param>
/// <param name="FileLength">Resolved file length in bytes; 0 when unresolved or the step names no file.
/// <para>解析出的文件字节数；未解析或步骤不涉及文件时为 0。</para></param>
/// <param name="StorageMedium">The physical place the step's payload lands: <c>MEMORY</c> for a <c>stage</c> transfer
/// that only loads into device RAM, <c>EC</c> for the K3 EC write, and empty when the command names no known target.
/// <para>步骤负载的物理落点：只载入设备内存的 <c>stage</c> 传输为 <c>MEMORY</c>；K3 的 EC 写入为 <c>EC</c>；命令
/// 未指向已知目标时为空。</para></param>
public sealed record SpacemiTPackageScriptStep(
    string Command,
    string FileReference,
    string? FilePath,
    long FileLength,
    string StorageMedium);

/// <summary>
/// Contents of a SpacemiT flash package folder (the titan flasher layout with <c>fastboot.yaml</c> at its
/// root): the loaded package, the script steps that run outside the partition tables, and the rows of its layout
/// tables.
/// <para>SpacemiT 烧录包目录（根目录含 <c>fastboot.yaml</c> 的 titan 烧录器布局）的内容：已加载的包、
/// 分区表之外执行的脚本步骤，以及各布局表汇聚出的行。</para>
/// </summary>
/// <param name="Package">The validated flash package. <para>校验通过的烧录包。</para></param>
/// <param name="PartitionTablePath">Path of the primary partition table, empty when the package carries none.
/// <para>主分区表的路径；包内没有分区表时为空。</para></param>
/// <param name="ScriptSteps">Script steps in script order: every <c>stage</c> transfer — the boot chain that only
/// loads images into RAM — and the storage-phase <c>oem</c> commands.
/// <para>脚本顺序的脚本步骤：每个 <c>stage</c> 传输——只把镜像载入内存的引导链——以及存储段落的 <c>oem</c> 命令。</para></param>
/// <param name="Partitions">Partition rows: primary table order first, then names only the other layouts carry, and —
/// right after a volume-image entry — its synthesized <c>&lt;partition&gt;-&lt;volume&gt;</c> targets.
/// <para>分区行：先按主分区表顺序，再补上只有其它布局表收录的名称，并在卷镜像条目之后紧接其合成的
/// <c>&lt;分区&gt;-&lt;卷&gt;</c> 目标。</para></param>
public sealed record SpacemiTPackageContents(
    SpacemiTFlashPackage Package,
    string PartitionTablePath,
    IReadOnlyList<SpacemiTPackageScriptStep> ScriptSteps,
    IReadOnlyList<SpacemiTPackagePartition> Partitions);

/// <summary>
/// Reads a SpacemiT flash package folder: the <c>fastboot.yaml</c> marker validates the layout, the package's
/// layout tables provide one row per partition whose image reference resolves inside the package, and the script's
/// device-facing steps (the <c>stage</c> transfers that only load images into RAM plus the storage-phase
/// <c>oem</c> commands) become rows of their own so the boot chain is visible without a storage medium. A name listed by
/// several layouts — the block (<c>gpt</c>) table and the SPI NOR (<c>mtd</c>) table — becomes one row carrying every
/// medium it is written to, because the run selects partitions by name and therefore flashes every layout that has
/// it. Multi-image entries display their first reference; vendor regex markers stay unresolved rows. An entry whose
/// <c>volume_images</c> map is its only image source keeps its own row and adds one row per synthesized
/// <c>&lt;partition&gt;-&lt;volume&gt;</c> target, so every volume the flasher writes is visible and selectable.
/// <para>读取 SpacemiT 烧录包目录：<c>fastboot.yaml</c> 标记校验布局，包内各布局表为每个镜像引用能在包内解析的
/// 分区提供一行，脚本中面向设备的步骤（只把镜像载入内存的 <c>stage</c> 传输与存储段落的 <c>oem</c> 命令）则自成
/// 一行，使引导链在没有存储介质的情况下可见。被多张布局表收录的名称——块设备（<c>gpt</c>）表与 SPI NOR（<c>mtd</c>）
/// 表——合并为一行，并带上它被写入的全部介质，因为运行按名称选择分区，因而会刷写所有收录它的布局。多镜像条目
/// 显示其第一个引用；厂商正则标记保留为未解析行。以 <c>volume_images</c> 映射作为唯一镜像来源的条目保留自身行，
/// 并为每个合成的 <c>&lt;分区&gt;-&lt;卷&gt;</c> 目标补一行，使烧录器写入的每个卷都可见、可勾选。</para>
/// </summary>
public static class FlashSpacemiTPackage
{
    /// <summary>Partition table file preferred as the primary layout when several tables ship in one package.
    /// <para>包内存在多张分区表时优先作为主布局的分区表文件。</para></summary>
    private const string UniversalTable = "partition_universal.json";

    /// <summary>
    /// Whether a folder has the SpacemiT flash package layout.
    /// <para>判断目录是否符合 SpacemiT 烧录包布局。</para>
    /// </summary>
    /// <param name="folder">Directory to inspect. <para>待检查目录。</para></param>
    /// <returns><see langword="true"/> when the folder carries <c>fastboot.yaml</c> at its root.
    /// <para>目录根下带有 <c>fastboot.yaml</c> 时为 <see langword="true"/>。</para></returns>
    public static bool HasStructure(string folder)
        => Directory.Exists(folder) && File.Exists(Path.Combine(folder, SpacemiTFlashPackage.ScriptFileName));

    /// <summary>
    /// Reads a SpacemiT flash package folder.
    /// <para>读取 SpacemiT 烧录包目录。</para>
    /// </summary>
    /// <param name="folder">Package root directory. <para>包根目录。</para></param>
    /// <returns>The package contents, or <see langword="null"/> when the folder lacks <c>fastboot.yaml</c>.
    /// <para>包内容；目录缺少 <c>fastboot.yaml</c> 时为 <see langword="null"/>。</para></returns>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist or the script file is missing.
    /// <para>目录不存在或脚本文件缺失。</para></exception>
    /// <exception cref="InvalidDataException">The script or the primary partition table is not valid.
    /// <para>脚本或主分区表无效。</para></exception>
    /// <exception cref="IOException">The script or the primary partition table cannot be read.
    /// <para>脚本或主分区表无法读取。</para></exception>
    public static SpacemiTPackageContents? Read(string folder)
    {
        if (!HasStructure(folder))
        {
            return null;
        }

        var package = SpacemiTFlashPackage.Load(folder);
        var tablePath = SelectPartitionTable(folder);
        if (tablePath is null)
        {
            return new SpacemiTPackageContents(package, string.Empty, CollectScriptSteps(package), []);
        }

        var layouts = LoadLayoutTables(folder, tablePath);
        var formatsByPartition = CollectFormats(layouts);
        var partitions = new List<SpacemiTPackagePartition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in layouts)
        {
            foreach (var entry in table.Partitions)
            {
                if (!seen.Add(entry.Name))
                {
                    continue;
                }

                var formats = formatsByPartition[entry.Name];
                var reference = entry.Images.Count > 0 ? entry.Images[0] : string.Empty;
                var resolved = reference.Length > 0 && !IsRegexReference(reference)
                    ? package.ResolveFile(reference)
                    : null;
                partitions.Add(new SpacemiTPackagePartition(entry.Name, resolved, reference,
                    resolved is { } path ? new FileInfo(path).Length : 0,
                    entry.Offset, entry.Size, formats));

                if (entry.Images.Count > 0 || entry.VolumeImages.Count == 0)
                {
                    continue;
                }

                foreach (var (volume, image) in entry.VolumeImages)
                {
                    var targetName = $"{entry.Name}-{volume}";
                    var volumeResolved = !IsRegexReference(image) ? package.ResolveFile(image) : null;
                    partitions.Add(new SpacemiTPackagePartition(targetName, volumeResolved, image,
                        volumeResolved is { } volumePath ? new FileInfo(volumePath).Length : 0,
                        entry.Offset, entry.Size, formats));
                }
            }
        }

        return new SpacemiTPackageContents(package, tablePath, CollectScriptSteps(package), partitions);
    }

    /// <summary>
    /// Derives the script's device-facing steps in script order: every <c>stage</c> action becomes a step because it
    /// only loads its file into device RAM, and an <c>oem</c> action becomes one when it runs in the storage phase —
    /// after the last <c>continue</c> — where it changes device state such as the K3 EC firmware. Boot-phase
    /// <c>oem</c> commands (the USB speed switch) and the control-flow actions stay out.
    /// <para>按脚本顺序派生面向设备的脚本步骤：每个 <c>stage</c> 动作都是一个步骤，因为它只把文件载入设备内存；
    /// <c>oem</c> 动作在存储段落（最后一个 <c>continue</c> 之后）执行、会改变设备状态（如 K3 的 EC 固件）时也是一个
    /// 步骤。引导段落的 <c>oem</c> 命令（USB 速率切换）与控制流动作不在此列。</para>
    /// </summary>
    /// <param name="package">The loaded package whose script is scanned. <para>已加载、将扫描其脚本的包。</para></param>
    /// <returns>The steps in script order. <para>按脚本顺序排列的步骤。</para></returns>
    private static List<SpacemiTPackageScriptStep> CollectScriptSteps(SpacemiTFlashPackage package)
    {
        var script = package.Script;
        var steps = new List<SpacemiTPackageScriptStep>();
        for (var index = 0; index < script.Actions.Count; index++)
        {
            switch (script.Actions[index])
            {
                case SpacemiTStageAction stage:
                    var resolved = stage.File.Length > 0 && !FlashSpacemiTPackage.IsRegexReference(stage.File)
                        ? package.ResolveFile(stage.File)
                        : null;
                    steps.Add(new SpacemiTPackageScriptStep("stage", stage.File, resolved,
                        resolved is { } path ? new FileInfo(path).Length : 0, "MEMORY"));
                    break;
                case SpacemiTOemAction oem when index >= script.BootChainEnd:
                    // The OEM body is part of the command itself and has no column of its own, so the command text
                    // keeps it; the medium names the hardware the body targets, which the vendor flow only defines
                    // for the EC write.
                    // OEM 命令体是命令本身的一部分，没有属于自己的列，因此命令文本保留它；介质命名命令体指向的硬件，
                    // 厂商流程中只有 EC 写入有明确目标。
                    var medium = oem.Args.StartsWith("ec:", StringComparison.OrdinalIgnoreCase) ? "EC" : string.Empty;
                    steps.Add(new SpacemiTPackageScriptStep($"oem {oem.Args}", string.Empty, null, 0, medium));
                    break;
            }
        }

        return steps;
    }

    /// <summary>
    /// Loads every layout table the run can write: the primary table plus the other <c>partition_*.json</c> layouts,
    /// skipping the vendor's flash-all descriptor. That descriptor lists several images per partition (and sometimes a
    /// regex marker) instead of one image per partition, so it describes what a vendor GUI may download rather than a
    /// storage layout; reading it as a layout would invent rows the run never writes.
    /// <para>加载运行可能写入的每张布局表：主表加上其它 <c>partition_*.json</c> 布局，跳过厂商的“全量刷写”描述表。
    /// 后者每个分区列出多个镜像（有时还有正则标记），描述的是厂商界面可能下载的内容而非存储布局；把它当布局读取会
    /// 编造出运行并不会写入的行。</para>
    /// </summary>
    /// <remarks>
    /// A secondary table that fails to parse is dropped as evidence instead of failing the listing: an unreadable
    /// layout only means its media are unknown, and the primary table still describes the package. The vendor's
    /// flash-all descriptor is excluded by the multi-image and regex-marker check below, not by a parse failure.
    /// <para>解析失败的次级分区表只作为证据被丢弃，不会让列举失败：一张读不懂的布局只意味着其介质未知，
    /// 主分区表仍然描述了这个包。厂商的「全量刷写」描述表由下面的多镜像与正则标记判定排除，而不是靠解析失败。</para>
    /// </remarks>
    /// <param name="folder">Package root directory. <para>包根目录。</para></param>
    /// <param name="primaryPath">Path of the primary table, already selected. <para>已选定的主分区表路径。</para></param>
    /// <returns>The primary table followed by the other layout tables, in file-name order.
    /// <para>主分区表在前、其它布局表按文件名顺序随后。</para></returns>
    /// <exception cref="InvalidDataException">The primary table is not a valid partition table.
    /// <para>主分区表不是有效的分区表。</para></exception>
    /// <exception cref="IOException">The primary table cannot be read. <para>主分区表无法读取。</para></exception>
    private static List<SpacemiTPartitionTable> LoadLayoutTables(string folder, string primaryPath)
    {
        List<SpacemiTPartitionTable> layouts = [SpacemiTPartitionTable.Load(primaryPath)];
        var primary = Path.GetFullPath(primaryPath);
        foreach (var path in Directory.EnumerateFiles(folder, "partition_*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFullPath(path), primary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SpacemiTPartitionTable table;
            try
            {
                table = SpacemiTPartitionTable.Load(path);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or FormatException)
            {
                continue;
            }

            if (!table.Partitions.Any(static entry =>
                    entry.Images.Count > 1 || entry.Images.Any(static image => IsRegexReference(image))))
            {
                layouts.Add(table);
            }
        }

        return layouts;
    }

    /// <summary>
    /// Collects the formats of every layout table that lists each partition, preserving first-seen order.
    /// <para>为每个分区收集所有收录它的布局表格式，并保持首次出现的顺序。</para>
    /// </summary>
    /// <param name="layouts">The loaded layout tables. <para>已加载的布局表。</para></param>
    /// <returns>Partition name to its distinct table formats. <para>分区名到其去重的分区表格式集合。</para></returns>
    private static Dictionary<string, List<string>> CollectFormats(IReadOnlyList<SpacemiTPartitionTable> layouts)
    {
        var formats = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var table in layouts)
        {
            foreach (var entry in table.Partitions)
            {
                if (!formats.TryGetValue(entry.Name, out var known))
                {
                    formats[entry.Name] = known = [];
                }

                if (!known.Contains(table.Format, StringComparer.Ordinal))
                {
                    known.Add(table.Format);
                }
            }
        }

        return formats;
    }

    /// <summary>
    /// Whether an image reference is the vendor's regex marker (<c>/…/</c>) rather than a package file.
    /// <para>判断镜像引用是否为厂商的正则标记（<c>/…/</c>）而不是包内文件。</para>
    /// </summary>
    /// <param name="reference">Image reference text. <para>镜像引用文本。</para></param>
    /// <returns><see langword="true"/> when the reference is wrapped in slashes.
    /// <para>引用被斜杠包裹时为 <see langword="true"/>。</para></returns>
    internal static bool IsRegexReference(string reference)
        => reference.Length > 1 && reference[0] == '/' && reference[^1] == '/';

    /// <summary>
    /// Picks the primary <c>partition_*.json</c> table: <c>partition_universal.json</c> when present,
    /// otherwise the alphabetically first one.
    /// <para>挑选主 <c>partition_*.json</c> 分区表：存在 <c>partition_universal.json</c> 时用它，否则取
    /// 名称排序最靠前的一个。</para>
    /// </summary>
    /// <param name="folder">Package root directory. <para>包根目录。</para></param>
    /// <returns>The table path, or <see langword="null"/> when the package has none.
    /// <para>分区表路径；包内没有时为 <see langword="null"/>。</para></returns>
    private static string? SelectPartitionTable(string folder)
    {
        string? first = null;
        string? universal = null;
        foreach (var path in Directory.EnumerateFiles(folder, "partition_*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            first ??= path;
            if (universal is null &&
                string.Equals(Path.GetFileName(path), UniversalTable, StringComparison.OrdinalIgnoreCase))
            {
                universal = path;
            }
        }

        return universal ?? first;
    }
}