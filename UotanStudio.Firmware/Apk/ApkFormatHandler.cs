using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Apk;

/// <summary>
/// <para>Android Package (APK) 格式处理器：识别 .apk 文件、读取元数据（包名/版本/权限/ABI/图标）。</para>
/// Android Package (APK) format handler: detects .apk files and reads metadata (package name,
/// version, permissions, ABIs, icon).
/// </summary>
public sealed class ApkFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Android Package (APK)";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions { get; } = ["apk"];

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            // zip 魔数 PK\x03\x04。
            // The zip magic PK\x03\x04.
            var header = FormatDetector.ReadHeader(filePath, 4);
            return header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K'
                   && header[2] == 0x03 && header[3] == 0x04;
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

    /// <inheritdoc />
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
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

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        // APK 重打包（资源重排/签名）不在本处理器范围内；原样拷贝输入文件。
        // APK repackaging (resource reordering / signing) is out of scope; copy the input verbatim.
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