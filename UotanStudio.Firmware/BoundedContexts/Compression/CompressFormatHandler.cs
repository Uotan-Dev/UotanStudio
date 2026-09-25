using UotanStudio.Firmware.BoundedContexts.Compression.Files;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.Compression;

/// <summary>
/// Format handler that detects, inspects, unpacks and packs stand-alone compressed files.
/// <para>负责检测、读取元数据、解包与打包独立压缩文件的格式处理器。</para>
/// </summary>
public sealed class CompressFormatHandler : IFormatHandler
{
    /// <summary>
    /// Display name of the handled format group.
    /// <para>所处理格式组的显示名称。</para>
    /// </summary>
    /// <value>Always <c>"Compressed File"</c>. <para>固定为 <c>"Compressed File"</c>。</para></value>
    public string FormatName => "Compressed File";

    /// <summary>
    /// Compressed file extensions recognized by this handler.
    /// <para>本处理器识别的压缩文件扩展名。</para>
    /// </summary>
    /// <value>
    /// Extension names without leading dots, covering gzip, zlib, deflate, Brotli, LZ4, LZMA, xz,
    /// bzip2 and Zstd.
    /// <para>不含点号前缀的扩展名，涵盖 gzip、zlib、deflate、Brotli、LZ4、LZMA、xz、bzip2 与 Zstd。</para>
    /// </value>
    public IReadOnlyList<string> FileExtensions { get; } =
        ["gz", "gzip", "zlib", "deflate", "br", "lz4", "lzma", "xz", "bz2", "bzip2", "zst", "zstd"];

    /// <summary>
    /// Determines whether this handler can process the given file, based on magic-byte detection.
    /// <para>判断该处理器能否处理指定文件；依据魔数检测进行判断。</para>
    /// </summary>
    /// <param name="filePath">Path of the file to probe. <para>待探测的文件路径。</para></param>
    /// <returns><see langword="true"/> when magic-byte detection identifies a compression format;
    /// <see langword="false"/> for a null/blank path, a missing file, or any detection error, which
    /// is swallowed.
    /// <para>魔数检测识别出压缩格式时返回 <see langword="true"/>；路径为空、文件不存在或检测过程出错
    /// （错误会被吞掉）时返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            return CompressionFile.DetectFile(filePath) != CompressionFormat.None;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads format metadata without unpacking.
    /// <para>读取格式元数据（不解包）。</para>
    /// </summary>
    /// <param name="filePath">Path of the compressed file. <para>压缩文件的路径。</para></param>
    /// <param name="cancellationToken">Cancels the pending read task; a cancelled task surfaces as
    /// <see cref="OperationCanceledException"/>. <para>取消挂起的读取任务；被取消的任务会抛出 <see cref="OperationCanceledException"/>。</para></param>
    /// <returns>
    /// On success, a result whose metadata holds <c>CompressionFormat</c>, <c>CompressedSize</c> in
    /// bytes, <c>CanCompress</c> and <c>CanDecompress</c>, with the file name as its entry list; the
    /// format comes from magic-byte detection, falling back to the file extension. A failure result
    /// is returned when the format is unrecognized or reading throws; cancellation propagates.
    /// <para>成功时，结果的元数据包含 <c>CompressionFormat</c>、以字节计的 <c>CompressedSize</c>、
    /// <c>CanCompress</c> 与 <c>CanDecompress</c>，条目列表为文件名；格式先由魔数检测，失败时回退到文件扩展名。
    /// 格式无法识别或读取抛出异常时返回失败结果；取消会向外传播。</para>
    /// </returns>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var format = CompressionFile.DetectFile(filePath);
                if (format == CompressionFormat.None)
                {


                    format = CompressionFormats.FromExtension(filePath);
                }
                if (format == CompressionFormat.None)
                {
                    return FirmwareOpResult.Fail($"Unrecognized compression format: '{filePath}'.");
                }

                var info = CompressionFormats.GetInfo(format);
                var fileInfo = new FileInfo(filePath);
                var metadata = new Dictionary<string, string>
                {
                    ["CompressionFormat"] = info?.Name ?? format.ToString(),
                    ["CompressedSize"] = fileInfo.Length.ToString(),
                    ["CanCompress"] = (info?.CanCompress ?? false).ToString(),
                    ["CanDecompress"] = (info?.CanDecompress ?? false).ToString(),
                };

