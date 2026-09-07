using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk;

public sealed class ApkFormatHandler : IFormatHandler
{
    public string FormatName => "Android Package (APK)";

    public IReadOnlyList<string> FileExtensions { get; } = ["apk"];

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


            var header = FormatDetector.ReadHeader(filePath, 4);
            return header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K'
                   && header[2] == 0x03 && header[3] == 0x04;
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
            var info = ApkParser.Analyze(filePath);
            return Task.FromResult(FirmwareOpResult.Ok(info.Describe()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"APK analysis failed: {ex.Message}"));
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
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Output directory is empty"));
            }

            var info = ApkParser.Analyze(filePath);
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "apk-metadata.txt");
            var overwrite = options?.Overwrite ?? true;
            if (File.Exists(outputPath) && !overwrite)
            {
                return Task.FromResult(FirmwareOpResult.Fail($"Output exists and overwrite is disabled: {outputPath}"));
            }

            System.IO.File.WriteAllText(outputPath, FormatInfo(info));
            return Task.FromResult(FirmwareOpResult.Ok($"APK metadata written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"APK unpack failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {


        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Output path is empty"));
            }
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            System.IO.File.Copy(inputPath, outputPath, overwrite: true);
            return Task.FromResult(FirmwareOpResult.Ok($"Copied APK to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"APK copy failed: {ex.Message}"));
        }
    }

    private static string FormatInfo(ApkInfo info)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(info.Describe());
        sb.Append("Package: ").AppendLine(info.PackageName);
        sb.Append("Version: ").Append(info.VersionName).Append(" (").Append(info.VersionCode).AppendLine(")");
        sb.Append("Min SDK: ").Append(info.MinSdkVersion).Append("  Target SDK: ").Append(info.TargetSdkVersion).AppendLine();
        sb.Append("ABIs: ").AppendLine(string.Join(", ", info.Abis));
        sb.AppendLine("Permissions:");
        foreach (var permission in info.Permissions)
        {
            sb.Append("  ").AppendLine(permission);
        }
        return sb.ToString().TrimEnd();
    }
}
