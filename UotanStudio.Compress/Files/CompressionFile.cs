using System.Diagnostics;
using UotanStudio.Compress.Detection;
using UotanStudio.Compress.Internal;

namespace UotanStudio.Compress.Files;

/// <summary>Compresses and decompresses files with progress, cancellation and atomic publication.
/// <para>压缩与解压文件，支持进度、取消以及原子发布输出。</para></summary>
public static class CompressionFile
{

    /// <summary>
    /// Compresses a file, reporting the bytes read against the source length.
    /// <para>压缩文件，并按源文件长度上报已读字节进度。</para>
    /// </summary>
    /// <param name="inputPath">The input file path. <para>源文件路径。</para></param>
    /// <param name="outputPath">The output file path. <para>目标文件路径。</para></param>
    /// <param name="format">The compression format. <para>压缩格式。</para></param>
    /// <param name="options">Compression options. <para>压缩选项。</para></param>
    /// <param name="progress">Progress <c>(bytes read, total)</c>; may be null. <para>压缩进度 <c>(已读字节, 源文件总字节)</c>，可为空。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    public static void CompressFile(string inputPath, string outputPath, CompressionFormat format, CompressionOptions? options = null, IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            throw new ArgumentNullException(nameof(inputPath));
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentNullException(nameof(outputPath));
        }


        WriteFileAtomically(outputPath, tempPath =>
        {
            using var input = File.OpenRead(inputPath);
            using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536);
            if (progress is null)
            {
                CompressionService.Compress(input, output, format, options);
                return;
            }

            var totalBytes = input.CanSeek ? input.Length : 0;
            progress.Report((0, totalBytes));
            long processed = 0;
            var lastReport = Stopwatch.GetTimestamp();
            using var counting = new ByteCountingReadStream(input, leaveOpen: true, cancellationToken: cancellationToken,
                bytesRead: bytes =>
                {
                    processed += bytes;
                    if (Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds < 100) return;
                    lastReport = Stopwatch.GetTimestamp();
                    progress.Report((processed, totalBytes));
                });
            CompressionService.Compress(counting, output, format, options);
            progress.Report((processed, totalBytes));
        });
    }

    /// <summary>
    /// Decompresses a file with the specified format.
    /// <para>按指定格式解压文件。</para>
    /// </summary>
    /// <param name="inputPath">Compressed source file. <para>压缩源文件。</para></param>
    /// <param name="outputPath">Destination file, replaced atomically on success.
    /// <para>目标文件，成功后原子替换。</para></param>
    /// <param name="format">Compression format to decode. <para>要解码的压缩格式。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> or <paramref name="outputPath"/>
    /// is null or empty. <para><paramref name="inputPath"/> 或 <paramref name="outputPath"/> 为 null 或空。</para></exception>
    public static void DecompressFile(string inputPath, string outputPath, CompressionFormat format)
        => DecompressFile(inputPath, outputPath, format, long.MaxValue, CancellationToken.None);

    /// <summary>Decompresses a file with a limit, atomically replacing the destination on success and cleaning the temporary file on failure.
    /// <para>按指定格式有界解压文件；成功后原子替换目标，失败时清理临时文件。</para></summary>
    /// <param name="inputPath">Compressed source file.<para>压缩源文件。</para></param>
    /// <param name="outputPath">Destination file.<para>目标文件。</para></param>
    /// <param name="format">Compression format to decode.<para>要解码的压缩格式。</para></param>
    /// <param name="maximumOutputBytes">Hard cap on decoded output size.
    /// <para>解压输出的硬性大小上限。</para></param>
    /// <param name="cancellationToken">Cancels the copy; the destination keeps its prior content.
    /// <para>取消复制；目标保持原有内容。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> or <paramref name="outputPath"/> is null or empty.
    /// <para><paramref name="inputPath"/> 或 <paramref name="outputPath"/> 为 null 或空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is negative.
    /// <para><paramref name="maximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static void DecompressFile(string inputPath, string outputPath, CompressionFormat format, long maximumOutputBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputPath)) throw new ArgumentNullException(nameof(inputPath));
        if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException(nameof(outputPath));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);

        WriteFileAtomically(outputPath, tempPath =>
        {
            using var input = File.OpenRead(inputPath);
            using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CompressionService.Decompress(input, output, format, maximumOutputBytes, cancellationToken);
        });
    }

    /// <summary>
    /// Automatically detects the format and decompresses a file (raw passthrough when detection fails).
    /// <para>自动检测格式并解压文件（检测失败时视为原始数据原样输出）。</para>
    /// </summary>
    /// <param name="inputPath">Source file; its magic bytes drive detection, with the extension as fallback.
    /// <para>源文件；由魔数字节驱动检测，扩展名作为回退。</para></param>
    /// <param name="outputPath">Destination file, replaced atomically on success.
    /// <para>目标文件，成功后原子替换。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> or <paramref name="outputPath"/>
    /// is null or empty. <para><paramref name="inputPath"/> 或 <paramref name="outputPath"/> 为 null 或空。</para></exception>
    public static void DecompressFileAuto(string inputPath, string outputPath)
        => DecompressFileAuto(inputPath, outputPath, long.MaxValue, CancellationToken.None);

    /// <summary>Detects and decompresses a file with an output limit.
    /// <para>自动检测并解压文件，同时限制输出大小。</para></summary>
    /// <param name="inputPath">Source file; its magic bytes drive detection, with the extension as fallback.
    /// <para>源文件；由魔数字节驱动检测，扩展名作为回退。</para></param>
    /// <param name="outputPath">Destination file.<para>目标文件。</para></param>
    /// <param name="maximumOutputBytes">Hard cap on decoded output size.
    /// <para>解压输出的硬性大小上限。</para></param>
    /// <param name="cancellationToken">Cancels before and during the copy; the destination keeps its prior content.
    /// <para>在复制开始前及进行中可取消；目标保持原有内容。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> or <paramref name="outputPath"/> is null or empty.
    /// <para><paramref name="inputPath"/> 或 <paramref name="outputPath"/> 为 null 或空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is negative.
    /// <para><paramref name="maximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static void DecompressFileAuto(string inputPath, string outputPath, long maximumOutputBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            throw new ArgumentNullException(nameof(inputPath));
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentNullException(nameof(outputPath));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);
        cancellationToken.ThrowIfCancellationRequested();
        WriteFileAtomically(outputPath, tempPath =>
        {
            using var input = File.OpenRead(inputPath);
            var format = CompressionDetector.Detect(input);
            if (format == CompressionFormat.None)
            {


                format = CompressionFormats.FromExtension(inputPath);
            }
            using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CompressionService.Decompress(input, output, format, maximumOutputBytes, cancellationToken);
        });
    }

    /// <summary>
    /// Detects the compression format of a file.
    /// <para>检测文件的压缩格式。</para>
    /// </summary>
    /// <param name="inputPath">File whose header bytes are inspected. <para>要检查其头部字节的文件。</para></param>
    /// <returns>The detected format, or <see cref="CompressionFormat.None"/> when no known magic matches.
    /// <para>检测到的格式；无已知魔数匹配时返回 <see cref="CompressionFormat.None"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputPath"/> is null or empty.
    /// <para><paramref name="inputPath"/> 为 null 或空。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist. <para>文件不存在。</para></exception>
    public static CompressionFormat DetectFile(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            throw new ArgumentNullException(nameof(inputPath));
        }

        using var input = File.OpenRead(inputPath);
        return CompressionDetector.Detect(input);
    }


    private static void WriteFileAtomically(string outputPath, Action<string> write)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            write(tempPath);
            AtomicFilePublish.Publish(tempPath, fullPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

}