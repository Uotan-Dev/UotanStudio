using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.UpdateApp;

/// <summary>
/// <para>华为 update.app 固件格式的 IFormatHandler 实现。</para>
/// IFormatHandler implementation for the Huawei update.app firmware format.
/// </summary>
public sealed class UpdateAppFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Huawei update.app";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["app", "bin"];

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var name = Path.GetFileName(filePath).ToLowerInvariant();
        if (name.StartsWith("update", StringComparison.Ordinal) && name.EndsWith(".app", StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4)
            {
                return false;
            }
            // Magic 0x55AA5AA5 (LE bytes 55 AA 5A A5).
            return magic.SequenceEqual(UpdateAppProcessor.MagicBytes);
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
            var entries = UpdateAppProcessor.GetEntries(filePath).ToList();
            var summary = $"Huawei update.app: {entries.Count} partitions";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. entries.Select(e => e.Name)],
                metadata: new Dictionary<string, string> { ["EntryCount"] = entries.Count.ToString() }));
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
            var count = UpdateAppProcessor.Extract(filePath, outputDirectory);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} partitions to {outputDirectory}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) => Task.FromResult(FirmwareOpResult.Fail("update.app packing is not supported by this handler."));
}