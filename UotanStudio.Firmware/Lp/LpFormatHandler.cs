using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Lp;

/// <summary>
/// IFormatHandler implementation for Android super images with logical partition (LP) metadata.
/// <para>Android super 镜像(含逻辑分区 LP 元数据)的 IFormatHandler 实现。</para>
/// </summary>
public sealed class LpFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Android Logical Partition (super)";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["img", "super"];

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext == "super" || Path.GetFileName(filePath).StartsWith("super", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        try
        {
            using var stream = File.OpenRead(filePath);
            return TryPeekMagic(stream);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPeekMagic(Stream stream)
    {
        if (stream.Length < MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.Sizes.Geometry)
        {
            return false;
        }
        var buffer = new byte[MetadataFormat.Sizes.Geometry];
        stream.Position = MetadataFormat.LP_PARTITION_RESERVED_BYTES;
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read != buffer.Length)
        {
            return false;
        }
        var geometry = LpMetadataGeometry.FromBytes(buffer);
        return geometry.Magic == MetadataFormat.LP_METADATA_GEOMETRY_MAGIC;
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = MetadataReader.ReadFromImageFile(filePath);
            var summary = $"LP metadata: {metadata.Partitions.Count} partitions, {metadata.Extents.Count} extents, " +
                          $"{metadata.Groups.Count} groups, {metadata.BlockDevices.Count} block devices";
            return Task.FromResult(FirmwareOpResult.Ok(summary, entries: [.. metadata.PartitionNames]));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = MetadataReader.ReadFromImageFile(filePath);
            Directory.CreateDirectory(outputDirectory);

            // For each partition with linear extents, slice the corresponding region out of the image.
            using var image = File.OpenRead(filePath);
            var written = 0;
            foreach (var partition in metadata.Partitions)
            {
                var extents = metadata.Extents
                    .Skip((int)partition.FirstExtentIndex)
                    .Take((int)partition.NumExtents)
                    .ToList();
                if (extents.Count == 0)
                {
                    continue;
                }

                var outPath = Path.Combine(outputDirectory, partition.NameString + ".img");
                using var output = File.Create(outPath);
                foreach (var extent in extents)
                {
                    if (extent.TargetType != MetadataFormat.LP_TARGET_TYPE_LINEAR)
                    {
                        // Zero extents: write zeros.
                        var zeroLen = (long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE;
                        output.SetLength(output.Length + zeroLen);
                        output.Position = output.Length;
                        continue;
                    }
                    var start = (long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE;
                    var len = (long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE;
                    image.Position = start;
                    var buffer = new byte[1024 * 1024];
                    var remaining = len;
                    while (remaining > 0)
                    {
                        var n = (int)Math.Min(buffer.Length, remaining);
                        var r = image.Read(buffer, 0, n);
                        if (r <= 0)
                        {
                            break;
                        }
                        output.Write(buffer, 0, r);
                        remaining -= r;
                    }
                }
                written++;
            }

            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {written} partitions to {outputDirectory}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            // inputPath is a directory of partition images; build a super image with LP metadata.
            var files = Directory.EnumerateFiles(inputPath, "*.img", SearchOption.TopDirectoryOnly).ToList();
            if (files.Count == 0)
            {
                return Task.FromResult(FirmwareOpResult.Fail("Input directory contains no *.img files."));
            }

            var deviceSize = 4096UL * 1024 * 1024; // 4 GiB default super device
            var builder = MetadataBuilder.New(deviceSize, 64 * 1024, 2);
            builder.AddGroup("default", 0);

            ulong total = 0;
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var size = (ulong)new FileInfo(file).Length;
                builder.AddPartition(name, "default", size);
                total += size;
            }

            var metadata = builder.Build();
            using (var image = File.Create(outputPath))
            {
                image.SetLength((long)deviceSize);
                MetadataWriter.WriteToImageStream(image, metadata);

                // Write partition contents at their linear extents.
                using var src = File.OpenRead(outputPath);
                foreach (var partition in metadata.Partitions)
                {
                    var extents = metadata.Extents
                        .Skip((int)partition.FirstExtentIndex)
                        .Take((int)partition.NumExtents)
                        .ToList();
                    if (extents.Count == 0)
                    {
                        continue;
                    }
                    var srcPath = Path.Combine(inputPath, partition.NameString + ".img");
                    if (!File.Exists(srcPath))
                    {
                        continue;
                    }
                    using var part = File.OpenRead(srcPath);
                    var extent = extents[0];
                    image.Position = (long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE;
                    part.CopyTo(image);
                }
            }

            return Task.FromResult(FirmwareOpResult.Ok($"Packed {files.Count} partitions into super image {outputPath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }
}