using UotanStudio.Firmware.Common.Streams;
using UotanStudio.Firmware.Compress.Compressors;
using UotanStudio.Firmware.Compress.Internal.Xz;

namespace UotanStudio.Firmware.Compress;

/// <summary>
/// 压缩/解压统一门面：按 <see cref="CompressionFormat"/> 调度到各格式实现，
/// 提供 byte[]、Stream 与文件级 API，以及自动格式检测。
/// <para>Unified compression/decompression facade: dispatches to per-format implementations
/// by <see cref="CompressionFormat"/>, providing byte[]/Stream/file-level APIs and automatic
/// format detection.</para>
/// <para><see cref="CompressionFormat.None"/> 被视为恒等操作（原样返回数据）。</para>
/// <para><see cref="CompressionFormat.None"/> is treated as an identity operation (data passed through unchanged).</para>
/// </summary>
public static class CompressionService
{
    /// <summary>
    /// 压缩数据。
    /// <para>Compresses data.</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="format">压缩格式。<para>The compression format.</para></param>
    /// <param name="options">压缩选项；null 表示各格式默认。<para>Compression options; null means each format's defaults.</para></param>
    /// <returns>压缩后的数据。<para>The compressed data.</para></returns>
    /// <exception cref="ArgumentNullException">data 为 null 时抛出。<para>Thrown when data is null.</para></exception>
    /// <exception cref="ArgumentException">格式不受支持时抛出。<para>Thrown when the format is unsupported.</para></exception>
    /// <exception cref="CompressionException">压缩失败时抛出。<para>Thrown when compression fails.</para></exception>
    public static byte[] Compress(byte[] data, CompressionFormat format, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ValidateForCompression(format);
        return CompressCore(data, format, options);
    }

