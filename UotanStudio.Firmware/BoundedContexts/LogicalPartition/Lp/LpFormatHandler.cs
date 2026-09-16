using System.Buffers.Binary;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;

public sealed class LpFormatHandler : IFormatHandler
{
    public string FormatName => "Android Logical Partition (super)";

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
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        try
        {
            // 无损预检：逻辑分区镜像必须在主、备份或空布局（偏移 0）几何槽位携带几何魔数。
            // 不满足时立即返回 false，避免为每个无关 .img 做全量 LP 解析并在主/备/空三个槽位
            // 各抛一次 InvalidDataException/EndOfStreamException（被捕获）——这正是工程打开时
            // 镜像扫描对 images/ 下每个非 super 镜像产生异常风暴的来源。
            // Cheap, lossless pre-probe: a logical-partition image must carry the geometry magic in
            // the primary, backup or empty-layout (offset 0) geometry slot. Otherwise it is rejected
            // immediately, so unrelated .img files are not fully LP-parsed and do not throw an
            // InvalidDataException/EndOfStreamException per slot (which is what produced the first-chance
            // exception storm when the project-open image scan probed every non-super images/ entry).
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
    {
        var header = FormatDetector.ReadHeader(filePath,
            MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE + sizeof(uint));
        var isSparse = header.Length >= sizeof(uint)
            && BinaryPrimitives.ReadUInt32LittleEndian(header) == SparseMagic;
        if (isSparse)
        {
            return true;
        }

        var primary = header.Length >= MetadataFormat.LP_PARTITION_RESERVED_BYTES + sizeof(uint)
            && MagicAt(header, MetadataFormat.LP_PARTITION_RESERVED_BYTES);
        var backup = header.Length >= MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE + sizeof(uint)
            && MagicAt(header, MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE);
        var empty = header.Length >= sizeof(uint) && MagicAt(header, 0);
        return primary || backup || empty;
    }

    private static bool MagicAt(byte[] header, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset, sizeof(uint))) == MetadataFormat.LP_METADATA_GEOMETRY_MAGIC;

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 存储形态只看文件头 4 字节：稀疏 super 的文件布局与逻辑字节不同，标注为 sparse；否则为 raw。
            // Storage layout reads only the first 4 header bytes: a sparse super's file layout differs
            // from its logical bytes, so it is labeled sparse; everything else is raw.
            var header = UotanStudio.Firmware.SharedKernel.FormatDetector.ReadHeader(filePath, sizeof(uint));
            var isSparse = header.Length >= sizeof(uint)
                && BinaryPrimitives.ReadUInt32LittleEndian(header) == SparseMagic;
            var metadata = MetadataReader.ReadFromImageFile(filePath);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new Dictionary<string, string>
            {
                // Super 布局：整体是 raw 还是 sparse 容器，以及几何/块设备关键参数。
                // Super layout: whether the container is raw or sparse, plus key geometry/device parameters.
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
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private async Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            options ??= new UnpackOptions();
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumOutputBytes);
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

            var written = 0;
            var readGate = new object();
            var progressGate = new object();
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