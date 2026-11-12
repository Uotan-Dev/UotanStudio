using System.Globalization;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Services.Flash;

/// <summary>
/// The geometry a generated EDL XML package is built from: sector size, disk length, the region (LUN) and the two
/// GPT array locations of one validated table.
/// <para>生成 EDL XML 包所用的几何：扇区大小、磁盘长度、区域（LUN）以及一张已校验表的两处 GPT 数组位置。</para>
/// </summary>
/// <param name="Lun">Physical partition number the package addresses. <para>包所寻址的物理分区号。</para></param>
/// <param name="SectorSize">Logical sector size in bytes. <para>逻辑扇区字节数。</para></param>
/// <param name="DiskSectors">Total sector count of the region. <para>该区域的总扇区数。</para></param>
/// <param name="LastUsableLba">Last usable LBA declared by the table. <para>表声明的最后一个可用 LBA。</para></param>
/// <param name="PartitionEntryCount">Declared entry-array capacity. <para>声明的条目数组容量。</para></param>
/// <param name="PartitionEntrySize">Declared size of one entry. <para>声明的单条目字节数。</para></param>
/// <param name="PrimaryArrayLba">LBA of the primary entry array. <para>主条目数组的 LBA。</para></param>
/// <param name="BackupArrayLba">LBA of the backup entry array. <para>备份条目数组的 LBA。</para></param>
/// <param name="Partitions">The table's non-empty entries. <para>表中的非空条目。</para></param>
internal sealed record EdlGptXmlLayout(int Lun, long SectorSize, long DiskSectors, ulong LastUsableLba,
    uint PartitionEntryCount, uint PartitionEntrySize, long PrimaryArrayLba, long BackupArrayLba,
    IReadOnlyList<GptPartition> Partitions)
{
    /// <summary>Gets the byte length of the entry array. <para>获取条目数组的字节长度。</para></summary>
    /// <value>Entry count multiplied by entry size. <para>条目数量乘以条目字节数。</para></value>
    internal long ArrayBytes => (long)PartitionEntryCount * PartitionEntrySize;

    /// <summary>Gets the sector count the primary array occupies. <para>获取主条目数组占用的扇区数。</para></summary>
    /// <value>Array bytes rounded up to whole sectors. <para>数组字节数向上取整到整扇区。</para></value>
    internal long ArraySectors => (ArrayBytes + SectorSize - 1) / SectorSize;

    /// <summary>Gets the sector count of the primary GPT blob, measured from LBA zero.
    /// <para>获取主 GPT 镜像的扇区数，从 LBA 0 起算。</para></summary>
    /// <value>Primary array LBA plus its own sector count, covering the protective MBR, the header and the array.
    /// <para>主数组 LBA 加上它自身的扇区数，覆盖保护性 MBR、头部与数组。</para></value>
    internal long PrimarySectors => PrimaryArrayLba + ArraySectors;

    /// <summary>Gets the disk-relative offset of the backup entry array. <para>获取备份条目数组的磁盘相对偏移。</para></summary>
    /// <value><c>DiskSectors - BackupArrayLba</c>, the value the <c>NUM_DISK_SECTORS-</c> form subtracts.
    /// <para><c>DiskSectors - BackupArrayLba</c>，即 <c>NUM_DISK_SECTORS-</c> 形式所减去的值。</para></value>
    internal long BackupArrayOffset => DiskSectors - BackupArrayLba;

    /// <summary>Gets the sector count of the backup GPT blob, measured back from the last sector.
    /// <para>获取备份 GPT 镜像的扇区数，从最后一个扇区往回计算。</para></summary>
    /// <value>The backup array offset, covering the array, any padding and the backup header.
    /// <para>备份数组偏移，覆盖数组、可能的填充以及备份头。</para></value>
    internal long BackupSectors => BackupArrayOffset;

    /// <summary>Gets the disk-relative offset of the last usable LBA. <para>获取最后可用 LBA 的磁盘相对偏移。</para></summary>
    /// <value><c>DiskSectors - LastUsableLba</c>. <para><c>DiskSectors - LastUsableLba</c>。</para></value>
    internal long LastUsableOffset => DiskSectors - checked((long)LastUsableLba);
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
/// Writes a device partition table as the Qualcomm toolchain layout: the two GPT blobs read back from the region plus
/// rawprogram, patch and partition XML documents that describe and restore it.
/// <para>把设备分区表写成高通工具链布局：从该区域读回的两份 GPT 镜像，以及描述并恢复它的 rawprogram、patch 与
/// partition XML。</para>
/// </summary>
/// <remarks>
/// The rawprogram rows follow the shape <c>gpt2xml.py</c> produces (one program row per partition, then PrimaryGPT and
/// BackupGPT rows), and the patch rows follow <c>ptool.py</c> (last usable LBA, backup header location, array location,
/// array CRC and header CRC, expressed against <c>NUM_DISK_SECTORS</c>). Patch rows target <c>DISK</c> only: the bundle
/// runner sends patches after the program rows, so they fix the header of what was just written, which is where the
/// disk-relative values and the target-computed CRCs belong.
/// <para>rawprogram 条目沿用 <c>gpt2xml.py</c> 的形状（每个分区一条 program 条目，随后是 PrimaryGPT 与 BackupGPT 条目），
/// patch 条目沿用 <c>ptool.py</c>（最后可用 LBA、备份头位置、数组位置、数组 CRC 与头部 CRC，均以 <c>NUM_DISK_SECTORS</c>
/// 相对值表达）。patch 只以 <c>DISK</c> 为目标：包执行器在 program 之后发送 patch，因此它们修补刚写下的头部，磁盘相对值与
/// 目标端计算的 CRC 正属于那里。</para>
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
        var layout = Describe(snapshot);
        Directory.CreateDirectory(directoryPath);
        var primaryPath = Path.Combine(directoryPath, PrimaryGptFileName);
        var backupPath = Path.Combine(directoryPath, BackupGptFileName);
        await ReadBlobAsync(firehose, layout, 0, layout.PrimarySectors, PrimaryGptFileName, primaryPath,
            cancellationToken).ConfigureAwait(false);
        await ReadBlobAsync(firehose, layout, layout.BackupArrayLba, layout.BackupSectors, BackupGptFileName,
            backupPath, cancellationToken).ConfigureAwait(false);

        var rawProgramPath = Path.Combine(directoryPath, RawProgramFileName);
        var patchPath = Path.Combine(directoryPath, PatchFileName);
        var partitionPath = Path.Combine(directoryPath, PartitionFileName);
        await File.WriteAllTextAsync(rawProgramPath, BuildRawProgram(layout), new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(patchPath, BuildPatch(layout), new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
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
            (ulong)table.BackupEntryArrayLba >= (ulong)geometry.SectorCount)
        {
            throw new InvalidOperationException(
                "The table's entry-array locations cannot be expressed as one primary and one backup blob. " +
                "该表的条目数组位置无法表达为主备两份镜像。");
        }

        return new EdlGptXmlLayout(geometry.PhysicalPartitionNumber, geometry.SectorSize, geometry.SectorCount,
            table.LastUsableLba, table.PartitionEntryCount, table.PartitionEntrySize,
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
    {
        ArgumentNullException.ThrowIfNull(layout);
        var root = new XElement("data");
        root.Add(new XComment("NOTE: This is an ** Autogenerated file **"));
        root.Add(new XComment(FormattableString.Invariant($"NOTE: Sector size is {layout.SectorSize}bytes")));
        root.Add(new XComment(
            "NOTE: Generated from the device partition table by UotanStudio; PrimaryGPT/BackupGPT carry the " +
            "table itself."));
        foreach (var partition in layout.Partitions)
        {
            var sectors = PartitionSectors(partition);
            var label = FileLabel(partition);
            root.Add(new XElement("program", ProgramAttributes(layout, label, label + ".img", partition.FirstLba,
                sectors, "false")));
        }

        root.Add(new XElement("program", ProgramAttributes(layout, "PrimaryGPT", PrimaryGptFileName, 0,
            layout.PrimarySectors, "true")));
        root.Add(new XElement("program", new XAttribute("SECTOR_SIZE_IN_BYTES",
            layout.SectorSize.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("file_sector_offset", "0"),
            new XAttribute("filename", BackupGptFileName),
            new XAttribute("label", "BackupGPT"),
            new XAttribute("num_partition_sectors", layout.BackupSectors.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("partofsingleimage", "true"),
            new XAttribute("physical_partition_number", layout.Lun.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("readbackverify", "false"),
            new XAttribute("size_in_KB", SizeInKb(layout.BackupSectors, layout.SectorSize)),
            new XAttribute("sparse", "false"),
            new XAttribute("start_byte_hex", FormattableString.Invariant(
                $"({layout.SectorSize}*NUM_DISK_SECTORS)-{layout.BackupSectors * layout.SectorSize}.")),
            new XAttribute("start_sector", FormattableString.Invariant($"NUM_DISK_SECTORS-{layout.BackupSectors}."))));
        return Document(root);
    }

    /// <summary>
    /// Builds the patch manifest text for one layout; every row targets <c>DISK</c>.
    /// <para>为一份布局构建 patch 清单文本；每条都以 <c>DISK</c> 为目标。</para>
    /// </summary>
    /// <param name="layout">The table geometry. <para>表几何。</para></param>
    /// <returns>The patch XML text. <para>patch XML 文本。</para></returns>
    /// <exception cref="ArgumentNullException">The layout is null. <para>布局为空。</para></exception>
    internal static string BuildPatch(EdlGptXmlLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var root = new XElement("patches");
        root.Add(new XComment("NOTE: This is an ** Autogenerated file **"));
        root.Add(new XComment("NOTE: Applied by the target after the program rows write the GPT blobs."));
        var lastUsable = FormattableString.Invariant($"NUM_DISK_SECTORS-{layout.LastUsableOffset}.");
        var backupHeader = "NUM_DISK_SECTORS-1.";
        var backupArray = FormattableString.Invariant($"NUM_DISK_SECTORS-{layout.BackupArrayOffset}.");
        AddPatch(root, layout, "1", 48, 8, lastUsable, "PrimaryGPT",
            "Update Primary Header with LastUseableLBA.");
        AddPatch(root, layout, "1", 32, 8, backupHeader, "PrimaryGPT",
            "Update Primary Header with BackupGPT Header Location.");
        AddPatch(root, layout, "1", 88, 4,
            FormattableString.Invariant($"CRC32({layout.PrimaryArrayLba},{layout.ArrayBytes})"), "PrimaryGPT",
            "Update Primary Header with CRC of Partition Array.");
        AddPatch(root, layout, "1", 16, 4, "0", "PrimaryGPT", "Zero Out Header CRC in Primary Header.");
        AddPatch(root, layout, "1", 16, 4, "CRC32(1,92)", "PrimaryGPT",
            "Update Primary Header with CRC of Primary Header.");
        AddPatch(root, layout, backupHeader, 48, 8, lastUsable, "BackupGPT",
            "Update Backup Header with LastUseableLBA.");
        AddPatch(root, layout, backupHeader, 24, 8, backupHeader, "BackupGPT",
            "Update Backup Header with CurrentLBA.");
        AddPatch(root, layout, backupHeader, 72, 8, backupArray, "BackupGPT",
            "Update Backup Header with Partition Array Location.");
        AddPatch(root, layout, backupHeader, 88, 4,
            FormattableString.Invariant($"CRC32({backupArray},{layout.ArrayBytes})"), "BackupGPT",
            "Update Backup Header with CRC of Partition Array.");
        AddPatch(root, layout, backupHeader, 16, 4, "0", "BackupGPT", "Zero Out Header CRC in Backup Header.");
        AddPatch(root, layout, backupHeader, 16, 4,
            FormattableString.Invariant($"CRC32({backupHeader},92)"), "BackupGPT",
            "Update Backup Header with CRC of Backup Header.");
        return Document(root);
    }

    /// <summary>
    /// Builds the partition description text for one layout, in the shape <c>ptool.py</c> reads.
    /// <para>为一份布局构建 <c>ptool.py</c> 可读形状的分区描述文本。</para>
    /// </summary>
    /// <param name="layout">The table geometry and entries. <para>表几何与条目。</para></param>
    /// <returns>The partition XML text. <para>partition XML 文本。</para></returns>
    /// <exception cref="ArgumentNullException">The layout is null. <para>布局为空。</para></exception>
    internal static string BuildPartition(EdlGptXmlLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var physical = new XElement("physical_partition");
        foreach (var partition in layout.Partitions)
        {
            var label = FileLabel(partition);
            physical.Add(new XElement("partition",
                new XAttribute("label", label),
                new XAttribute("size_in_kb", PartitionSectors(partition) * layout.SectorSize / 1024),
                new XAttribute("type", partition.TypeId.ToString().ToUpperInvariant()),
                new XAttribute("uguid", partition.Id.ToString().ToUpperInvariant()),
                new XAttribute("filename", label + ".img"),
                new XAttribute("bootable", "false"),
                new XAttribute("readonly", "false")));
        }

        var root = new XElement("partition_list", physical);
        return Document(root);
    }

    private static void AddPatch(XElement root, EdlGptXmlLayout layout, string startSector, int byteOffset,
        int sizeInBytes, string value, string label, string what)
        => root.Add(new XElement("patch",
            new XAttribute("start_sector", startSector),
            new XAttribute("byte_offset", byteOffset.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("physical_partition_number", layout.Lun.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("size_in_bytes", sizeInBytes.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("value", value),
            new XAttribute("filename", "DISK"),
            new XAttribute("SECTOR_SIZE_IN_BYTES", layout.SectorSize.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("label", label),
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
        => checked((long)(partition.LastLba - partition.FirstLba + 1UL));

    private static string SizeInKb(long sectors, long sectorSize)
        => (sectors * sectorSize / 1024d).ToString("F1", CultureInfo.InvariantCulture);

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
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(label.Length);
        foreach (var ch in label)
        {
            builder.Append(ch is '/' or '\\' || invalid.Contains(ch) ? '_' : ch);
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