                return FirmwareOpResult.Ok(
                    $"Compressed file '{filePath}' detected as {info?.Name ?? format.ToString()}.",
                    [fileInfo.Name],
                    metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Unpacks the compressed file into the output directory (format is auto-detected).
    /// <para>将压缩文件解包到输出目录（格式自动检测）。</para>
    /// </summary>
    /// <param name="filePath">Compressed file to decompress. <para>要解压的压缩文件。</para></param>
    /// <param name="outputDirectory">Directory receiving the decompressed file and the repack metadata record.
    /// <para>接收解压后文件与打包元数据记录的目录。</para></param>
    /// <param name="options">Overwrite policy and decompression size bound; <see langword="null"/> uses the
    /// defaults of <see cref="UnpackOptions"/>. <para>覆盖策略与解压大小上限；为 <see langword="null"/> 时采用 <see cref="UnpackOptions"/> 的默认值。</para></param>
    /// <param name="cancellationToken">Checked before extraction and during decompression; cancellation is
    /// rethrown and never converted to a failure result. <para>解包前与解压过程中均会检查；取消会重新抛出且绝不转换为失败结果。</para></param>
    /// <returns>
    /// On success, the decompressed content is written atomically to a file named after the input's base
    /// name (falling back to <c>"decompressed"</c>), capped at <c>MaximumOutputBytes</c>, and repack
    /// metadata is published into the output directory; the result carries the output file name and the
    /// detected format. A failure result is returned for an unrecognized format, an existing output with
    /// overwriting disabled, a pre-existing unrecognizable metadata file, or any decompression error.
    /// <para>成功时，解压内容以输入文件基本名（为空时回退为 <c>"decompressed"</c>）原子写出，大小受
    /// <c>MaximumOutputBytes</c> 限制，并向输出目录发布打包元数据；结果携带输出文件名与检测到的格式。
    /// 格式无法识别、输出已存在且禁止覆盖、已有元数据文件不可识别或解压出错时返回失败结果。</para>
    /// </returns>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var format = CompressionFile.DetectFile(filePath);
                if (format == CompressionFormat.None)
                {


                    format = CompressionFormats.FromExtension(filePath);
                }
                if (format == CompressionFormat.None)
                {
                    return FirmwareOpResult.Fail($"Unrecognized compression format: '{filePath}'.");
                }



                var outputName = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrEmpty(outputName))
                {
                    outputName = "decompressed";
                }
                var outputPath = FileExtraction.ResolvePath(outputDirectory, outputName);

                if (File.Exists(outputPath) && options is { Overwrite: false })
                {
                    return FirmwareOpResult.Fail($"Output file already exists: '{outputPath}'.");
                }

                var maximumOutputBytes = (options ?? new UnpackOptions()).MaximumOutputBytes;
                ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);
                FileExtraction.WriteAtomic(outputPath, output =>
                {
                    using var input = File.OpenRead(filePath);
                    CompressionService.Decompress(input, output, format, maximumOutputBytes, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }, options?.Overwrite ?? true);

                var info = CompressionFormats.GetInfo(format);
                return FirmwareOpResult.Ok(
                    $"Decompressed '{filePath}' ({info?.Name ?? format.ToString()}) to '{outputPath}'.",
                    [outputName],
                    new Dictionary<string, string> { ["CompressionFormat"] = info?.Name ?? format.ToString() });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Packs a file into the output file (format is inferred from the output extension).
    /// <para>将文件打包为输出文件（格式由输出扩展名推断）。</para>
    /// </summary>
    /// <param name="inputPath">File to compress. <para>要压缩的输入文件。</para></param>
    /// <param name="outputPath">Destination path whose extension selects the format.
    /// <para>目标路径，其扩展名决定压缩格式。</para></param>
    /// <param name="cancellationToken">Cancels the pending pack task; cancellation is rethrown rather than
    /// reported as a failure result. <para>取消挂起的打包任务；取消会重新抛出，而不是报告为失败结果。</para></param>
    /// <returns>
    /// On success, the result reports the compressed input file name. A failure result is returned when
    /// the input is missing, the output extension maps to no format, the format does not support
    /// compression, or compressing throws.
    /// <para>成功时，结果报告被压缩的输入文件名。输入缺失、输出扩展名无法映射到任何格式、该格式不支持压缩
    /// 或压缩过程抛出异常时返回失败结果。</para>
    /// </returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input file not found: '{inputPath}'.");
                }

                var format = CompressionFormats.FromExtension(outputPath);
                if (format == CompressionFormat.None)
                {
                    return FirmwareOpResult.Fail($"Cannot infer a compression format from the output extension: '{outputPath}'.");
                }

                var info = CompressionFormats.GetInfo(format);
                if (info is not { CanCompress: true })
                {
                    return FirmwareOpResult.Fail($"Compression is not supported for format '{info?.Name ?? format.ToString()}'.");
                }

                CompressionFile.CompressFile(inputPath, outputPath, format);

                return FirmwareOpResult.Ok(
                    $"Compressed '{inputPath}' into '{outputPath}' ({info.Name}).",
                    [Path.GetFileName(inputPath)]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to pack '{inputPath}': {ex.Message}");
            }
        }, cancellationToken);
    }
}