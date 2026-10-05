using System.Buffers.Binary;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BlockImage.Lp;

/// <summary>
/// Format handler for Android logical partition (super) images: detection, metadata reading, extraction and packing.
/// <para>Android 逻辑分区（super）镜像的格式处理器：检测、读取元数据、解包与打包。</para>
/// </summary>
public sealed class LpFormatHandler : IFormatHandler, IRepackMetadataProvider, IFormatProbeHints
{
    /// <summary>
    /// Confirms an LP super image from a pre-read header by checking the little-endian geometry magic
    /// <c>0x616c4467</c> at the reserved-bytes offset 4096, mirroring the handler's own detection offset
    /// without any file access; a header shorter than 4096 plus four bytes never matches.
    /// <para>通过在保留字节偏移 4096 处检查小端几何魔数 <c>0x616c4467</c>，从预先读取的头部确认 LP super 镜像，
    /// 复刻本处理器自身检测所用的偏移且不访问文件；短于 4096 加四字节的头部永不匹配。</para>
    /// </summary>
    /// <param name="header">Leading bytes the caller already read. <para>调用方预先读入的起始字节。</para></param>
    /// <param name="path">Read-from path; unused here, never opened. <para>读取来源路径；此处不使用，绝不打开。</para></param>
    /// <returns><see langword="true"/> when the geometry magic sits at offset 4096. <para>几何魔数位于偏移 4096 时返回 <see langword="true"/>。</para></returns>
    bool IFormatProbeHints.MatchesMagic(ReadOnlySpan<byte> header, string path)
    {
        const int geometryOffset = MetadataFormat.LP_PARTITION_RESERVED_BYTES;
        return header.Length >= geometryOffset + sizeof(uint)
            && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(geometryOffset, sizeof(uint))) == MetadataFormat.LP_METADATA_GEOMETRY_MAGIC;
    }

    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
        metadata.PackingNotes = " The directory packer creates new default LP groups and geometry.";
    }

    /// <summary>
    /// Display name of the handled format.
    /// <para>所处理格式的显示名称。</para>
    /// </summary>
    /// <value>
    /// The fixed string <c>"Android Logical Partition (super)"</c>.
    /// <para>固定字符串 <c>"Android Logical Partition (super)"</c>。</para>
    /// </value>
    public string FormatName => "Android Logical Partition (super)";

    /// <summary>
    /// File extensions this handler advertises for super images.
    /// <para>该处理器为 super 镜像声明的文件扩展名。</para>
    /// </summary>
    /// <value>
    /// <c>img</c>, <c>super</c>, <c>simg</c>, <c>sparse</c> and <c>raw</c>; an extension alone never claims a match, content probing in <see cref="CanHandle"/> decides.
    /// <para>依次为 <c>img</c>、<c>super</c>、<c>simg</c>、<c>sparse</c>、<c>raw</c>；仅凭扩展名不构成匹配，由 <see cref="CanHandle"/> 的内容探测决定。</para>
    /// </value>
    public IReadOnlyList<string> FileExtensions => ["img", "super", "simg", "sparse", "raw"];

    /// <summary>
    /// Android sparse header magic (little-endian 0xED26FF3A). A sparse image's logical bytes differ
    /// from its raw file layout, so the geometry slot check below must be skipped for sparse supers.
    /// <para>Android sparse 魔数（小端 0xED26FF3A）。稀疏镜像的逻辑字节与原始文件布局不同，
    /// 因此下面的几何槽位魔数预检必须对稀疏 super 跳过。</para>
    /// </summary>
    private const uint SparseMagic = 0xED26FF3A;

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to probe; a missing, blank or unreadable path never matches.
    /// <para>待探测的路径；不存在、为空或不可读的路径均不匹配。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when a geometry magic (or a sparse header) is present and the full LP metadata parses without error; every read or parse failure yields <see langword="false"/>.
    /// <para>仅当存在几何魔数（或 sparse 头）且完整 LP 元数据解析无错时返回 <see langword="true"/>；任何读取或解析失败均返回 <see langword="false"/>。</para>
    /// </returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        try
        {
            if (!HasGeometryMagicOrIsSparse(filePath))
            {
                return false;
            }

            using var stream = MetadataReader.OpenImage(filePath);
            _ = MetadataReader.ReadFromImageStream(stream);
            return true;
        }
        catch { return false; }
    }

    private static bool HasGeometryMagicOrIsSparse(string filePath)
        => HeaderProbe.ProbeMagic(filePath, sizeof(uint), 0, SparseMagic)
            || HeaderProbe.ProbeMagic(filePath,
                MetadataFormat.LP_PARTITION_RESERVED_BYTES + sizeof(uint),
                MetadataFormat.LP_PARTITION_RESERVED_BYTES,
                MetadataFormat.LP_METADATA_GEOMETRY_MAGIC)
            || HeaderProbe.ProbeMagic(filePath,
                MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE + sizeof(uint),
                MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE,
                MetadataFormat.LP_METADATA_GEOMETRY_MAGIC)
            || HeaderProbe.ProbeMagic(filePath, sizeof(uint), 0, MetadataFormat.LP_METADATA_GEOMETRY_MAGIC);

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">
    /// Super image path; both raw and Android sparse storage layouts are decoded.
    /// <para>super 镜像路径；原始与 Android sparse 两种存储布局均可解码。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancel token checked before and after the metadata parse; the read itself is synchronous.
    /// <para>在元数据解析前后各检查一次的取消令牌；读取本身为同步执行。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> carrying geometry, per-partition sizes and the partition name list on success, or a failure result when the image cannot be parsed; every non-cancellation error is captured into the result.
    /// <para>成功时返回包含几何信息、各分区大小与分区名列表的 <see cref="FirmwareOpResult"/>，镜像无法解析时返回失败结果；除取消外的所有错误都被捕获进结果。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = UotanStudio.Firmware.Core.FormatDetector.ReadHeader(filePath, sizeof(uint));
            var isSparse = header.Length >= sizeof(uint)
                && BinaryPrimitives.ReadUInt32LittleEndian(header) == SparseMagic;
            var metadata = MetadataReader.ReadFromImageFile(filePath);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new Dictionary<string, string>
            {
                ["Storage"] = isSparse ? "sparse" : "raw",
                ["Logical block size"] = $"{metadata.Geometry.LogicalBlockSize:N0} bytes",
                ["Metadata max size"] = $"{metadata.Geometry.MetadataMaxSize:N0} bytes",
                ["Metadata slots"] = metadata.Geometry.MetadataSlotCount.ToString(),
                ["Super device size"] = $"{(long)metadata.BlockDevices[0].Size:N0} bytes",
                ["Data start"] = $"sector {metadata.BlockDevices[0].FirstLogicalSector:N0}"
            };
            foreach (var partition in metadata.Partitions)
            {
                long partitionBytes = 0;
                foreach (var extent in metadata.Extents.GetRange(checked((int)partition.FirstExtentIndex), checked((int)partition.NumExtents)))
                    partitionBytes = checked(partitionBytes + (long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE);
                result[$"Partition {partition.NameString}"] =
                    $"{partitionBytes:N0} bytes, {partition.NumExtents} extent(s), group {metadata.Groups[(int)partition.GroupIndex].NameString}";
            }
            var summary = $"LP metadata: {metadata.Partitions.Count} partitions, {metadata.Extents.Count} extents, " +
                          $"{metadata.Groups.Count} groups, {metadata.BlockDevices.Count} block devices";
            return Task.FromResult(FirmwareOpResult.Ok(summary, entries: [.. metadata.PartitionNames], metadata: result));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Task.FromResult(FirmwareOpResult.Fail(ex.Message)); }
    }

    /// <summary>
    /// Extracts complete primary-device partitions and zero extents, committing each file atomically.
    /// <para>提取完整的主设备分区及零 extent，逐文件原子提交。</para>
    /// </summary>
    /// <param name="filePath">
    /// Source super image; it is never modified and a partition output may not replace it.
    /// <para>源 super 镜像；镜像本身不会被修改，且分区输出不得指向源镜像。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Directory that receives one <c>&lt;name&gt;.img</c> file per selected partition.
    /// <para>接收每个所选分区对应 <c>&lt;name&gt;.img</c> 文件的目录。</para>
    /// </param>
    /// <param name="options">
    /// Optional selection, overwrite and output-budget settings; <see langword="null"/> uses defaults with overwrite enabled.
    /// <para>可选的选择、覆盖与输出预算设置；<see langword="null"/> 表示使用默认值且允许覆盖。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancel token checked around every partition read and copy; cancellation aborts before partially committed files are replaced.
    /// <para>在每个分区的读取与拷贝前后检查的取消令牌；取消发生在尚未完成提交的文件被替换之前。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> reporting the extracted partition count on success, or a failure result for invalid metadata, budget or path conflicts; only <see cref="OperationCanceledException"/> escapes.
    /// <para>成功时报告提取分区数量的 <see cref="FirmwareOpResult"/>；元数据无效、超出预算或路径冲突时返回失败结果；仅 <see cref="OperationCanceledException"/> 会向外抛出。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    /// <summary>
    /// Extracts selected image entries without creating directory repack metadata.
    /// <para>提取所选镜像条目，且不创建目录重打包元数据。</para>
    /// </summary>
    /// <param name="filePath">
    /// Source super image to read.
    /// <para>要读取的源 super 镜像。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Directory that receives the extracted <c>&lt;name&gt;.img</c> files.
    /// <para>接收解出的 <c>&lt;name&gt;.img</c> 文件的目录。</para>
    /// </param>
    /// <param name="partitions">
    /// Exact partition names to extract, matched case-insensitively; existing outputs are never overwritten.
    /// <para>要提取的分区名称集合，按不区分大小写匹配；绝不覆盖已有输出。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancel token propagated through the extraction loop.
    /// <para>贯穿整个提取循环传递的取消令牌。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> for the selected-partition extraction; format errors are captured into the result.
    /// <para>针对所选分区提取的 <see cref="FirmwareOpResult"/>；格式错误被捕获进结果。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="partitions"/> is empty. <para><paramref name="partitions"/> 为空。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitions"/> is <see langword="null"/>. <para><paramref name="partitions"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public Task<FirmwareOpResult> ExtractSelectedAsync(string filePath, string outputDirectory,
        IReadOnlyCollection<string> partitions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (partitions.Count == 0) throw new ArgumentException("Select at least one partition.", nameof(partitions));
        return UnpackCoreAsync(filePath, outputDirectory,
            new UnpackOptions { SelectedEntries = partitions, Overwrite = false }, cancellationToken);
    }

    private async Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            options ??= new UnpackOptions();
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumOutputBytes);
            var selected = options.CreateSelectedEntrySet();
            await using var image = MetadataReader.OpenImage(filePath);
            var metadata = MetadataReader.ReadFromImageStream(image);
            var targets = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var plans = new List<(string Path, long Length, LpMetadataExtent[] Extents)>();
            foreach (var partition in metadata.Partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = partition.NameString;
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\']) >= 0)
                    throw new InvalidDataException("Invalid LP partition name.");
                if (selected is not null && !selected.Contains(name)) continue;
                var target = FileExtraction.ResolvePath(outputDirectory, name + ".img");
                if (!targets.Add(target)) throw new InvalidDataException("Duplicate LP partition output path.");
                if (string.Equals(target, Path.GetFullPath(filePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("An LP partition output cannot replace the source image.");
                if (!options.Overwrite && File.Exists(target)) throw new IOException($"Output already exists: '{target}'.");
                var extents = metadata.Extents.GetRange(checked((int)partition.FirstExtentIndex), checked((int)partition.NumExtents)).ToArray();
                long length = 0;
                foreach (var extent in extents)
                {
                    var bytes = checked((long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE);
                    length = checked(length + bytes);
                    if (length > options.MaximumOutputBytes) throw new InvalidDataException("LP partition exceeds the output budget.");
                    if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_ZERO) continue;
                    if (extent.TargetType != MetadataFormat.LP_TARGET_TYPE_LINEAR || extent.TargetSource != 0)
                        throw new InvalidDataException("LP extraction requires a supplied primary-device linear extent or a zero extent.");
                    var offset = checked((long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE);
                    FileExtraction.ValidateRange(offset, bytes, image.Length, "LP extent");
                }
                plans.Add((target, length, extents));
            }
            if (selected is not null && selected.Except(plans.Select(plan => Path.GetFileNameWithoutExtension(plan.Path)),
                    StringComparer.OrdinalIgnoreCase).Any())
                throw new InvalidDataException("Selected LP partition does not exist in the image.");

            var written = 0;
            var readGate = new object();
            var progressGate = new Lock();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = FirmwareParallelism.Workers,
                CancellationToken = cancellationToken
            };
            await Parallel.ForEachAsync(plans, parallelOptions, async (plan, token) =>
            {
                using var source = new IndependentReadStream(image, readGate);
                lock (progressGate)
                    options.Progress?.Report(100.0 * written / plans.Count, $"Extracting {Path.GetFileName(plan.Path)}");
                await FileExtraction.WriteAtomicAsync(plan.Path, async output =>
                {
                    output.SetLength(plan.Length);
                    foreach (var extent in plan.Extents)
                    {
                        token.ThrowIfCancellationRequested();
                        var length = checked((long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE);
                        if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_ZERO)
                        {
                            output.Position = checked(output.Position + length);
                            continue;
                        }
                        source.Position = checked((long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE);
                        await StreamCopy.CopyExactlyAsync(source, output, length, token, 256 * 1024).ConfigureAwait(false);
                    }
                    if (output.Position != plan.Length) throw new InvalidDataException("LP partition length mismatch.");
                }, options.Overwrite, token).ConfigureAwait(false);
                lock (progressGate) written++;
            }).ConfigureAwait(false);
            options.Progress?.Report(100, $"Extracted {written} partitions");
            return FirmwareOpResult.Ok($"Extracted {written} partitions to {outputDirectory}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
    }

    /// <summary>
    /// Plans an aligned device from the input images, then streams it to an atomic output file.
    /// <para>根据输入镜像规划对齐的设备布局，再流式构建并原子提交输出文件。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Directory whose top-level <c>*.img</c> files become one linearly laid-out partition each; the output file itself is excluded.
    /// <para>其顶层 <c>*.img</c> 文件将各自成为一个线性排布分区的目录；输出文件本身会被排除。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination super image, written through a temporary file and committed atomically.
    /// <para>目标 super 镜像，先写入临时文件再原子提交。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancel token checked during planning and around every partition copy.
    /// <para>在规划阶段及每次分区拷贝前后检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> reporting the packed partition count on success, or a failure result when the directory has no <c>*.img</c> files, a source changes while packing, or sizes overflow; only <see cref="OperationCanceledException"/> escapes.
    /// <para>成功时报告打包分区数量的 <see cref="FirmwareOpResult"/>；目录无 <c>*.img</c> 文件、打包期间源文件变化或尺寸溢出时返回失败结果；仅 <see cref="OperationCanceledException"/> 会向外抛出。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public async Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputFullPath = Path.GetFullPath(outputPath);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var files = Directory.EnumerateFiles(inputPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFullPath(path), outputFullPath, pathComparison))
                .Order(StringComparer.Ordinal)
                .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path), Length: new FileInfo(path).Length))
                .ToList();
            if (files.Count == 0) return FirmwareOpResult.Fail("Input directory contains no *.img files.");

            const uint logicalBlockSize = 4096;
            const uint slotCount = 2;
            var tableBytes = checked((ulong)MetadataFormat.Sizes.Header + MetadataFormat.Sizes.PartitionGroup
                + MetadataFormat.Sizes.BlockDevice + (ulong)files.Count * (MetadataFormat.Sizes.Partition + MetadataFormat.Sizes.Extent));
            var metadataMaxSize = checked((uint)Math.Max(64 * 1024UL, AlignUp(tableBytes, logicalBlockSize)));
            var dataStart = AlignUp(checked(MetadataFormat.LP_PARTITION_RESERVED_BYTES + 2UL * MetadataFormat.LP_METADATA_GEOMETRY_SIZE
                + 2UL * slotCount * metadataMaxSize), logicalBlockSize);
            ulong partitionBytes = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                partitionBytes = checked(partitionBytes + AlignUp(checked((ulong)file.Length), logicalBlockSize));
            }
            var deviceSize = checked(dataStart + Math.Max(partitionBytes, logicalBlockSize));
            var imageLength = checked((long)deviceSize);
            var builder = MetadataBuilder.New(deviceSize, metadataMaxSize, slotCount, logicalBlockSize);
            builder.AddGroup("default", 0);
            foreach (var file in files) builder.AddPartition(file.Name, "default", checked((ulong)file.Length));
            var metadata = builder.Build();
            var sources = files.ToDictionary(file => file.Name, StringComparer.Ordinal);
            await FileExtraction.WriteAtomicAsync(outputFullPath, async image =>
            {
                image.SetLength(imageLength);
                MetadataWriter.WriteToImageStream(image, metadata);
                await image.FlushAsync(cancellationToken).ConfigureAwait(false);
                var handle = image.SafeFileHandle;
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = FirmwareParallelism.Workers,
                    CancellationToken = cancellationToken
                };
                await Parallel.ForEachAsync(metadata.Partitions, parallelOptions, async (partition, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var source = sources[partition.NameString];
                    await using var part = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous);
                    if (part.Length != source.Length) throw new IOException($"Partition image changed during packing: '{source.Path}'.");
                    if (source.Length == 0) return;
                    if (partition.NumExtents != 1) throw new InvalidDataException("The LP builder returned an unexpected extent layout.");
                    var extent = metadata.Extents[checked((int)partition.FirstExtentIndex)];
                    var offset = checked((long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE);
                    var extentLength = checked((long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE);
                    FileExtraction.ValidateRange(offset, extentLength, imageLength, "Packed LP extent");
                    if (extent.TargetType != MetadataFormat.LP_TARGET_TYPE_LINEAR || extent.TargetSource != 0 || source.Length > extentLength)
                        throw new InvalidDataException("The LP builder returned an invalid linear extent.");
                    using var target = new PositionedWriteStream(handle, offset, source.Length);
                    await StreamCopy.CopyExactlyAsync(part, target, source.Length, token, 256 * 1024).ConfigureAwait(false);
                    if (part.Length != source.Length) throw new IOException($"Partition image changed during packing: '{source.Path}'.");
                }).ConfigureAwait(false);
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok($"Packed {files.Count} partitions into super image {outputPath}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
    }

    private static ulong AlignUp(ulong value, uint alignment)
        => checked(value + alignment - 1) / alignment * alignment;
}