using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.UpdateApp;

public sealed class UpdateAppFormatHandler : IFormatHandler
{
    public string FormatName => "Huawei update.app";

    public IReadOnlyList<string> FileExtensions => ["app", "bin"];

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
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4)
            {
                return false;
            }

            return magic.SequenceEqual(UpdateAppProcessor.MagicBytes);
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
            var entries = UpdateAppProcessor.GetEntries(filePath).ToList();
            var summary = $"Huawei update.app: {entries.Count} partitions";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. entries.Select(e => e.Name)],
                metadata: new Dictionary<string, string> { ["EntryCount"] = entries.Count.ToString() }));
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
        => UotanStudio.Firmware.SharedKernel.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = UpdateAppProcessor.Extract(filePath, outputDirectory,
                overwrite: options?.Overwrite ?? true, cancellationToken: cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} partitions to {outputDirectory}"));
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
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) => Task.FromResult(FirmwareOpResult.Fail("update.app packing is not supported by this handler."));
}
