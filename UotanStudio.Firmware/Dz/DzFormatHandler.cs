using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Dz;

/// <summary>
/// <para>LG DZ/KDZ 固件格式的 IFormatHandler 实现。</para>
/// IFormatHandler implementation for the LG DZ/KDZ firmware formats.
/// </summary>
public sealed class DzFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "LG DZ/KDZ Firmware";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["dz", "kdz", "bin"];

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            if (ext == "kdz")
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
            Directory.CreateDirectory(outputDirectory);
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            var count = ext == "kdz"
                ? KdzExtractor.Extract(filePath, outputDirectory)
                : DzExtractor.Extract(filePath, outputDirectory);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} partitions to {outputDirectory}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) => Task.FromResult(FirmwareOpResult.Fail("DZ/KDZ packing is not supported by this handler."));
}