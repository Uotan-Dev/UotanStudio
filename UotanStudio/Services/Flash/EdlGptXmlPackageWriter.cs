using System.Globalization;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Services.Flash;

/// <summary>
/// The geometry a generated EDL XML package is built from: sector size, the primary and backup GPT blob spans and
/// the disk-relative offsets of one validated table, expressed the way the Qualcomm toolchain (ptool) expresses them.
/// <para>生成 EDL XML 包所用的几何：扇区大小、主备两份 GPT 镜像的跨度以及一张已校验表的磁盘相对偏移，全部按
/// 高通工具链（ptool）的表达方式给出。</para>
/// </summary>
/// <param name="Lun">Physical partition number the package addresses. <para>包所寻址的物理分区号。</para></param>
/// <param name="SectorSize">Logical sector size in bytes. <para>逻辑扇区字节数。</para></param>
/// <param name="DiskSectors">Total sector count of the region; zero when the source carries no disk-size field
/// (file-parsed tables whose header was stripped). <para>该区域的总扇区数；来源不含盘尺寸字段（头被剥离的文件解析表）时为零。</para></param>
/// <param name="PrimaryBlobSectors">Sector count the primary GPT blob spans, from LBA zero.
/// <para>主 GPT 镜像自 LBA 零起算的扇区数。</para></param>
/// <param name="BackupBlobSectors">Sector count the backup GPT blob spans back from the last sector.
/// <para>备份 GPT 镜像从最后一个扇区往回计的扇区数。</para></param>
/// <param name="LastUsableOffset">Disk-relative offset of the last usable LBA, i.e. <c>DiskSectors - LastUsableLba</c>
/// for a live table or the primary blob span for a stripped one, matching ptool's expression.
/// <para>最后可用 LBA 的磁盘相对偏移：活动表为 <c>DiskSectors - LastUsableLba</c>，被剥离的表取主镜像跨度，
/// 与 ptool 的表达一致。</para></param>
/// <param name="EntryCount">Declared entry-array capacity. <para>声明的条目数组容量。</para></param>
/// <param name="EntrySize">Declared size of one entry. <para>声明的单条目字节数。</para></param>
/// <param name="PrimaryArrayLba">LBA of the primary entry array. <para>主条目数组的 LBA。</para></param>
/// <param name="BackupArrayLba">LBA of the backup entry array; zero when unknown. <para>备份条目数组的 LBA；未知时为零。</para></param>
/// <param name="Partitions">The table's non-empty entries. <para>表中的非空条目。</para></param>
internal sealed record EdlGptXmlLayout(int Lun, long SectorSize, long DiskSectors, long PrimaryBlobSectors,
    long BackupBlobSectors, long LastUsableOffset, uint EntryCount, uint EntrySize, long PrimaryArrayLba,
    long BackupArrayLba, IReadOnlyList<GptPartition> Partitions)
{
    /// <summary>Gets the byte length of the entry array. <para>获取条目数组的字节长度。</para></summary>
    /// <value>Entry count multiplied by entry size. <para>条目数量乘以条目字节数。</para></value>
    internal long ArrayBytes => (long)EntryCount * EntrySize;

    /// <summary>Gets the sector count the primary array occupies. <para>获取主条目数组占用的扇区数。</para></summary>
    /// <value>Array bytes rounded up to whole sectors. <para>数组字节数向上取整到整扇区。</para></value>
    internal long ArraySectors => (ArrayBytes + SectorSize - 1) / SectorSize;
}

/// <summary>
/// One generated XML package: the directory and the five files written into it.
/// <para>一个生成的 XML 包：目录以及写入其中的五个文件。</para>
/// </summary>
/// <param name="DirectoryPath">Directory holding every file. <para>存放全部文件的目录。</para></param>
/// <param name="RawProgramPath">Generated rawprogram manifest. <para>生成的 rawprogram 清单。</para></param>
/// <param name="PatchPath">Generated patch manifest. <para>生成的 patch 清单。</para></param>
/// <param name="PartitionPath">Generated partition description. <para>生成的分区描述。</para></param>
/// <param name="PrimaryGptPath">Primary GPT blob read from the device. <para>从设备读到的主 GPT 镜像。</para></param>
/// <param name="BackupGptPath">Backup GPT blob read from the device. <para>从设备读到的备份 GPT 镜像。</para></param>
/// <param name="Layout">Geometry the package was built from. <para>生成该包所用的几何。</para></param>
internal sealed record EdlGptXmlPackage(string DirectoryPath, string RawProgramPath, string PatchPath,
    string PartitionPath, string PrimaryGptPath, string BackupGptPath, EdlGptXmlLayout Layout);

/// <summary>
/// A generated multi-region XML package: one directory holding every region's (LUN's) files, one package per
/// readable LUN.
/// <para>生成的多区域 XML 包：一个目录容纳每个区域（LUN）的文件，每个可读 LUN 一份包。</para>
/// </summary>
/// <param name="DirectoryPath">Directory holding every region's files. <para>存放全部区域文件的目录。</para></param>
/// <param name="Packages">One package per readable LUN, ordered by LUN. <para>每个可读 LUN 一份包，按 LUN 排序。</para></param>
internal sealed record EdlGptXmlPackageSet(string DirectoryPath, IReadOnlyList<EdlGptXmlPackage> Packages);

