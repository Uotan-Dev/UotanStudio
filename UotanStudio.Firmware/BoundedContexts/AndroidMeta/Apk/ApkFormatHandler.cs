using System.IO.Compression;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

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
            // apk-metadata.txt 与 .uotanstudio-repack.json 同规则统一管理：
            // 有项目上下文时写入 .uotan/metadata/<分区名>/，无项目时写入解包目录。
            // apk-metadata.txt follows the same unified rule as .uotanstudio-repack.json:
            // stored under .uotan/metadata/<partition>/ when a project is open, in the
            // extraction directory otherwise.
            var metadataDirectory = RepackMetadata.DefaultMetadataRoot is { Length: > 0 } root
                ? Path.Combine(root, RepackMetadata.DetectPartitionName(Path.GetFileName(filePath)))
                : outputDirectory;
            Directory.CreateDirectory(metadataDirectory);
            var metadataPath = Path.Combine(metadataDirectory, "apk-metadata.txt");
            var overwrite = options?.Overwrite ?? true;
            if (File.Exists(metadataPath) && !overwrite)
            {
                return Task.FromResult(FirmwareOpResult.Fail($"Output exists and overwrite is disabled: {metadataPath}"));
            }

            // APK 是标准 ZIP 容器：除元数据外完整解出全部条目（有界流式 + zip-slip 校验 + 原子提交）。
            // An APK is a standard ZIP container: extract every entry besides the metadata file
            // (bounded streaming + zip-slip validation + atomic commit).
            var maximumOutputBytes = options?.MaximumOutputBytes ?? new UnpackOptions().MaximumOutputBytes;
            long totalBytes = 0;
            var extractedFiles = new List<string>();

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                    {
                        // 目录占位条目仅创建目录，不产出文件。
                        // Directory placeholder entries only create the directory.
                        FileExtraction.ResolvePath(outputDirectory, entry.FullName);
                        continue;
                    }

                    var target = FileExtraction.ResolvePath(outputDirectory, entry.FullName);
                    var length = entry.Length;
                    if (length < 0 || length > maximumOutputBytes - totalBytes)
                    {
                        throw new InvalidDataException(
                            $"APK entry '{entry.FullName}' exceeds the remaining output budget ({maximumOutputBytes - totalBytes} bytes left, declared {length}).");
                    }

                    FileExtraction.WriteAtomicAsync(target, async output =>
                    {
                        await using var counting = new CountingWriteStream(output, length, leaveOpen: true, cancellationToken);
                        await using var entryStream = entry.Open();
                        await entryStream.CopyToAsync(counting, cancellationToken).ConfigureAwait(false);
                        if (counting.BytesWritten != length)
                        {
                            throw new InvalidDataException(
                                $"APK entry '{entry.FullName}' produced {counting.BytesWritten} bytes; expected {length}.");
                        }
                    }, overwrite, cancellationToken).GetAwaiter().GetResult();

                    totalBytes += length;
                    extractedFiles.Add(target);
                }
            }

            System.IO.File.WriteAllText(metadataPath, FormatInfo(info));
            extractedFiles.Insert(0, metadataPath);
            return Task.FromResult(FirmwareOpResult.Ok(
                $"APK unpacked to {outputDirectory} ({extractedFiles.Count} files)  APK 已完整解包到 {outputDirectory}（{extractedFiles.Count} 个文件）。",
                [.. extractedFiles]));
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
    /// Packs the input into firmware asynchronously. Directories are rebuilt into an APK via
    /// <see cref="Signing.ApkRepacker"/>; APK files are copied through.
    /// <para>异步将输入打包为固件。目录经 <see cref="Signing.ApkRepacker"/> 重建为 APK；APK 文件直接复制。</para>
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

            if (Directory.Exists(inputPath))
            {
                Signing.ApkRepacker.Repack(inputPath, outputPath, cancellationToken: cancellationToken);
                return Task.FromResult(FirmwareOpResult.Ok($"APK repacked to {outputPath}", [outputPath]));
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
            return Task.FromResult(FirmwareOpResult.Fail($"APK pack failed: {ex.Message}"));
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
