using System.Xml;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.Lenovo.Qsb;

/// <summary>
/// One <c>program</c> element of a QSB rawprogram XML: target sector window, chunk file and the
/// in-file sector offset where payload data begins.
/// <para>QSB rawprogram XML 中的一个 <c>program</c> 元素：目标扇区窗口、分块文件以及负载在文件内的起始扇区偏移。</para>
/// </summary>
/// <param name="Label">Declared partition label. <para>声明的分区标签。</para></param>
/// <param name="Filename">Chunk file name relative to the XML directory.
/// <para>相对于 XML 所在目录的分块文件名。</para></param>
/// <param name="StartSector">First target sector of the window. <para>窗口内第一个目标扇区。</para></param>
/// <param name="NumPartitionSectors">Declared sector count of the window. <para>声明的窗口扇区数。</para></param>
/// <param name="FileSectorOffset">Sector offset inside the chunk file before payload data starts.
/// <para>分块文件内负载开始前的扇区偏移。</para></param>
/// <param name="SectorSizeInBytes">Declared sector size; zero when the element omitted the attribute.
/// <para>声明的扇区大小；元素未提供该属性时为 0。</para></param>
internal sealed record QsbProgram(string Label, string Filename, long StartSector, long NumPartitionSectors,
    long FileSectorOffset, long SectorSizeInBytes);

/// <summary>
/// Bounded parser for the Lenovo/ZUK QSB rawprogram XML and the chunk-merge layout proven by the
/// reference merger: chunk files named <c>{prefix}_{n}.{ext}</c> are ordered by <c>n</c>, written at
/// <c>start_sector * SECTOR_SIZE_IN_BYTES</c> with zero-filled sector gaps and per-file
/// <c>file_sector_offset</c> padding, and the first chunk is probed for an ext4 superblock
/// (magic 0xEF53 at byte 1080, block count at 1028, log block size at 1048) to extend the merged
/// image to the filesystem's declared total size.
/// <para>Lenovo/ZUK QSB rawprogram XML 的有界解析器，及其参考合并器证实的分块合并布局：
/// 名为 <c>{prefix}_{n}.{ext}</c> 的分块按 <c>n</c> 排序，写入 <c>start_sector * SECTOR_SIZE_IN_BYTES</c>
/// 处，扇区间隙与每文件的 <c>file_sector_offset</c> 用零填充；并探测首分块的 ext4 超级块
/// （0xEF53 魔数位于字节 1080，块数位于 1028，对数块大小位于 1048），把合并镜像扩展到文件系统声明的总大小。</para>
/// </summary>
internal static class QsbRawprogram
{
    /// <summary>Hard ceiling on the number of parsed program elements.
    /// <para>已解析 program 元素数量的硬性上限。</para></summary>
    public const int MaximumPrograms = 4096;

    private const long MaximumXmlBytes = 16L * 1024 * 1024;
    private const int Ext4SuperblockMagicOffset = 1080;

