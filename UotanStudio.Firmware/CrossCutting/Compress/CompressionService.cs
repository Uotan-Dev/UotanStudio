using UotanStudio.Firmware.SharedKernel.Streams;
using UotanStudio.Firmware.CrossCutting.Compress.Compressors;
using UotanStudio.Firmware.CrossCutting.Compress.Internal.Xz;

namespace UotanStudio.Firmware.CrossCutting.Compress;

public static class CompressionService
{
    /// <summary>
    /// Compresses data.
    /// <para>压缩数据。</para>
    /// </summary>
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
    /// Decompresses data.
    /// <para>解压数据。</para>
    /// </summary>
    public static byte[] Decompress(byte[] data, CompressionFormat format)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ValidateForDecompression(format);
        return DecompressCore(data, format);
    }


    /// <summary>
    /// Streaming compression.
    /// <para>流式压缩。</para>
    /// </summary>
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
            case CompressionFormat.Lzop:
                LzopCompressor.Compress(input, output);
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
            case CompressionFormat.Zopfli:
                break;
            default:
                throw new CompressionException($"Streaming compression is not supported for format: {format}");
        }
    }

    /// <summary>
    /// Streaming decompression.
    /// <para>流式解压。</para>
    /// </summary>
    public static void Decompress(Stream input, Stream output, CompressionFormat format)
        => Decompress(input, output, format, long.MaxValue, CancellationToken.None);

    /// <summary>
    /// Decompresses a stream with a unified output limit and cancellation support.
    /// <para>使用统一输出上限和取消支持进行流式解压。</para>
    /// </summary>
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


    /// <summary>
    /// Compresses a file.
    /// <para>压缩文件。</para>
    /// </summary>
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


        WriteFileAtomically(outputPath, tempPath =>
        {
            using var input = File.OpenRead(inputPath);
            using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Compress(input, output, format, options);
        });
    }

    /// <summary>
    /// Decompresses a file with the specified format.
    /// <para>按指定格式解压文件。</para>
    /// </summary>
    public static void DecompressFile(string inputPath, string outputPath, CompressionFormat format)
        => DecompressFile(inputPath, outputPath, format, long.MaxValue, CancellationToken.None);

    /// <summary>
    /// Decompresses a file with a limit, atomically replacing the destination on success and cleaning the temporary file on failure.
    /// <para>按指定格式有界解压文件；成功后原子替换目标，失败时清理临时文件。</para>
    /// </summary>
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
    /// Automatically detects the format and decompresses a file (raw passthrough when detection fails).
    /// <para>自动检测格式并解压文件（检测失败时视为原始数据原样输出）。</para>
    /// </summary>
    public static void DecompressFileAuto(string inputPath, string outputPath)
        => DecompressFileAuto(inputPath, outputPath, long.MaxValue, CancellationToken.None);

    /// <summary>
    /// Detects and decompresses a file with an output limit.
    /// <para>自动检测并解压文件，同时限制输出大小。</para>
    /// </summary>
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


                format = CompressionFormats.FromExtension(inputPath);
            }
            using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Decompress(input, output, format, maximumOutputBytes, cancellationToken);
        });
    }


    /// <summary>
    /// Detects the compression format of the data by magic bytes.
    /// <para>通过魔数检测数据的压缩格式。</para>
    /// </summary>
    public static CompressionFormat Detect(byte[] data) => CompressionFormats.Detect(data);

    /// <summary>
    /// Detects the compression format from the stream head; restores the position when the stream is seekable.
    /// <para>通过流头部检测压缩格式；若流可定位则恢复原位置。</para>
    /// </summary>
    public static CompressionFormat Detect(Stream input) => CompressionFormats.Detect(input);

    /// <summary>
    /// Detects the compression format of a file.
    /// <para>检测文件的压缩格式。</para>
    /// </summary>
    public static CompressionFormat DetectFile(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            throw new ArgumentNullException(nameof(inputPath));
        }

        using var input = File.OpenRead(inputPath);
        return CompressionFormats.Detect(input);
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


            CompressionFormat.Lzop => LzopCompressor.Compress(data),
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


            CompressionFormat.Lzop => LzopCompressor.Decompress(data),


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
