using System.IO.Compression;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.AndroidMeta.Apk;

/// <summary>
/// Format handler that detects, describes, unpacks and rebuilds Android packages stored as ZIP archives.
/// <para>格式处理器：检测、描述并解包/重建以 ZIP 归档形式存储的 Android 应用包。</para>
/// </summary>
internal sealed class ApkFormatHandler : IFormatHandler
{
    /// <summary>Display name of the handled format. <para>所处理格式的显示名称。</para></summary>
    /// <value>The constant label "Android Package (APK)" this handler reports as its format identity. <para>该处理器作为格式标识上报的固定标签 "Android Package (APK)"。</para></value>
    public string FormatName => "Android Package (APK)";

    /// <summary>File extension this format is associated with; detection itself relies on the ZIP magic, not the name. <para>该格式关联的文件扩展名；实际检测依赖 ZIP 魔数而非文件名。</para></summary>
    /// <value>A read-only list containing the single extension <c>apk</c>. <para>仅包含扩展名 <c>apk</c> 的只读列表。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["apk"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path of the file to sniff; blank or missing paths are rejected without opening.
    /// <para>要探测的文件路径；空白或不存在的路径直接判定不匹配且不会打开文件。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the first four bytes are the ZIP local-file-header signature <c>PK 03 04</c>; read errors yield <see langword="false"/>.
    /// <para>文件前四字节为 ZIP 本地文件头签名 <c>PK 03 04</c> 时返回 <see langword="true"/>；读取错误返回 <see langword="false"/>。</para>
    /// </returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, 4, 0, "PK\x03\x04"u8);
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">
    /// APK file to analyze through its manifest and archive structure.
    /// <para>要经清单与归档结构进行分析的 APK 文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before analysis starts; the analysis itself is synchronous.
    /// <para>在分析开始前取消；分析本身为同步执行。</para>
    /// </param>
    /// <returns>
    /// A task whose result is <c>Ok</c> with the analyzer description, or <c>Fail</c> when the archive cannot be read.
    /// <para>任务结果为 <c>Ok</c> 并携带分析器描述，归档无法读取时为 <c>Fail</c>。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para>
    /// </exception>
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
    /// <param name="filePath">
    /// APK archive to extract; each declared entry size is checked against the output budget.
    /// <para>要解包的 APK 归档；每个条目的声明长度都对照输出预算校验。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory, created on demand; no entry may resolve outside it.
    /// <para>目标目录，按需创建；任何条目不得解析到目录之外。</para>
    /// </param>
    /// <param name="options">
    /// Extraction options; <c>Overwrite</c> governs replacing outputs and <c>MaximumOutputBytes</c> bounds entry sizes.
    /// <para>解包选项；<c>Overwrite</c> 决定是否替换已有输出，<c>MaximumOutputBytes</c> 限制条目大小总和。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before opening the archive and between entries; cancellation is rethrown.
    /// <para>在打开归档前与条目之间检查；取消异常会重新抛出。</para>
    /// </param>
    /// <returns>
    /// A task naming the generated metadata file and every extracted entry, or reporting an unpack failure.
    /// <para>任务指明生成的元数据文件与每个解出的条目，或报告解包失败。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled; extraction errors are reported as failed results instead.
    /// <para><paramref name="cancellationToken"/> 已被触发；提取错误则以失败结果报告。</para>
    /// </exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private async Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return FirmwareOpResult.Fail("Output directory is empty");
            }

            var info = ApkParser.AnalyzeForUnpack(filePath);
            Directory.CreateDirectory(outputDirectory);
            var metadataDirectory = RepackMetadata.DefaultMetadataRoot is { Length: > 0 } root
                ? Path.Combine(root, RepackMetadata.DetectPartitionName(Path.GetFileName(filePath)))
                : outputDirectory;
            Directory.CreateDirectory(metadataDirectory);
            var metadataPath = Path.Combine(metadataDirectory, "apk-metadata.txt");
            var overwrite = options?.Overwrite ?? true;
            if (File.Exists(metadataPath) && !overwrite)
            {
                return FirmwareOpResult.Fail($"Output exists and overwrite is disabled: {metadataPath}");
            }

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

                    await FileExtraction.WriteAtomicAsync(target, async output =>
                    {
                        await using var counting = new CountingWriteStream(output, length, leaveOpen: true, cancellationToken);
                        await using var entryStream = entry.Open();
                        await entryStream.CopyToAsync(counting, cancellationToken).ConfigureAwait(false);
                        if (counting.BytesWritten != length)
                        {
                            throw new InvalidDataException(
                                $"APK entry '{entry.FullName}' produced {counting.BytesWritten} bytes; expected {length}.");
                        }
                    }, overwrite, cancellationToken).ConfigureAwait(false);

                    totalBytes += length;
                    extractedFiles.Add(target);
                }
            }

            File.WriteAllText(metadataPath, FormatInfo(info));
            extractedFiles.Insert(0, metadataPath);
            return FirmwareOpResult.Ok(
                $"APK unpacked to {outputDirectory} ({extractedFiles.Count} files)",
                [.. extractedFiles]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail($"APK unpack failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously. Directories are rebuilt into an APK via
    /// <see cref="ApkRepacker"/>; APK files are copied through.
    /// <para>异步将输入打包为固件。目录经 <see cref="ApkRepacker"/> 重建为 APK；APK 文件直接复制。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Existing unpacked directory or an already-built APK file; a missing path yields a failed result.
    /// <para>已解包的目录或已构建好的 APK 文件；路径不存在时得到失败结果。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination file; its parent directory is created on demand and an existing output is replaced.
    /// <para>目标文件；按需创建其父目录，已有输出会被替换。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before packing and is forwarded to the directory rebuild; file copying is synchronous and cannot observe it.
    /// <para>在打包开始前取消，并在目录重建时继续传递；文件复制为同步执行，无法感知取消。</para>
    /// </param>
    /// <returns>
    /// A task naming the produced file, or reporting a failure prefixed <c>APK pack failed:</c>.
    /// <para>任务指明生成的文件，或以 <c>APK pack failed:</c> 前缀报告失败。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para>
    /// </exception>
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
                ApkRepacker.Repack(inputPath, outputPath, cancellationToken: cancellationToken);
                return Task.FromResult(FirmwareOpResult.Ok($"APK repacked to {outputPath}", [outputPath]));
            }

            File.Copy(inputPath, outputPath, overwrite: true);
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