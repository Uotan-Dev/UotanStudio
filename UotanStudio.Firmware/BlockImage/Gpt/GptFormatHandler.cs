using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// Handler for GPT-bearing disk images: detects the EFI PART header, reports the validated partition map, and unpacks by publishing a partition-entry manifest rather than carving payloads. Packing republishes the image so both header copies and entry arrays are rewritten from the validated table with payload bytes intact.
/// <para>承载 GPT 的磁盘镜像处理器：识别 EFI PART 头部、上报已校验的分区表，并以发布分区条目清单的方式“解包”而非切分载荷。打包重新发布镜像，依据已校验表重写两份头部与条目数组，载荷字节保持不变。</para>
/// </summary>
public sealed class GptFormatHandler : IFormatHandler, IFormatProbeHints
{
    private static readonly int[] CandidateBlockSizes = [512, 4096, 16384, 65536];

    /// <summary>Display name published with every operation result of this handler.
    /// <para>本处理器每次操作结果随附的显示名称。</para></summary>
    /// <value>The fixed string <c>GUID Partition Table (GPT)</c>.
    /// <para>固定字符串 <c>GUID Partition Table (GPT)</c>。</para></value>
    public string FormatName => "GUID Partition Table (GPT)";

    /// <summary>Lowercase file extensions this handler advertises for picker filters.
    /// <para>本处理器在文件选择过滤器中声明的小写扩展名。</para></summary>
    /// <value><c>img</c> and <c>dat</c>; both still need an <c>EFI PART</c> header to be recognized.
    /// <para><c>img</c> 与 <c>dat</c>；两者仍需 <c>EFI PART</c> 头部才能被识别。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img", "dat"];

