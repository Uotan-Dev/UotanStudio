using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;
using UotanStudio.Firmware.BoundedContexts.Compression.Internal.Xz;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.Compression;

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