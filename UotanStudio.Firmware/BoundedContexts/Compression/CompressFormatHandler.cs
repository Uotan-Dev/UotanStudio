using UotanStudio.Firmware.BoundedContexts.Compression.Files;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.Compression;

public sealed class CompressFormatHandler : IFormatHandler
{
    public string FormatName => "Compressed File";

    public IReadOnlyList<string> FileExtensions { get; } =
        ["gz", "gzip", "zlib", "deflate", "br", "lz4", "lzma", "xz", "bz2", "bzip2", "zst", "zstd"];

    /// <summary>
    /// Determines whether this handler can process the given file, based on magic-byte detection.
    /// <para>判断该处理器能否处理指定文件；依据魔数检测进行判断。</para>
    /// </summary>
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
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
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