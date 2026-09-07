using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz;

public sealed class DzFormatHandler : IFormatHandler
{
    public string FormatName => "LG DZ/KDZ Firmware";

    public IReadOnlyList<string> FileExtensions => ["dz", "kdz", "bin"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext is "dz" or "kdz")
        {
            return true;
        }
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[8];
            var read = fs.Read(magic);
            return (read >= 4 && magic[..4].SequenceEqual(DzConstants.DzFileHeader))
                   || (read >= 8 && (magic.SequenceEqual(DzConstants.KdzHeaderV0)
                       || magic.SequenceEqual(DzConstants.KdzHeaderV1)
                       || magic.SequenceEqual(DzConstants.KdzHeaderV2)));
        }
        catch
        {
            return false;
        }
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
            if (IsKdz(filePath))
            {
                var (headerType, partitions) = KdzExtractor.Parse(filePath);
                var kdzSummary = $"LG KDZ (header v{headerType}): {partitions.Count} partitions";
                return Task.FromResult(FirmwareOpResult.Ok(kdzSummary,
                    entries: [.. partitions.Select(p => p.Name)],
                    metadata: new Dictionary<string, string>
                    {
                        ["HeaderType"] = headerType.ToString(),
                        ["PartitionCount"] = partitions.Count.ToString()
                    }));
            }

            var (header, chunks) = DzExtractor.Parse(filePath);
            var summary = $"LG DZ: {chunks.Count} chunks (device: {header.GetDevice()})";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. chunks.Select(c => c.Name)],
                metadata: new Dictionary<string, string>
                {
                    ["Device"] = header.GetDevice(),
                    ["ChunkCount"] = chunks.Count.ToString()
                }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kdz = IsKdz(filePath);
            var count = kdz
                ? KdzExtractor.Extract(filePath, outputDirectory, overwrite: options?.Overwrite ?? true, cancellationToken: cancellationToken)
                : DzExtractor.Extract(filePath, outputDirectory, overwrite: options?.Overwrite ?? true, cancellationToken: cancellationToken);
            var kind = kdz ? "embedded files" : "decompressed chunks";
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} {kind} to {outputDirectory}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) => Task.FromResult(FirmwareOpResult.Fail("DZ/KDZ packing is not supported by this handler."));

    private static bool IsKdz(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        return magic.SequenceEqual(DzConstants.KdzHeaderV0) || magic.SequenceEqual(DzConstants.KdzHeaderV1)
            || magic.SequenceEqual(DzConstants.KdzHeaderV2);
    }
}