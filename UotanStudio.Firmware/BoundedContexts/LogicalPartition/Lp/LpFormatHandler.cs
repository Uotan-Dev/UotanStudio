using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;

public sealed class LpFormatHandler : IFormatHandler
{
    public string FormatName => "Android Logical Partition (super)";

    public IReadOnlyList<string> FileExtensions => ["img", "super", "simg", "sparse", "raw"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        try
        {
            using var stream = MetadataReader.OpenImage(filePath);
            _ = MetadataReader.ReadFromImageStream(stream);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = MetadataReader.ReadFromImageFile(filePath);
            cancellationToken.ThrowIfCancellationRequested();
            var summary = $"LP metadata: {metadata.Partitions.Count} partitions, {metadata.Extents.Count} extents, " +
                          $"{metadata.Groups.Count} groups, {metadata.BlockDevices.Count} block devices";
            return Task.FromResult(FirmwareOpResult.Ok(summary, entries: [.. metadata.PartitionNames]));
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
            foreach (var plan in plans)
            {
                options.Progress?.Report(100.0 * written / plans.Count, $"Extracting {Path.GetFileName(plan.Path)}");
                await FileExtraction.WriteAtomicAsync(plan.Path, async output =>
                {
                    output.SetLength(plan.Length);
                    foreach (var extent in plan.Extents)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var length = checked((long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE);
                        if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_ZERO)
                        {
                            output.Position = checked(output.Position + length);
                            continue;
                        }
                        image.Position = checked((long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE);
                        await StreamCopy.CopyExactlyAsync(image, output, length, cancellationToken).ConfigureAwait(false);
                    }
                    if (output.Position != plan.Length) throw new InvalidDataException("LP partition length mismatch.");
                }, options.Overwrite, cancellationToken).ConfigureAwait(false);
                written++;
            }
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
                foreach (var partition in metadata.Partitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources[partition.NameString];
                    await using var part = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous);
                    if (part.Length != source.Length) throw new IOException($"Partition image changed during packing: '{source.Path}'.");
                    if (source.Length == 0) continue;
                    if (partition.NumExtents != 1) throw new InvalidDataException("The LP builder returned an unexpected extent layout.");
                    var extent = metadata.Extents[checked((int)partition.FirstExtentIndex)];
                    var offset = checked((long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE);
                    var extentLength = checked((long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE);
                    FileExtraction.ValidateRange(offset, extentLength, imageLength, "Packed LP extent");
                    if (extent.TargetType != MetadataFormat.LP_TARGET_TYPE_LINEAR || extent.TargetSource != 0 || source.Length > extentLength)
                        throw new InvalidDataException("The LP builder returned an invalid linear extent.");
                    image.Position = offset;
                    await StreamCopy.CopyExactlyAsync(part, image, source.Length, cancellationToken).ConfigureAwait(false);
                    if (part.Length != source.Length) throw new IOException($"Partition image changed during packing: '{source.Path}'.");
                }
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok($"Packed {files.Count} partitions into super image {outputPath}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
    }

    private static ulong AlignUp(ulong value, uint alignment)
        => checked(value + alignment - 1) / alignment * alignment;
}