/// <summary>
/// Writes a device partition table as the Qualcomm toolchain layout: the two GPT blobs read back from the region plus
/// rawprogram, patch and partition XML documents that describe and restore it.
/// <para>把设备分区表写成高通工具链布局：从该区域读回的两份 GPT 镜像，以及描述并恢复它的 rawprogram、patch 与
/// partition XML。</para>
/// </summary>
/// <remarks>
/// The rawprogram rows follow the shape <c>ptool.py</c> produces: one program row per table entry in table order —
/// including a zero-length grow placeholder — then the PrimaryGPT and BackupGPT rows, with an empty file name when no
/// image exists for a partition. The patch rows follow <c>ptool.py</c>'s row set (last-partition grow fix, last usable
/// LBA, backup header location, array location, array and header CRC, expressed against <c>NUM_DISK_SECTORS</c>) and
/// target each GPT blob file as well as the raw <c>DISK</c>: the file rows fix the blob before it is programmed and the
/// disk rows fix the header of what was just written. The partition document uses the <c>configuration</c> shape with
/// the same parser instructions <c>ptool.py</c> and <c>gen_partition.py</c> emit.
/// <para>rawprogram 条目沿用 <c>ptool.py</c> 的形状：按表顺序为每条表条目生成一条 program 条目（包含零长度的 grow 占位），
/// 随后是 PrimaryGPT 与 BackupGPT 条目；分区没有镜像时文件名为空。patch 条目沿用 <c>ptool.py</c> 的行集（最后一分区
/// grow 修正、最后可用 LBA、备份头位置、数组位置、数组与头部 CRC，均以 <c>NUM_DISK_SECTORS</c> 相对值表达），并同时
/// 指向两份 GPT 镜像与裸盘 <c>DISK</c>：镜像行在编程前修正镜像，裸盘行修正刚写下的头部。分区文档采用
/// <c>configuration</c> 形状，并带上 <c>ptool.py</c> 与 <c>gen_partition.py</c> 生成的同一组解析指令。</para>
/// </remarks>
internal static class EdlGptXmlPackageWriter
{
    /// <summary>The rawprogram manifest file name the bundle runner selects.
    /// <para>包执行器会选中的 rawprogram 清单文件名。</para></summary>
    internal const string RawProgramFileName = "rawprogram.xml";

    /// <summary>The patch manifest file name the bundle runner pairs with the rawprogram one.
    /// <para>包执行器与 rawprogram 清单配对使用的 patch 清单文件名。</para></summary>
    internal const string PatchFileName = "patch.xml";

    /// <summary>The partition description file name. <para>分区描述文件名。</para></summary>
    internal const string PartitionFileName = "partition.xml";

    /// <summary>The primary GPT blob file name. <para>主 GPT 镜像文件名。</para></summary>
    internal const string PrimaryGptFileName = "gpt_main.bin";

    /// <summary>The backup GPT blob file name. <para>备份 GPT 镜像文件名。</para></summary>
    internal const string BackupGptFileName = "gpt_backup.bin";