    /// <summary>
    /// Confirms a GPT image from a pre-read header by checking for the <c>EFI PART</c> signature at each
    /// candidate block alignment, mirroring <see cref="CanHandle"/>'s physical offsets without any file
    /// access; a header too short to reach an alignment never matches there.
    /// <para>通过在候选块对齐处检查 <c>EFI PART</c> 签名，从预先读取的头部确认 GPT 镜像，复刻 <see cref="CanHandle"/>
    /// 的物理偏移且不访问文件；短到无法触及某个对齐的头部在该处永不匹配。</para>
    /// </summary>
    /// <param name="header">Leading bytes the caller already read. <para>调用方预先读入的起始字节。</para></param>
    /// <param name="path">Read-from path; unused here, never opened. <para>读取来源路径；此处不使用，绝不打开。</para></param>
    /// <returns><see langword="true"/> when the signature is present at a candidate alignment. <para>某个候选对齐处存在签名时返回 <see langword="true"/>。</para></returns>
    bool IFormatProbeHints.MatchesMagic(ReadOnlySpan<byte> header, string path)
    {
        foreach (var blockSize in CandidateBlockSizes)
        {
            if (header.Length >= blockSize + "EFI PART"u8.Length
                && header.Slice(blockSize, "EFI PART"u8.Length).SequenceEqual("EFI PART"u8))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Determines whether the file carries a GPT header at a candidate block alignment.
    /// <para>判断文件是否在候选块对齐处携带 GPT 头部。</para></summary>
    /// <param name="filePath">Candidate disk image; only the first byte of each candidate header block is probed for the <c>EFI PART</c> signature. GPT headers do not store the logical block size, so the probe tries the conventional 512-byte alignment first and then 4Kn-style alignments.
    /// <para>候选磁盘镜像；仅在各候选头部块的首字节探测 <c>EFI PART</c> 签名。GPT 头部不存储逻辑块大小， 因此先按常见的 512 字节对齐探测，再尝试 4Kn 类对齐。</para></param>
    /// <returns><see langword="true"/> when a signature matches at any probed offset; <see langword="false"/> for blank, missing or unreadable files (all I/O errors are swallowed).
    /// <para>任一探测偏移命中签名时为 <see langword="true"/>；空白、缺失或不可读文件为 <see langword="false"/>（吞掉所有 I/O 错误）。</para></returns>
    public bool CanHandle(string filePath)
    {
        foreach (var blockSize in CandidateBlockSizes)
        {
            if (HeaderProbe.ProbeMagic(filePath, blockSize * 2, blockSize, "EFI PART"u8))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Reads and validates the GPT pair and reports the partition map.
    /// <para>读取并校验 GPT 主备对，并报告分区映射。</para></summary>
    /// <param name="filePath">Disk image file; the block size is detected from the header position and the block count is derived from the file length, so a trailing partial block is ignored.
    /// <para>磁盘镜像文件；块大小由头部位置探测得出，块数量由文件长度推导，因此尾部不完整块会被忽略。</para></param>
    /// <param name="cancellationToken">Checked between the bounded metadata reads.
    /// <para>在各次有界元数据读取之间检查。</para></param>
    /// <returns>Success result with one entry per partition and the layout metadata, or a failure result carrying the validation message when no GPT pair validates.
    /// <para>成功结果按分区各列一条条目并携带布局元数据；主备对无法校验时返回携带校验信息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested during the read.
    /// <para>读取过程中请求了取消。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var table = ReadTable(filePath, cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok(
                $"GPT: {table.Partitions.Count} partitions of {table.PartitionEntryCount} slots, " +
                $"{table.BlockCount} blocks x {table.BlockSize}B.",
                entries: [.. table.Partitions.Select(partition =>
                    $"{partition.Index:D2} {partition.Name} ({partition.FirstLba}-{partition.LastLba})")],
                metadata: BuildMetadata(table)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>Publishes the partition-entry manifest as a tab-separated <c>gpt-partitions.tsv</c> file in the output directory. Partition payloads are not carved; use the filesystem handlers on the raw partition ranges for content extraction, and <c>GptTableWriter</c> to write tables.
    /// <para>在输出目录发布以制表符分隔的分区条目清单 <c>gpt-partitions.tsv</c>。不切除分区数据；内容提取请对原始分区 范围使用文件系统处理器，写入表请使用 <c>GptTableWriter</c>。</para></summary>
    /// <param name="filePath">Disk image file whose GPT pair must validate.
    /// <para>GPT 主备对必须校验通过的磁盘镜像文件。</para></param>
    /// <param name="outputDirectory">Created on demand; receives the manifest via a temporary publication file.
    /// <para>按需创建；经临时发布文件接收清单。</para></param>
    /// <param name="options">Unpack options; <c>Overwrite</c> defaults to enabled and is the only option honoured here. May be <see langword="null"/>.
    /// <para>解包选项；<c>Overwrite</c> 默认开启且是此处唯一生效的选项。可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before reading and while emitting manifest lines; cancellation is rethrown and leaves the destination untouched.
    /// <para>读取前与生成清单行时检查；取消会重新抛出且不改动目标。</para></param>
    /// <returns>Success result naming the manifest, or a failure result for images without a valid GPT pair.
    /// <para>成功结果指明清单文件；不含有效 GPT 主备对的镜像返回失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested before or during the read.
    /// <para>读取之前或过程中请求了取消。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var table = ReadTable(filePath, cancellationToken);
            Directory.CreateDirectory(outputDirectory);
            var manifestPath = Path.Combine(outputDirectory, "gpt-partitions.tsv");
            FileExtraction.WriteAtomic(manifestPath, output =>
            {
                using var writer = new StreamWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.WriteLine("index\ttype_guid\tunique_guid\tfirst_lba\tlast_lba\tattributes\tbyte_offset\tbyte_length\tname");
                foreach (var partition in table.Partitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{partition.Index}\t{partition.TypeId}\t{partition.Id}\t{partition.FirstLba}\t{partition.LastLba}\t{partition.Attributes}\t{partition.ByteOffset}\t{partition.ByteLength}\t{partition.Name}"));
                }
                writer.Flush();
                output.Flush(flushToDisk: true);
            }, options?.Overwrite ?? true, cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok(
                $"Wrote the GPT partition manifest for {table.Partitions.Count} entries to {manifestPath}.",
                entries: [manifestPath],
                metadata: BuildMetadata(table)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>Republishes the image with both GPT header copies and both entry arrays rewritten from the validated table; partition payload bytes are copied untouched.
    /// <para>按已校验的表重写 GPT 的主备头部与两份条目数组后重新发布镜像；分区数据字节原样复制。</para></summary>
    /// <param name="inputPath">Source disk image whose GPT pair must validate.
    /// <para>源磁盘镜像，其 GPT 主备对必须能通过校验。</para></param>
    /// <param name="outputPath">Destination image path, published atomically through a temporary file.
    /// <para>发布后的目标镜像路径，经临时文件原子写入。</para></param>
    /// <param name="cancellationToken">Checked while reading the table and during the image copy.
    /// <para>在读取表与复制镜像期间持续检查。</para></param>
    /// <returns>Success result naming the published image and the restored entry count; a failure result when the table does not validate or its capacity differs from the image length.
    /// <para>成功结果说明发布的镜像与恢复的条目数；表无法校验或容量与镜像不符时返回失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation requested during publication; rethrown so the destination stays untouched.
    /// <para>取消在发布过程中被请求；该异常重新抛出且不改动目标。</para></exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var table = ReadTable(inputPath, cancellationToken);
            GptTableWriter.ApplyToImageFile(inputPath, outputPath, table, cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok(
                $"Republished {outputPath} with {table.Partitions.Count} GPT entries in {table.PartitionEntryCount} slots.",
                entries: [Path.GetFullPath(outputPath)],
                metadata: BuildMetadata(table)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    private static GptTable ReadTable(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var path = Path.GetFullPath(filePath);
        var blockSize = DetectBlockSize(path)
            ?? throw new InvalidDataException("No EFI PART header was found at a supported block alignment.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var blockCount = (ulong)(stream.Length / blockSize);
        return GptReader.Read(stream, blockSize, blockCount, cancellationToken);
    }

    private static int? DetectBlockSize(string path)
    {
        var length = new FileInfo(path).Length;
        Span<byte> signature = stackalloc byte[8];
        foreach (var blockSize in CandidateBlockSizes)
        {
            var offset = (long)blockSize;
            if (length < offset + blockSize)
            {
                continue;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.SequentialScan);
            stream.Position = offset;
            if (stream.Read(signature) == signature.Length && signature.SequenceEqual("EFI PART"u8))
            {
                return blockSize;
            }
        }
        return null;
    }

    private static Dictionary<string, string> BuildMetadata(GptTable table)
    {
        var metadata = new Dictionary<string, string>
        {
            ["DiskId"] = table.DiskId.ToString(),
            ["BlockSize"] = table.BlockSize.ToString(),
            ["BlockCount"] = table.BlockCount.ToString(),
            ["HeaderSize"] = table.HeaderSize.ToString(),
            ["FirstUsableLba"] = table.FirstUsableLba.ToString(),
            ["LastUsableLba"] = table.LastUsableLba.ToString(),
            ["PartitionEntryCount"] = table.PartitionEntryCount.ToString(),
            ["PartitionEntrySize"] = table.PartitionEntrySize.ToString(),
            ["PrimaryEntryArrayLba"] = table.PrimaryEntryArrayLba.ToString(),
            ["BackupEntryArrayLba"] = table.BackupEntryArrayLba.ToString(),
            ["PartitionCount"] = table.Partitions.Count.ToString(),
            ["MetadataSha256"] = table.MetadataSha256
        };
        return metadata;
    }
}