    /// <summary>
    /// 解压数据。
    /// <para>Decompresses data.</para>
    /// </summary>
    /// <param name="data">待解压数据。<para>Data to decompress.</para></param>
    /// <param name="format">压缩格式。<para>The compression format.</para></param>
    /// <returns>解压后的原始数据。<para>The decompressed original data.</para></returns>
    /// <exception cref="ArgumentNullException">data 为 null 时抛出。<para>Thrown when data is null.</para></exception>
    /// <exception cref="ArgumentException">格式不受支持时抛出。<para>Thrown when the format is unsupported.</para></exception>
    /// <exception cref="CompressionException">解压失败时抛出。<para>Thrown when decompression fails.</para></exception>
    public static byte[] Decompress(byte[] data, CompressionFormat format)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ValidateForDecompression(format);
        return DecompressCore(data, format);
    }

    // Stream API / 流式 API

    /// <summary>
    /// 流式压缩。流式格式（gzip/zlib/deflate/brotli/bzip2/lz4）直接以管道处理；
    /// 块格式（xz/lzma/lz4_legacy/zstd）的底层编码器本身接受流，同样直接管道。
    /// <para>Streaming compression. Streaming formats (gzip/zlib/deflate/brotli/bzip2/lz4)
    /// are piped directly; block formats (xz/lzma/lz4_legacy/zstd) whose encoders accept
    /// streams are also piped directly.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入压缩结果的输出流。<para>The output stream that receives the compressed data.</para></param>
    /// <param name="format">压缩格式。<para>The compression format.</para></param>
    /// <param name="options">压缩选项；null 表示各格式默认。<para>Compression options; null means each format's defaults.</para></param>
    /// <exception cref="ArgumentNullException">input 或 output 为 null 时抛出。<para>Thrown when input or output is null.</para></exception>
    /// <exception cref="ArgumentException">格式不受支持时抛出。<para>Thrown when the format is unsupported.</para></exception>
    /// <exception cref="CompressionException">压缩失败时抛出。<para>Thrown when compression fails.</para></exception>
    public static void Compress(Stream input, Stream output, CompressionFormat format, CompressionOptions? options = null)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        ValidateForCompression(format);

        if (format == CompressionFormat.None)
        {
            input.CopyTo(output);
            return;
        }

        var info = CompressionFormats.GetInfo(format)!;
        if (info.IsStreaming)
        {
            CompressStreaming(input, output, format, options);
            return;
        }

        switch (format)
        {
            case CompressionFormat.Xz:
                XzCompressor.Compress(input, output,
                    options?.DictionarySize ?? Lzma2Encoder.DefaultDictionarySize,
                    maxDegreeOfParallelism: options?.MaxDegreeOfParallelism);
                return;
            case CompressionFormat.Lzma:
                LzmaCompressor.Compress(input, output);
                return;
            case CompressionFormat.Zstd:
                ZstdCompressor.Compress(input, output, options);
                return;
            case CompressionFormat.Lz4Legacy:
                Lz4Compressor.CompressLegacy(input, output, options);
                return;
            case CompressionFormat.None:
                break;
            case CompressionFormat.Gzip:
                break;
            case CompressionFormat.Zlib:
                break;
            case CompressionFormat.Deflate:
                break;
            case CompressionFormat.Brotli:
                break;
            case CompressionFormat.Lz4:
                break;
            case CompressionFormat.Bzip2:
                break;
            case CompressionFormat.Lzop:
                break;
            case CompressionFormat.Zopfli:
                break;
            default:
                throw new CompressionException($"Streaming compression is not supported for format: {format}");
        }
    }

    /// <summary>
    /// 流式解压。流式格式直接以管道处理；xz/lzma/zstd/lz4_legacy 的底层解码器本身
    /// 支持流式，同样直接管道，避免大输入整块缓冲。
    /// <para>Streaming decompression. Streaming formats are piped directly; the underlying
    /// decoders of xz/lzma/zstd/lz4_legacy are inherently streaming and are also piped
    /// directly, avoiding full buffering of large inputs.</para>
    /// </summary>
    /// <param name="input">待解压的输入流。<para>The input stream to decompress.</para></param>
    /// <param name="output">写入解压结果的输出流。<para>The output stream that receives the decompressed data.</para></param>
    /// <param name="format">压缩格式。<para>The compression format.</para></param>
    /// <exception cref="ArgumentNullException">input 或 output 为 null 时抛出。<para>Thrown when input or output is null.</para></exception>
    /// <exception cref="ArgumentException">格式不受支持时抛出。<para>Thrown when the format is unsupported.</para></exception>
    /// <exception cref="CompressionException">解压失败时抛出。<para>Thrown when decompression fails.</para></exception>
    public static void Decompress(Stream input, Stream output, CompressionFormat format)
        => Decompress(input, output, format, long.MaxValue, CancellationToken.None);

    /// <summary>
    /// 使用统一输出上限和取消支持进行流式解压。
    /// <para>Decompresses a stream with a unified output limit and cancellation support.</para>
    /// </summary>
    /// <param name="input">待解压输入流。<para>The compressed input stream.</para></param>
    /// <param name="output">结果输出流。<para>The output stream receiving decompressed data.</para></param>
    /// <param name="format">压缩格式。<para>The compression format.</para></param>
    /// <param name="maximumOutputBytes">允许输出的最大字节数。<para>The maximum number of output bytes allowed.</para></param>
    /// <param name="cancellationToken">取消令牌。<para>The cancellation token.</para></param>
    public static void Decompress(Stream input, Stream output, CompressionFormat format, long maximumOutputBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateForDecompression(format);

        using var limitedOutput = new CountingWriteStream(output, maximumOutputBytes, leaveOpen: true, cancellationToken: cancellationToken);
        output = limitedOutput;

        if (format == CompressionFormat.None)
        {
            input.CopyTo(output);
            return;
        }

        // 底层解码器支持流式的块格式：直接管道，避免整块缓冲。
        // Block formats whose decoders are inherently streaming: pipe directly.
        switch (format)
        {
            case CompressionFormat.Xz:
                XzCompressor.Decompress(input, output);
                return;
            case CompressionFormat.Lzma:
                LzmaCompressor.Decompress(input, output);
                return;
            case CompressionFormat.Zstd:
                ZstdCompressor.Decompress(input, output);
                return;
            case CompressionFormat.Lz4Legacy:
                Lz4Compressor.DecompressLegacy(input, output);
                return;
            case CompressionFormat.None:
                break;
            case CompressionFormat.Gzip:
                break;
            case CompressionFormat.Zlib:
                break;
            case CompressionFormat.Deflate:
                break;
            case CompressionFormat.Brotli:
                break;
            case CompressionFormat.Lz4:
                break;
            case CompressionFormat.Bzip2:
                break;
            case CompressionFormat.Lzop:
                break;
            case CompressionFormat.Zopfli:
                break;
            default:
                break;
        }

        var info = CompressionFormats.GetInfo(format)!;
        if (info.IsStreaming)
        {
            DecompressStreaming(input, output, format);
            return;
        }

        if (format == CompressionFormat.Zopfli)
        {
            GzipCompressor.Decompress(input, output);
            return;
        }

        if (format == CompressionFormat.Lzop)
        {
            LzopCompressor.Decompress(input, output, maximumOutputBytes);
            return;
        }

        throw new CompressionException($"Streaming decompression is not supported for format: {format}");
    }

    // File API / 文件 API

    /// <summary>
    /// 压缩文件。
    /// <para>Compresses a file.</para>
    /// </summary>
    /// <param name="inputPath">输入文件路径。<para>Path to the input file.</para></param>
    /// <param name="outputPath">输出文件路径。<para>Path to the output file.</para></param>
    /// <param name="format">压缩格式。<para>The compression format.</para></param>
    /// <param name="options">压缩选项；null 表示各格式默认。<para>Compression options; null means each format's defaults.</para></param>
    /// <exception cref="ArgumentNullException">路径为 null 或空时抛出。<para>Thrown when a path is null or empty.</para></exception>
    /// <exception cref="CompressionException">压缩失败时抛出。<para>Thrown when compression fails.</para></exception>
    public static void CompressFile(string inputPath, string outputPath, CompressionFormat format, CompressionOptions? options = null)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            throw new ArgumentNullException(nameof(inputPath));
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentNullException(nameof(outputPath));
        }

        using var input = File.OpenRead(inputPath);
        using var output = File.Create(outputPath);
        Compress(input, output, format, options);
    }

    /// <summary>
    /// 按指定格式解压文件。
    /// <para>Decompresses a file with the specified format.</para>
    /// </summary>
    /// <param name="inputPath">输入文件路径。<para>Path to the input file.</para></param>
    /// <param name="outputPath">输出文件路径。<para>Path to the output file.</para></param>
    /// <param name="format">压缩格式。<para>The compression format.</para></param>
    /// <exception cref="ArgumentNullException">路径为 null 或空时抛出。<para>Thrown when a path is null or empty.</para></exception>
    /// <exception cref="CompressionException">解压失败时抛出。<para>Thrown when decompression fails.</para></exception>
    public static void DecompressFile(string inputPath, string outputPath, CompressionFormat format)
        => DecompressFile(inputPath, outputPath, format, long.MaxValue, CancellationToken.None);

    /// <summary>
    /// 按指定格式有界解压文件；成功后原子替换目标，失败时清理临时文件。
    /// <para>Decompresses a file with a limit, atomically replacing the destination on success and cleaning the temporary file on failure.</para>
    /// </summary>
    /// <param name="inputPath">输入文件路径。<para>Path to the input file.</para></param>
    /// <param name="outputPath">输出文件路径。<para>Path to the output file.</para></param>
    /// <param name="format">压缩格式。<para>The compression format.</para></param>
    /// <param name="maximumOutputBytes">最大输出字节数。<para>The maximum output byte count.</para></param>
    /// <param name="cancellationToken">取消令牌。<para>The cancellation token.</para></param>
    public static void DecompressFile(string inputPath, string outputPath, CompressionFormat format, long maximumOutputBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputPath)) throw new ArgumentNullException(nameof(inputPath));
        if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException(nameof(outputPath));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);

        WriteFileAtomically(outputPath, tempPath =>
        {
            using var input = File.OpenRead(inputPath);
            using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Decompress(input, output, format, maximumOutputBytes, cancellationToken);
        });
    }

    /// <summary>
    /// 自动检测格式并解压文件（检测失败时视为原始数据原样输出）。
    /// <para>Automatically detects the format and decompresses a file (raw passthrough when detection fails).</para>
    /// </summary>
    /// <param name="inputPath">输入文件路径。<para>Path to the input file.</para></param>
    /// <param name="outputPath">输出文件路径。<para>Path to the output file.</para></param>
    /// <exception cref="ArgumentNullException">路径为 null 或空时抛出。<para>Thrown when a path is null or empty.</para></exception>
    /// <exception cref="CompressionException">解压失败时抛出。<para>Thrown when decompression fails.</para></exception>
    public static void DecompressFileAuto(string inputPath, string outputPath)
        => DecompressFileAuto(inputPath, outputPath, long.MaxValue, CancellationToken.None);

    /// <summary>Automatically detects and decompresses a file with an output limit.</summary>
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
            var format = CompressionFormats.Detect(input);
            if (format == CompressionFormat.None)
            {
                // 无魔数的格式(Brotli 等)回退到扩展名推断。
                // Formats without magic bytes (Brotli etc.) fall back to extension inference.
                format = CompressionFormats.FromExtension(inputPath);
            }
            using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Decompress(input, output, format, maximumOutputBytes, cancellationToken);
        });
    }

    // Detection / 格式检测

    /// <summary>
    /// 通过魔数检测数据的压缩格式。
    /// <para>Detects the compression format of the data by magic bytes.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to detect.</para></param>
    /// <returns>检测到的格式；无法识别时返回 <see cref="CompressionFormat.None"/>。<para>The detected format; <see cref="CompressionFormat.None"/> when unrecognized.</para></returns>
    /// <exception cref="ArgumentNullException">data 为 null 时抛出。<para>Thrown when data is null.</para></exception>
    public static CompressionFormat Detect(byte[] data) => CompressionFormats.Detect(data);

    /// <summary>
    /// 通过流头部检测压缩格式；若流可定位则恢复原位置。
    /// <para>Detects the compression format from the stream head; restores the position when the stream is seekable.</para>
    /// </summary>
    /// <param name="input">待检测的流。<para>The stream to detect.</para></param>
    /// <returns>检测到的格式；无法识别时返回 <see cref="CompressionFormat.None"/>。<para>The detected format; <see cref="CompressionFormat.None"/> when unrecognized.</para></returns>
    /// <exception cref="ArgumentNullException">input 为 null 时抛出。<para>Thrown when input is null.</para></exception>
    public static CompressionFormat Detect(Stream input) => CompressionFormats.Detect(input);

    /// <summary>
    /// 检测文件的压缩格式。
    /// <para>Detects the compression format of a file.</para>
    /// </summary>
    /// <param name="inputPath">待检测文件的路径。<para>Path to the file to detect.</para></param>
    /// <returns>检测到的格式；无法识别时返回 <see cref="CompressionFormat.None"/>。<para>The detected format; <see cref="CompressionFormat.None"/> when unrecognized.</para></returns>
    /// <exception cref="ArgumentNullException">inputPath 为 null 或空时抛出。<para>Thrown when inputPath is null or empty.</para></exception>
    public static CompressionFormat DetectFile(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            throw new ArgumentNullException(nameof(inputPath));
        }

        using var input = File.OpenRead(inputPath);
        return CompressionFormats.Detect(input);
    }

    // Internals / 内部实现

    private static void WriteFileAtomically(string outputPath, Action<string> write)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            write(tempPath);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void ValidateForCompression(CompressionFormat format)
    {
        if (format == CompressionFormat.None)
        {
            return;
        }

        var info = CompressionFormats.GetInfo(format);
        if (info == null || !info.CanCompress)
        {
            throw new ArgumentException($"Compression is not supported for format: {format}", nameof(format));
        }
    }

    private static void ValidateForDecompression(CompressionFormat format)
    {
        if (format == CompressionFormat.None)
        {
            return;
        }

        var info = CompressionFormats.GetInfo(format);
        if (info == null || !info.CanDecompress)
        {
            throw new ArgumentException($"Decompression is not supported for format: {format}", nameof(format));
        }
    }

    private static byte[] CompressCore(byte[] data, CompressionFormat format, CompressionOptions? options)
    {
        return format switch
        {
            CompressionFormat.Gzip => GzipCompressor.Compress(data, options),
            CompressionFormat.Zlib => ZlibCompressor.Compress(data, options),
            CompressionFormat.Deflate => DeflateCompressor.Compress(data, options),
            CompressionFormat.Brotli => BrotliCompressor.Compress(data, options),
            CompressionFormat.Lz4 => Lz4Compressor.Compress(data, options),
            CompressionFormat.Lz4Legacy => Lz4Compressor.CompressLegacy(data, options),
            CompressionFormat.Lzma => LzmaCompressor.Compress(data),
            CompressionFormat.Xz => XzCompressor.Compress(data, options?.DictionarySize ?? Lzma2Encoder.DefaultDictionarySize, maxDegreeOfParallelism: options?.MaxDegreeOfParallelism),
            CompressionFormat.Bzip2 => Bzip2Compressor.Compress(data, options),
            CompressionFormat.Zstd => ZstdCompressor.Compress(data, options),
            CompressionFormat.None => data,
            // LZOP / Zopfli 压缩均不支持（LZOP 无托管编码器；Zopfli 为 gzip 兼容输出，压缩端不提供）。
            // LZOP / Zopfli compression is not supported (no managed LZO encoder; Zopfli is gzip-compatible output and is not produced here).
            CompressionFormat.Lzop => throw new NotSupportedException("LZOP compression is not supported. LZOP 压缩不受支持。"),
            CompressionFormat.Zopfli => throw new NotSupportedException("Zopfli compression is not supported. Zopfli 压缩不受支持。"),
            _ => throw new ArgumentException($"Unsupported compression format: {format}", nameof(format))
        };
    }

    private static byte[] DecompressCore(byte[] data, CompressionFormat format)
    {
        return format switch
        {
            CompressionFormat.Gzip => GzipCompressor.Decompress(data),
            CompressionFormat.Zlib => ZlibCompressor.Decompress(data),
            CompressionFormat.Deflate => DeflateCompressor.Decompress(data),
            CompressionFormat.Brotli => BrotliCompressor.Decompress(data),
            CompressionFormat.Lz4 => Lz4Compressor.Decompress(data),
            CompressionFormat.Lz4Legacy => Lz4Compressor.DecompressLegacy(data),
            CompressionFormat.Lzma => LzmaCompressor.Decompress(data),
            CompressionFormat.Xz => XzCompressor.Decompress(data),
            CompressionFormat.Bzip2 => Bzip2Compressor.Decompress(data),
            CompressionFormat.Zstd => ZstdCompressor.Decompress(data),
            CompressionFormat.None => data,
            // LZOP 仅支持解压（SharpZipLib 无 LZO 支持，由本库内嵌 LZO1X 解码器实现）。
            // LZOP decompression is supported via the embedded LZO1X decoder; compression is not.
            CompressionFormat.Lzop => LzopCompressor.Decompress(data),
            // Zopfli 产物是标准 gzip 流，解压直接复用 gzip 解码器；压缩端不支持。
            // Zopfli output is a standard gzip stream, so the gzip decoder is reused; compression is not supported.
            CompressionFormat.Zopfli => GzipCompressor.Decompress(data),
            _ => throw new ArgumentException($"Unsupported compression format: {format}", nameof(format))
        };
    }

    private static void CompressStreaming(Stream input, Stream output, CompressionFormat format, CompressionOptions? options)
    {
        switch (format)
        {
            case CompressionFormat.Gzip:
                GzipCompressor.Compress(input, output, options);
                break;
            case CompressionFormat.Zlib:
                ZlibCompressor.Compress(input, output, options);
                break;
            case CompressionFormat.Deflate:
                DeflateCompressor.Compress(input, output, options);
                break;
            case CompressionFormat.Brotli:
                BrotliCompressor.Compress(input, output, options);
                break;
            case CompressionFormat.Bzip2:
                Bzip2Compressor.Compress(input, output);
                break;
            case CompressionFormat.Lz4:
                Lz4Compressor.Compress(input, output, options);
                break;
            case CompressionFormat.None:
                break;
            case CompressionFormat.Lz4Legacy:
                break;
            case CompressionFormat.Lzma:
                break;
            case CompressionFormat.Xz:
                break;
            case CompressionFormat.Zstd:
                break;
            case CompressionFormat.Lzop:
                break;
            case CompressionFormat.Zopfli:
                break;
            default:
                throw new ArgumentException($"Format is not streaming: {format}", nameof(format));
        }
    }

    private static void DecompressStreaming(Stream input, Stream output, CompressionFormat format)
    {
        switch (format)
        {
            case CompressionFormat.Gzip:
                GzipCompressor.Decompress(input, output);
                break;
            case CompressionFormat.Zlib:
                ZlibCompressor.Decompress(input, output);
                break;
            case CompressionFormat.Deflate:
                DeflateCompressor.Decompress(input, output);
                break;
            case CompressionFormat.Brotli:
                BrotliCompressor.Decompress(input, output);
                break;
            case CompressionFormat.Bzip2:
                Bzip2Compressor.Decompress(input, output);
                break;
            case CompressionFormat.Lz4:
                Lz4Compressor.Decompress(input, output);
                break;
            case CompressionFormat.None:
                break;
            case CompressionFormat.Lz4Legacy:
                break;
            case CompressionFormat.Lzma:
                break;
            case CompressionFormat.Xz:
                break;
            case CompressionFormat.Zstd:
                break;
            case CompressionFormat.Lzop:
                break;
            case CompressionFormat.Zopfli:
                break;
            default:
                throw new ArgumentException($"Format is not streaming: {format}", nameof(format));
        }
    }
}