    /// <summary>
    /// Reads both GPT blobs out of the region and writes the five files into an existing directory.
    /// <para>从该区域读出两份 GPT 镜像并把五个文件写入已存在的目录。</para>
    /// </summary>
    /// <param name="firehose">The initialized Firehose protocol. <para>已初始化的 Firehose 协议实例。</para></param>
    /// <param name="snapshot">The validated table to describe. <para>要描述其内容的已校验分区表。</para></param>
    /// <param name="directoryPath">Existing directory that receives the files. <para>接收文件且已存在的目录。</para></param>
    /// <param name="cancellationToken">Cancels both blob reads and the file writes. <para>取消两次镜像读取与写文件。</para></param>
    /// <returns>The written package. <para>已写出的包。</para></returns>
    /// <exception cref="ArgumentNullException">The protocol or snapshot is null. <para>协议或快照为空。</para></exception>
    /// <exception cref="ArgumentException">The directory path is blank. <para>目录路径为空白。</para></exception>
    /// <exception cref="InvalidOperationException">The table geometry cannot be expressed as a blob pair.
    /// <para>表的几何无法表达为一对镜像。</para></exception>
    internal static async Task<EdlGptXmlPackage> WriteAsync(FirehoseProtocol firehose, FirehoseGptSnapshot snapshot,
        string directoryPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        Directory.CreateDirectory(directoryPath);
        return await WriteOneAsync(firehose, snapshot, directoryPath, PrimaryGptFileName, BackupGptFileName,
            RawProgramFileName, PatchFileName, PartitionFileName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads every region's (LUN's) partition table and writes one package per readable LUN into an existing
    /// directory: the two GPT blobs plus rawprogram, patch and partition manifests, each file carrying the LUN
    /// number as its name suffix so one directory holds all regions side by side.
    /// <para>读取每个区域（LUN）的分区表，并把每个可读 LUN 的一份包写入已存在的目录：两份 GPT 镜像加 rawprogram、
    /// patch 与 partition 清单，文件名都以 LUN 号为后缀，使一个目录并排容纳全部区域。</para>
    /// </summary>
    /// <param name="firehose">The initialized Firehose protocol. <para>已初始化的 Firehose 协议实例。</para></param>
    /// <param name="snapshots">One validated table per readable LUN, ordered by LUN. <para>每个可读 LUN 一份已校验表，按 LUN 排序。</para></param>
    /// <param name="directoryPath">Existing directory that receives the files. <para>接收文件且已存在的目录。</para></param>
    /// <param name="cancellationToken">Cancels the blob reads and the file writes. <para>取消镜像读取与写文件。</para></param>
    /// <returns>The written package set. <para>已写出的包集合。</para></returns>
    /// <exception cref="ArgumentNullException">The protocol or the snapshot list is null.
    /// <para>协议或快照列表为空。</para></exception>
    /// <exception cref="ArgumentException">The snapshot list is empty or the directory path is blank.
    /// <para>快照列表为空或目录路径为空白。</para></exception>
    /// <exception cref="InvalidOperationException">A table's geometry cannot be expressed as a blob pair.
    /// <para>某张表的几何无法表达为一对镜像。</para></exception>
    internal static async Task<EdlGptXmlPackageSet> WriteAsync(FirehoseProtocol firehose,
        IReadOnlyList<FirehoseGptSnapshot> snapshots, string directoryPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("At least one table snapshot is required. 至少需要一份表快照。", nameof(snapshots));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        Directory.CreateDirectory(directoryPath);
        var packages = new List<EdlGptXmlPackage>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var lun = snapshot.Geometry.PhysicalPartitionNumber;
            packages.Add(await WriteOneAsync(firehose, snapshot, directoryPath,
                FormattableString.Invariant($"gpt_main{lun}.bin"),
                FormattableString.Invariant($"gpt_backup{lun}.bin"),
                FormattableString.Invariant($"rawprogram{lun}.xml"),
                FormattableString.Invariant($"patch{lun}.xml"),
                FormattableString.Invariant($"partition{lun}.xml"),
                cancellationToken).ConfigureAwait(false));
        }

        return new EdlGptXmlPackageSet(directoryPath, packages);
    }