    /// <summary>
    /// Parses the rawprogram XML from a stream, rejecting oversized or malformed documents.
    /// <para>从流解析 rawprogram XML，拒绝超大或格式错误的文档。</para>
    /// </summary>
    /// <param name="input">Readable XML stream; consumed from the start and left open.
    /// <para>可读的 XML 流；从起始处消费并保持打开。</para></param>
    /// <param name="cancellationToken">Cancels parsing. <para>取消本次解析。</para></param>
    /// <returns>Program elements in document order. <para>按文档顺序排列的 program 元素。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read. <para>流不可读。</para></exception>
    /// <exception cref="InvalidDataException">The XML is malformed, a required sector attribute is
    /// missing or non-numeric, or the program count exceeds the ceiling.
    /// <para>XML 格式错误、必需扇区属性缺失或非数值、program 数量超过上限。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static IReadOnlyList<QsbProgram> Parse(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("A readable stream is required.", nameof(input));
        }

        var programs = new List<QsbProgram>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
            MaxCharactersInDocument = MaximumXmlBytes * 2,
            MaxCharactersFromEntities = 0
        };
        try
        {
            using var reader = XmlReader.Create(input, settings);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "program")
                {
                    continue;
                }

                if (programs.Count == MaximumPrograms)
                {
                    throw new InvalidDataException("The QSB rawprogram declares too many program elements.");
                }

                var filename = reader["filename"] ?? "";
                programs.Add(new QsbProgram(
                    reader["label"] ?? "",
                    filename,
                    ReadAttribute(reader, "start_sector", programs.Count),
                    ReadAttribute(reader, "num_partition_sectors", programs.Count),
                    ReadAttribute(reader, "file_sector_offset", programs.Count),
                    reader["SECTOR_SIZE_IN_BYTES"] is { } sector
                        ? ReadSectorSize(sector, programs.Count)
                        : 0));
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"The QSB rawprogram XML is malformed: {exception.Message}", exception);
        }

        return programs;
    }

    /// <summary>
    /// Groups chunk programs by the <c>{prefix}_{n}</c> file-name convention in ascending <c>n</c> order.
    /// <para>按 <c>{prefix}_{n}</c> 文件名约定将分块程序分组，并按 <c>n</c> 升序排列。</para>
    /// </summary>
    /// <param name="programs">Parsed programs, any order. <para>已解析的 program 元素，顺序不限。</para></param>
    /// <returns>Groups keyed by prefix with their ordered members; non-chunk programs are excluded.
    /// <para>按前缀分组的有序结果；非分块程序被排除。</para></returns>
    public static IReadOnlyList<(string Prefix, List<QsbProgram> Members)> GroupChunks(
        IReadOnlyList<QsbProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        var groups = new Dictionary<string, List<(int Order, QsbProgram Program)>>(StringComparer.Ordinal);
        foreach (var program in programs)
        {
            var underscore = program.Filename.IndexOf('_');
            var dot = program.Filename.IndexOf('.', underscore + 1);
            if (underscore <= 0 || dot <= underscore + 1
                || !int.TryParse(program.Filename.AsSpan(underscore + 1, dot - underscore - 1), out var order))
            {
                continue;
            }

            var prefix = program.Filename[..underscore];
            if (!groups.TryGetValue(prefix, out var members))
            {
                members = [];
                groups[prefix] = members;
            }

            members.Add((order, program));
        }

        return groups
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value.OrderBy(m => m.Order).Select(m => m.Program).ToList()))
            .ToList();
    }

    /// <summary>
    /// Merges one chunk group into <c>{prefix}.img</c> inside the output directory, following the
    /// reference sector-window layout with bounded writes.
    /// <para>将一组分块按参考扇区窗口布局合并为输出目录中的 <c>{prefix}.img</c>，写入量有界。</para>
    /// </summary>
    /// <param name="baseDirectory">Directory containing the chunk files. <para>包含分块文件的目录。</para></param>
    /// <param name="prefix">Group prefix taken from <see cref="GroupChunks"/>. <para>来自 <see cref="GroupChunks"/> 的组前缀。</para></param>
    /// <param name="members">Ordered chunk programs of the group. <para>组内有序的分块程序。</para></param>
    /// <param name="outputDirectory">Destination directory for the merged image. <para>合并镜像的目标目录。</para></param>
    /// <param name="maximumOutputBytes">Ceiling for the merged image size. <para>合并镜像的大小上限。</para></param>
    /// <param name="cancellationToken">Cancels the merge. <para>取消本次合并。</para></param>
    /// <returns>Absolute path of the merged image. <para>合并镜像的绝对路径。</para></returns>
    /// <exception cref="InvalidDataException">A chunk file is missing, a sector computation overflows or
    /// exceeds the output ceiling, or a file name escapes the base directory.
    /// <para>分块缺失、扇区计算溢出或超出输出上限、文件名逃出基准目录。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static async Task<string> MergeGroupAsync(string baseDirectory, string prefix,
        IReadOnlyList<QsbProgram> members, string outputDirectory, long maximumOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);
        var destination = Path.Combine(Path.GetFullPath(outputDirectory), prefix + ".img");
        Directory.CreateDirectory(outputDirectory);
        var written = 0L;
        long currentSector = 0;
        long baseSector = 0;
        long totalSectors = 0;
        long sectorSize = 512;
        var first = true;
        await using (var output = File.Create(destination))
        {
            foreach (var program in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkPath = ResolveChunkPath(baseDirectory, program.Filename);
                if (!File.Exists(chunkPath))
                {
                    throw new InvalidDataException($"QSB chunk file is missing: {program.Filename}");
                }

                if (first)
                {
                    if (program.SectorSizeInBytes > 0)
                    {
                        sectorSize = program.SectorSizeInBytes;
                    }

                    baseSector = program.StartSector;
                    totalSectors = ProbeExt4Sectors(chunkPath, sectorSize);
                }

                var fillSectors = currentSector > 0 && currentSector < program.StartSector
                    ? program.StartSector - currentSector
                    : 0;
                fillSectors = checked(fillSectors + program.FileSectorOffset);
                var fillBytes = checked(fillSectors * sectorSize);
                written = checked(written + fillBytes);
                if (written > maximumOutputBytes)
                {
                    throw new InvalidDataException("The merged QSB image exceeds the configured output limit.");
                }

                if (fillBytes > 0)
                {
                    ZeroFill.WriteZeros(output, fillBytes, cancellationToken);
                }

                await using (var chunk = File.OpenRead(chunkPath))
                {
                    written = checked(written + chunk.Length);
                    if (written > maximumOutputBytes)
                    {
                        throw new InvalidDataException("The merged QSB image exceeds the configured output limit.");
                    }

                    await chunk.CopyToAsync(output, cancellationToken);
                }

                currentSector = checked(program.StartSector + program.NumPartitionSectors);
                first = false;
            }

            if (totalSectors > 0)
            {
                var extraSectors = checked(baseSector + totalSectors - currentSector);
                if (extraSectors > 0)
                {
                    var extraBytes = checked(extraSectors * sectorSize);
                    written = checked(written + extraBytes);
                    if (written > maximumOutputBytes)
                    {
                        throw new InvalidDataException("The merged QSB image exceeds the configured output limit.");
                    }

                    ZeroFill.WriteZeros(output, extraBytes, cancellationToken);
                }
            }
        }

        return destination;
    }

    private static long ProbeExt4Sectors(string chunkPath, long sectorSize)
    {
        const int probeLength = Ext4SuperblockMagicOffset + 2;
        using var stream = File.OpenRead(chunkPath);
        if (stream.Length < probeLength || sectorSize <= 0)
        {
            return 0;
        }

        var header = new byte[probeLength];
        stream.ReadExactly(header, 0, probeLength);
        if (header[Ext4SuperblockMagicOffset] != 0x53 || header[Ext4SuperblockMagicOffset + 1] != 0xEF)
        {
            return 0;
        }

        var totalBlocks = (uint)BitConverter.ToInt32(header, 1028);
        var logBlockSize = BitConverter.ToInt32(header, 1048);
        if (logBlockSize is < 0 or > 16)
        {
            return 0;
        }

        try
        {
            var totalSize = checked((long)totalBlocks * 1024L << logBlockSize);
            return totalSize / sectorSize;
        }
        catch (OverflowException)
        {
            return 0;
        }
    }

    private static string ResolveChunkPath(string baseDirectory, string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || Path.IsPathRooted(filename) || filename.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"QSB chunk name is not a safe relative path: {filename}");
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, filename));
    }

    private static long ReadAttribute(XmlReader reader, string name, int programIndex)
    {
        var value = reader[name];
        if (string.IsNullOrWhiteSpace(value) || !long.TryParse(value.Trim(), out var parsed) || parsed < 0)
        {
            throw new InvalidDataException($"QSB program {programIndex} has a missing or invalid {name} attribute.");
        }

        return parsed;
    }

    private static long ReadSectorSize(string value, int programIndex)
    {
        if (!long.TryParse(value.Trim(), out var parsed) || parsed is <= 0 or > 1L << 20)
        {
            throw new InvalidDataException($"QSB program {programIndex} declares an unusable sector size.");
        }

        return parsed;
    }
}