    /// <summary>
    /// Writes one region's five files with the given names, the shared core of the single-LUN and multi-LUN writers.
    /// <para>以给定文件名写出一个区域的五个文件；单 LUN 与多 LUN 写入器共用的核心。</para>
    /// </summary>
    private static async Task<EdlGptXmlPackage> WriteOneAsync(FirehoseProtocol firehose, FirehoseGptSnapshot snapshot,
        string directoryPath, string primaryGptFileName, string backupGptFileName, string rawProgramFileName,
        string patchFileName, string partitionFileName, CancellationToken cancellationToken)
    {
        var layout = Describe(snapshot);
        var primaryPath = Path.Combine(directoryPath, primaryGptFileName);
        var backupPath = Path.Combine(directoryPath, backupGptFileName);
        await ReadBlobAsync(firehose, layout, 0, layout.PrimaryBlobSectors, primaryGptFileName, primaryPath,
            cancellationToken).ConfigureAwait(false);
        await ReadBlobAsync(firehose, layout, layout.BackupArrayLba, layout.BackupBlobSectors, backupGptFileName,
            backupPath, cancellationToken).ConfigureAwait(false);

        var rawProgramPath = Path.Combine(directoryPath, rawProgramFileName);
        var patchPath = Path.Combine(directoryPath, patchFileName);
        var partitionPath = Path.Combine(directoryPath, partitionFileName);
        await File.WriteAllTextAsync(rawProgramPath, BuildRawProgram(layout), new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(patchPath, BuildPatch(layout, primaryGptFileName, backupGptFileName),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(partitionPath, BuildPartition(layout), new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return new EdlGptXmlPackage(directoryPath, rawProgramPath, patchPath, partitionPath, primaryPath, backupPath,
            layout);
    }

    /// <summary>
    /// Derives the XML layout from one snapshot, rejecting geometry that cannot be written as a blob pair.
    /// <para>从一份快照推导 XML 布局，并拒绝无法写成一对镜像的几何。</para>
    /// </summary>
    /// <param name="snapshot">The validated table. <para>已校验的分区表。</para></param>
    /// <returns>The layout used by every builder. <para>各构建器共用的布局。</para></returns>
    /// <exception cref="ArgumentNullException">The snapshot is null. <para>快照为空。</para></exception>
    /// <exception cref="InvalidOperationException">A declared length or array location cannot be represented.
    /// <para>声明的长度或数组位置无法表示。</para></exception>
    internal static EdlGptXmlLayout Describe(FirehoseGptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var geometry = snapshot.Geometry;
        var table = snapshot.Table;
        if (geometry.SectorSize <= 0 || geometry.SectorCount <= 0 ||
            table.PartitionEntryCount == 0 || table.PartitionEntrySize == 0)
        {
            throw new InvalidOperationException(
                "The table geometry has no positive sector size, sector count or entry dimensions. " +
                "表几何缺少正的扇区大小、扇区数或条目维度。");
        }

        if (table.PrimaryEntryArrayLba is 0 || table.PrimaryEntryArrayLba > long.MaxValue ||
            table.BackupEntryArrayLba > long.MaxValue ||
            table.BackupEntryArrayLba <= table.PrimaryEntryArrayLba ||
            (ulong)table.BackupEntryArrayLba >= (ulong)geometry.SectorCount ||
            table.LastUsableLba >= (ulong)geometry.SectorCount)
        {
            throw new InvalidOperationException(
                "The table's entry-array locations cannot be expressed as one primary and one backup blob. " +
                "该表的条目数组位置无法表达为主备两份镜像。");
        }

        var primaryBlobSectors = checked((long)table.PrimaryEntryArrayLba) +
            ((long)table.PartitionEntryCount * table.PartitionEntrySize + geometry.SectorSize - 1) /
            geometry.SectorSize;
        var backupBlobSectors = geometry.SectorCount - checked((long)table.BackupEntryArrayLba);
        return new EdlGptXmlLayout(geometry.PhysicalPartitionNumber, geometry.SectorSize, geometry.SectorCount,
            primaryBlobSectors, backupBlobSectors, geometry.SectorCount - checked((long)table.LastUsableLba),
            table.PartitionEntryCount, table.PartitionEntrySize,
            checked((long)table.PrimaryEntryArrayLba), checked((long)table.BackupEntryArrayLba), table.Partitions);
    }

    private static async Task ReadBlobAsync(FirehoseProtocol firehose, EdlGptXmlLayout layout, long startSector,
        long sectorCount, string readName, string path, CancellationToken cancellationToken)
    {
        if (sectorCount <= 0 || startSector < 0 ||
            checked(startSector + sectorCount) > layout.DiskSectors)
        {
            throw new InvalidOperationException(
                $"The GPT blob {readName} covers sectors {startSector}..{startSector + sectorCount - 1}, outside the " +
                "region the target reported. 该 GPT 镜像覆盖的扇区超出目标上报的区域。");
        }

        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await firehose.ReadPartitionToStreamAsync(output, startSector, sectorCount, readName, layout.Lun,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Points one program row of a generated rawprogram manifest at another file, replacing the document atomically so
    /// a reader never sees a half-written manifest.
    /// <para>把生成的 rawprogram 清单中某一条 program 条目改指向另一个文件，并以原子替换写回，使读取方不会看到写了一半的
    /// 清单。</para>
    /// </summary>
    /// <param name="rawProgramPath">The generated manifest to update. <para>要更新的生成清单。</para></param>
    /// <param name="label">Program label to match. <para>要匹配的 program 标签。</para></param>
    /// <param name="lun">Physical partition number to match. <para>要匹配的物理分区号。</para></param>
    /// <param name="fileName">File name to write into the row. <para>要写入该条目的文件名。</para></param>
    /// <param name="cancellationToken">Cancels the rewrite. <para>取消改写。</para></param>
    /// <returns><see langword="true"/> when a row was updated. <para>更新了某条条目时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentException">A parameter is blank. <para>某个参数为空白。</para></exception>
    internal static async Task<bool> UpdateRawProgramFileNameAsync(string rawProgramPath, string label, int lun,
        string fileName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawProgramPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var document = XDocument.Load(rawProgramPath, LoadOptions.PreserveWhitespace);
        var matches = document.Descendants("program")
            .Where(element => string.Equals((string?)element.Attribute("label"), label, StringComparison.Ordinal) &&
                (int?)element.Attribute("physical_partition_number") is { } rowLun && rowLun == lun)
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        matches[0].SetAttributeValue("filename", fileName);
        var temporaryPath = rawProgramPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, document.Declaration + Environment.NewLine + document.Root,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, rawProgramPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return true;
    }

    /// <summary>
    /// Names a chosen file the way a generated manifest should reference it: relative when it lives inside the package
    /// directory, absolute otherwise.
    /// <para>把所选文件按生成清单应有的引用方式命名：位于包目录内时用相对路径，否则用绝对路径。</para>
    /// </summary>
    /// <param name="packageDirectory">The generated package directory. <para>生成的包目录。</para></param>
    /// <param name="filePath">The file the user chose. <para>用户所选的文件。</para></param>
    /// <returns>The file name to write into the manifest. <para>要写入清单的文件名。</para></returns>
    internal static string PackageRelativeFileName(string packageDirectory, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var relative = Path.GetRelativePath(Path.GetFullPath(packageDirectory), Path.GetFullPath(filePath));
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? Path.GetFullPath(filePath)
            : relative;
    }

    /// <summary>
    /// Builds the rawprogram manifest text for one layout.
    /// <para>为一份布局构建 rawprogram 清单文本。</para>
    /// </summary>
    /// <param name="layout">The table geometry and entries. <para>表几何与条目。</para></param>
    /// <returns>The rawprogram XML text. <para>rawprogram XML 文本。</para></returns>
    /// <exception cref="ArgumentNullException">The layout is null. <para>布局为空。</para></exception>
    internal static string BuildRawProgram(EdlGptXmlLayout layout)
        => BuildRawProgram(layout, null, null, null);

    /// <summary>
    /// Builds the rawprogram manifest text for one layout, letting a caller bind each partition row to an existing
    /// image file name and name the two GPT rows after files that already exist.
    /// <para>为一份布局构建 rawprogram 清单文本，允许调用方把每条分区条目绑定到已存在的镜像文件名，并让两条
    /// GPT 条目引用已经存在的文件名。</para>
    /// </summary>
    /// <param name="layout">The table geometry and entries. <para>表几何与条目。</para></param>
    /// <param name="imageBinding">Maps a partition to its image file name, or returns <see langword="null"/> to leave
    /// the file name empty. <para>把分区映射到其镜像文件名；返回 <see langword="null"/> 时文件名为空。</para></param>
    /// <param name="primaryGptFileName">File name written into the PrimaryGPT row, or <see langword="null"/> for the
    /// default. <para>PrimaryGPT 条目写入的文件名；<see langword="null"/> 用默认值。</para></param>
    /// <param name="backupGptFileName">File name written into the BackupGPT row, or <see langword="null"/> for the
    /// default. <para>BackupGPT 条目写入的文件名；<see langword="null"/> 用默认值。</para></param>
    /// <returns>The rawprogram XML text. <para>rawprogram XML 文本。</para></returns>
    /// <exception cref="ArgumentNullException">The layout is null. <para>布局为空。</para></exception>
    internal static string BuildRawProgram(EdlGptXmlLayout layout, Func<GptPartition, string?>? imageBinding,
        string? primaryGptFileName, string? backupGptFileName)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var root = new XElement("data");
        root.Add(new XComment("NOTE: This is an ** Autogenerated file **"));
        root.Add(new XComment(FormattableString.Invariant($"NOTE: Sector size is {layout.SectorSize}bytes")));
        foreach (var partition in layout.Partitions)
        {
            var sectors = PartitionSectors(partition);
            var label = FileLabel(partition);
            var fileName = imageBinding?.Invoke(partition) ?? string.Empty;
            root.Add(new XElement("program", ProgramAttributes(layout, label, fileName, partition.FirstLba,
                sectors, "false")));
        }

        root.Add(new XElement("program", ProgramAttributes(layout, "PrimaryGPT",
            primaryGptFileName ?? PrimaryGptFileName, 0, layout.PrimaryBlobSectors, "true")));
        root.Add(new XElement("program", new XAttribute("SECTOR_SIZE_IN_BYTES",
            layout.SectorSize.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("file_sector_offset", "0"),
            new XAttribute("filename", backupGptFileName ?? BackupGptFileName),
            new XAttribute("label", "BackupGPT"),
            new XAttribute("num_partition_sectors", layout.BackupBlobSectors.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("partofsingleimage", "true"),
            new XAttribute("physical_partition_number", layout.Lun.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("readbackverify", "false"),
            new XAttribute("size_in_KB", SizeInKb(layout.BackupBlobSectors, layout.SectorSize)),
            new XAttribute("sparse", "false"),
            new XAttribute("start_byte_hex", FormattableString.Invariant(
                $"({layout.SectorSize}*NUM_DISK_SECTORS)-{layout.BackupBlobSectors * layout.SectorSize}.")),
            new XAttribute("start_sector", FormattableString.Invariant($"NUM_DISK_SECTORS-{layout.BackupBlobSectors}."))));
        return Document(root);
    }

    /// <summary>
    /// Builds the patch manifest text for one layout, following the Qualcomm toolchain (ptool) row set: the
    /// last-partition grow fix, the last-usable-LBA fix, the backup header and array locations, the array and header
    /// CRC rows, each emitted for the GPTMAIN and GPTBACKUP blob files and for the raw DISK with
    /// <c>NUM_DISK_SECTORS-</c> relative expressions.
    /// <para>为一份布局构建 patch 清单文本，沿用高通工具链（ptool）的行集：最后一分区 grow 修正、最后可用 LBA
    /// 修正、备份头与数组位置、数组与头部 CRC 行，每条同时给出 GPTMAIN 与 GPTBACKUP 镜像文件目标以及
    /// <c>NUM_DISK_SECTORS-</c> 相对表达的裸盘目标。</para>
    /// </summary>
    /// <param name="layout">The table geometry. <para>表几何。</para></param>
    /// <returns>The patch XML text. <para>patch XML 文本。</para></returns>
    /// <exception cref="ArgumentNullException">The layout is null. <para>布局为空。</para></exception>
    internal static string BuildPatch(EdlGptXmlLayout layout) => BuildPatch(layout, null, null);

    /// <summary>
    /// Builds the patch manifest text for one layout, naming the two GPT blob files the patch rows target; the raw
    /// disk keeps the <c>DISK</c> target and the <c>NUM_DISK_SECTORS-</c> relative expressions.
    /// <para>为一份布局构建 patch 清单文本，并命名 patch 条目所指向的两份 GPT 镜像文件；裸盘目标保持
    /// <c>DISK</c> 与 <c>NUM_DISK_SECTORS-</c> 相对表达式。</para>
    /// </summary>
    /// <param name="layout">The table geometry. <para>表几何。</para></param>
    /// <param name="primaryGptFileName">File name of the primary blob, or <see langword="null"/> for the default.
    /// <para>主 GPT 镜像文件名；<see langword="null"/> 用默认值。</para></param>
    /// <param name="backupGptFileName">File name of the backup blob, or <see langword="null"/> for the default.
    /// <para>备份 GPT 镜像文件名；<see langword="null"/> 用默认值。</para></param>
    /// <returns>The patch XML text. <para>patch XML 文本。</para></returns>
    /// <exception cref="ArgumentNullException">The layout is null. <para>布局为空。</para></exception>
    internal static string BuildPatch(EdlGptXmlLayout layout, string? primaryGptFileName, string? backupGptFileName)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var primaryName = primaryGptFileName ?? PrimaryGptFileName;
        var backupName = backupGptFileName ?? BackupGptFileName;
        var root = new XElement("patches");
        root.Add(new XComment("NOTE: This is an ** Autogenerated file **"));
        root.Add(new XComment(
            "NOTE: Patching is in little endian format, i.e. 0xAABBCCDD will look like DD CC BB AA in the file or on disk"));
        root.Add(new XComment(
            "NOTE: This file is used by Trace32 - So make sure to add decimals, i.e. 0x10-10=0, *but* 0x10-10.=6."));
        var crcBytes = layout.ArrayBytes;

        if (layout.Partitions.Count > 0)
        {
            var last = layout.Partitions[^1];
            var entryOffset = 40 + last.Index * (long)layout.EntrySize;
            var start = layout.PrimaryArrayLba + entryOffset / layout.SectorSize;
            var offset = entryOffset % layout.SectorSize;
            var value = FormattableString.Invariant($"NUM_DISK_SECTORS-{layout.PrimaryBlobSectors}.");
            var backupStart = entryOffset / layout.SectorSize;
            var backupDiskStart = FormattableString.Invariant(
                $"NUM_DISK_SECTORS-{layout.BackupBlobSectors - backupStart}.");
            AddPatch(root, layout, primaryName, start.ToString(CultureInfo.InvariantCulture), offset, 8, value,
                FormattableString.Invariant(
                    $"Update last partition {last.Index + 1} '{last.Name}' with actual size in Primary Header."));
            AddPatch(root, layout, DiskPatchTarget, start.ToString(CultureInfo.InvariantCulture), offset, 8, value,
                FormattableString.Invariant(
                    $"Update last partition {last.Index + 1} '{last.Name}' with actual size in Primary Header."));
            AddPatch(root, layout, backupName, backupStart.ToString(CultureInfo.InvariantCulture), offset, 8, value,
                FormattableString.Invariant(
                    $"Update last partition {last.Index + 1} '{last.Name}' with actual size in Backup Header."));
            AddPatch(root, layout, DiskPatchTarget, backupDiskStart, offset, 8, value,
                FormattableString.Invariant(
                    $"Update last partition {last.Index + 1} '{last.Name}' with actual size in Backup Header."));
        }

        var lastUsable = FormattableString.Invariant($"NUM_DISK_SECTORS-{layout.LastUsableOffset}.");
        var backupHeader = "NUM_DISK_SECTORS-1.";
        var backupInBlob = layout.BackupBlobSectors - 1;
        var backupInBlobText = backupInBlob.ToString(CultureInfo.InvariantCulture);
        AddPatch(root, layout, primaryName, "1", 48, 8, lastUsable, "Update Primary Header with LastUseableLBA.");
        AddPatch(root, layout, DiskPatchTarget, "1", 48, 8, lastUsable, "Update Primary Header with LastUseableLBA.");
        AddPatch(root, layout, backupName, backupInBlobText, 48, 8, lastUsable,
            "Update Backup Header with LastUseableLBA.");
        AddPatch(root, layout, DiskPatchTarget, backupHeader, 48, 8, lastUsable,
            "Update Backup Header with LastUseableLBA.");

        AddPatch(root, layout, primaryName, "1", 32, 8, "NUM_DISK_SECTORS-1.",
            "Update Primary Header with BackupGPT Header Location.");
        AddPatch(root, layout, DiskPatchTarget, "1", 32, 8, "NUM_DISK_SECTORS-1.",
            "Update Primary Header with BackupGPT Header Location.");

        AddPatch(root, layout, backupName, backupInBlobText, 24, 8, "NUM_DISK_SECTORS-1.",
            "Update Backup Header with CurrentLBA.");
        AddPatch(root, layout, DiskPatchTarget, backupHeader, 24, 8, "NUM_DISK_SECTORS-1.",
            "Update Backup Header with CurrentLBA.");

        var backupArray = FormattableString.Invariant($"NUM_DISK_SECTORS-{layout.BackupBlobSectors}.");
        AddPatch(root, layout, backupName, backupInBlobText, 72, 8, backupArray,
            "Update Backup Header with Partition Array Location.");
        AddPatch(root, layout, DiskPatchTarget, "NUM_DISK_SECTORS-1", 72, 8, backupArray,
            "Update Backup Header with Partition Array Location.");

        var arrayCrc = FormattableString.Invariant($"CRC32(2,{crcBytes})");
        AddPatch(root, layout, primaryName, "1", 88, 4, arrayCrc,
            "Update Primary Header with CRC of Partition Array.");
        AddPatch(root, layout, DiskPatchTarget, "1", 88, 4, arrayCrc,
            "Update Primary Header with CRC of Partition Array.");
        var backupArrayCrc = FormattableString.Invariant($"CRC32(0,{crcBytes})");
        var backupArrayDiskCrc = FormattableString.Invariant(
            $"CRC32(NUM_DISK_SECTORS-{layout.BackupBlobSectors}.,{crcBytes})");
        AddPatch(root, layout, backupName, backupInBlobText, 88, 4, backupArrayCrc,
            "Update Backup Header with CRC of Partition Array.");
        AddPatch(root, layout, DiskPatchTarget, backupHeader, 88, 4, backupArrayDiskCrc,
            "Update Backup Header with CRC of Partition Array.");

        AddPatch(root, layout, primaryName, "1", 16, 4, "0", "Zero Out Header CRC in Primary Header.");
        AddPatch(root, layout, primaryName, "1", 16, 4, "CRC32(1,92)",
            "Update Primary Header with CRC of Primary Header.");
        AddPatch(root, layout, DiskPatchTarget, "1", 16, 4, "0", "Zero Out Header CRC in Primary Header.");
        AddPatch(root, layout, DiskPatchTarget, "1", 16, 4, "CRC32(1,92)",
            "Update Primary Header with CRC of Primary Header.");

        AddPatch(root, layout, backupName, backupInBlobText, 16, 4, "0", "Zero Out Header CRC in Backup Header.");
        var backupHeaderCrc = FormattableString.Invariant($"CRC32({backupInBlob},92)");
        AddPatch(root, layout, backupName, backupInBlobText, 16, 4, backupHeaderCrc,
            "Update Backup Header with CRC of Backup Header.");
        AddPatch(root, layout, DiskPatchTarget, backupHeader, 16, 4, "0", "Zero Out Header CRC in Backup Header.");
        var backupHeaderDiskCrc = "CRC32(NUM_DISK_SECTORS-1.,92)";
        AddPatch(root, layout, DiskPatchTarget, backupHeader, 16, 4, backupHeaderDiskCrc,
            "Update Backup Header with CRC of Backup Header.");
        return Document(root);
    }

    /// <summary>
    /// Builds the partition description text for one layout in the <c>configuration</c> shape <c>ptool.py</c> and
    /// <c>gen_partition.py</c> read, including the parser instructions those tools emit.
    /// <para>为一份布局构建 <c>ptool.py</c> 与 <c>gen_partition.py</c> 可读的 <c>configuration</c> 形状分区描述文本，
    /// 并带上这两个工具发出的解析指令。</para>
    /// </summary>
    /// <param name="layout">The table geometry and entries. <para>表几何与条目。</para></param>
    /// <returns>The partition XML text. <para>partition XML 文本。</para></returns>
    /// <exception cref="ArgumentNullException">The layout is null. <para>布局为空。</para></exception>
    internal static string BuildPartition(EdlGptXmlLayout layout)
        => BuildPartition(layout, null);

    /// <summary>
    /// Builds the partition description text for one layout, letting a caller bind each partition row to an
    /// existing image file name.
    /// <para>为一份布局构建分区描述文本，允许调用方把每条分区条目绑定到已存在的镜像文件名。</para>
    /// </summary>
    /// <param name="layout">The table geometry and entries. <para>表几何与条目。</para></param>
    /// <param name="imageBinding">Maps a partition to its image file name, or returns <see langword="null"/> to keep
    /// an empty file name. <para>把分区映射到其镜像文件名；返回 <see langword="null"/> 时保留空文件名。</para></param>
    /// <returns>The partition XML text. <para>partition XML 文本。</para></returns>
    /// <exception cref="ArgumentNullException">The layout is null. <para>布局为空。</para></exception>
    internal static string BuildPartition(EdlGptXmlLayout layout, Func<GptPartition, string?>? imageBinding)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var root = new XElement("configuration");
        root.Add(new XElement("parser_instructions", FormattableString.Invariant(
            $"\n\tWRITE_PROTECT_BOUNDARY_IN_KB=0\n\tSECTOR_SIZE_IN_BYTES = {layout.SectorSize}\n\tGROW_LAST_PARTITION_TO_FILL_DISK=true\n\t")));
        var physical = new XElement("physical_partition");
        foreach (var partition in layout.Partitions)
        {
            if (partition.TypeId == Guid.Empty)
            {
                // A zero type GUID marks the grow placeholder, which ptool adds itself; it has no partition entry.
                // 零类型 GUID 标记 grow 占位，由 ptool 自行添加，不属于分区条目。
                continue;
            }

            var label = FileLabel(partition);
            physical.Add(new XElement("partition",
                new XAttribute("label", label),
                new XAttribute("size_in_kb", PartitionSectors(partition) * layout.SectorSize / 1024),
                new XAttribute("type", partition.TypeId.ToString().ToUpperInvariant()),
                new XAttribute("bootable", (partition.Attributes & (1UL << 2)) != 0 ? "true" : "false"),
                new XAttribute("readonly", (partition.Attributes & (1UL << 60)) != 0 ? "true" : "false"),
                new XAttribute("filename", imageBinding?.Invoke(partition) ?? string.Empty)));
        }

        root.Add(physical);
        return Document(root);
    }

    /// <summary>The patch target that addresses the just-programmed raw disk.
    /// <para>寻址刚完成编程的裸盘的 patch 目标。</para></summary>
    private const string DiskPatchTarget = "DISK";

    private static void AddPatch(XElement root, EdlGptXmlLayout layout, string filename, string startSector,
        long byteOffset, int sizeInBytes, string value, string what)
        => root.Add(new XElement("patch",
            new XAttribute("start_sector", startSector),
            new XAttribute("byte_offset", byteOffset.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("physical_partition_number", layout.Lun.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("size_in_bytes", sizeInBytes.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("value", value),
            new XAttribute("filename", filename),
            new XAttribute("SECTOR_SIZE_IN_BYTES", layout.SectorSize.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("what", what)));

    private static IEnumerable<XAttribute> ProgramAttributes(EdlGptXmlLayout layout, string label, string fileName,
        ulong startLba, long sectors, string partOfSingleImage)
        =>
        [
            new XAttribute("SECTOR_SIZE_IN_BYTES", layout.SectorSize.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("file_sector_offset", "0"),
            new XAttribute("filename", fileName),
            new XAttribute("label", label),
            new XAttribute("num_partition_sectors", sectors.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("partofsingleimage", partOfSingleImage),
            new XAttribute("physical_partition_number", layout.Lun.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("readbackverify", "false"),
            new XAttribute("size_in_KB", SizeInKb(sectors, layout.SectorSize)),
            new XAttribute("sparse", "false"),
            new XAttribute("start_byte_hex", FormattableString.Invariant($"0x{startLba * (ulong)layout.SectorSize:x}")),
            new XAttribute("start_sector", startLba.ToString(CultureInfo.InvariantCulture))
        ];

    private static long PartitionSectors(GptPartition partition)
        => partition.LastLba >= partition.FirstLba
            ? checked((long)(partition.LastLba - partition.FirstLba + 1UL))
            : 0;

    private static string SizeInKb(long sectors, long sectorSize)
        => sectors <= 0
            ? "0"
            : (sectors * sectorSize / 1024d).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// Turns one table label into a file-name stem: separators, invalid characters and reserved device names are
    /// replaced so a hostile label can never escape the package directory or produce an unreadable member, and an empty
    /// label falls back to its slot position.
    /// <para>把表内标签转成文件名词干：分隔符、非法字符与保留设备名被替换，使恶意标签无法逃出包目录或产生不可读的成员；
    /// 空标签回退到其槽位序号。</para>
    /// </summary>
    /// <param name="partition">The table entry. <para>表条目。</para></param>
    /// <returns>A file-name-safe stem. <para>可安全用作文件名的词干。</para></returns>
    internal static string FileLabel(GptPartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return SafeFileStem(partition.Name, FormattableString.Invariant($"partition{partition.Index}"));
    }

    /// <summary>
    /// Sanitises an arbitrary label into a file-name stem that the bundle parser accepts.
    /// <para>把任意标签净化为包解析器可接受的文件名词干。</para>
    /// </summary>
    /// <param name="label">The untrusted label. <para>不可信标签。</para></param>
    /// <param name="fallback">Stem used when nothing usable remains. <para>没有可用字符时使用的词干。</para></param>
    /// <returns>The sanitised stem. <para>净化后的词干。</para></returns>
    internal static string SafeFileStem(string label, string fallback)
    {
        var builder = new StringBuilder(label.Length);
        foreach (var ch in label)
        {
            builder.Append(ch is '/' or '\\' || PathSearchValues.InvalidFileName.Contains(ch) ? '_' : ch);
        }

        var stem = builder.ToString().Trim().TrimEnd('.');
        if (stem.Length == 0)
        {
            return fallback;
        }

        return ReservedDeviceNames.Contains(stem.Split('.')[0], StringComparer.OrdinalIgnoreCase)
            ? "_" + stem
            : stem;
    }

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    private static string Document(XElement root)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" ?>");
        builder.Append(root);
        builder.AppendLine();
        return builder.ToString();
    }
}