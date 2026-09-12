using K4os.Compression.LZ4;
using UotanStudio.Firmware.CrossCutting.Lzo;
using System.Buffers;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

public enum CompressionAlgorithm : byte
{
    Lz4 = 0,

    Zstd = 1,

    Lzo = 2,
}

internal static class F2fsCompression
{
    public const int ClusterHeaderSize = F2fsCompressedCluster.HeaderSize;

    public const int ClusterPages = F2fsCompressedCluster.ClusterSizePages;

    public const int ClusterBytes = F2fsCompressedCluster.ClusterSizeBytes;

    /// <summary>
    /// Compresses a data cluster.
    /// <para>压缩一个数据簇。</para>
    /// </summary>
    public static byte[]? CompressCluster(ReadOnlySpan<byte> data, CompressionAlgorithm algorithm, int level = 0)
    {
        if (data.Length > ClusterBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), $"Cluster data exceeds {ClusterBytes} bytes.");
        }

        if (data.IsEmpty)
        {
            return null;
        }

        byte[] compressed;
        try
        {
            compressed = algorithm switch
            {
                CompressionAlgorithm.Lz4 => CompressLz4(data, level),
                CompressionAlgorithm.Zstd => CompressZstd(data, level),
                CompressionAlgorithm.Lzo => Lzo1x.Compress(data),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
            };
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {

            System.Diagnostics.Trace.TraceWarning(
                $"F2FS compression failed ({algorithm}): {ex.Message}  压缩失败");
            return null;
        }

        if (compressed.Length >= data.Length)
        {
            return null;
        }


        var cluster = new F2fsCompressedCluster
        {
            Algorithm = algorithm,
            UncompressedSize = data.Length,
            CompressedSize = compressed.Length,
        };

        var result = new byte[F2fsCompressedCluster.HeaderSize + compressed.Length];
        cluster.WriteHeaderTo(result);
        compressed.CopyTo(result, F2fsCompressedCluster.HeaderSize);
        return result;
    }

    /// <summary>
    /// Decompresses a data cluster.
    /// <para>解压缩一个数据簇。</para>
    /// </summary>
    public static byte[] DecompressCluster(
        ReadOnlySpan<byte> compressedData,
        CompressionAlgorithm algorithm,
        int expectedSize,
        int maxOutputSize)
    {
        if (expectedSize < 0 || expectedSize > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        }

        var bound = Math.Min(expectedSize, maxOutputSize);
        if (bound == 0)
        {
            return [];
        }

        return algorithm switch
        {
            CompressionAlgorithm.Lz4 => DecompressLz4(compressedData, bound),
            CompressionAlgorithm.Zstd => DecompressZstd(compressedData, bound),


            CompressionAlgorithm.Lzo => Lzo1x.Decompress(compressedData, bound),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
    }

    /// <summary>
    /// Parses metadata from a compressed cluster header.
    /// <para>从压缩簇头部解析元数据。</para>
    /// </summary>
    public static F2fsCompressedCluster ParseHeader(ReadOnlySpan<byte> data)
        => F2fsCompressedCluster.FromBytes(data);

    private static byte[] CompressLz4(ReadOnlySpan<byte> data, int level)
    {
        var lz4Level = level switch
        {
            0 => LZ4Level.L00_FAST,
            1 => LZ4Level.L03_HC,
            3 => LZ4Level.L06_HC,
            5 => LZ4Level.L09_HC,
            9 => LZ4Level.L12_MAX,
            _ => LZ4Level.L00_FAST,
        };

        var maxOutput = LZ4Codec.MaximumOutputSize(data.Length);
        var target = ArrayPool<byte>.Shared.Rent(maxOutput);
        try
        {
            var written = LZ4Codec.Encode(data, target.AsSpan(0, maxOutput), lz4Level);
            if (written <= 0)
                throw new InvalidOperationException("LZ4 compression produced no output.");
            return target.AsSpan(0, written).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(target); }
    }

    private static byte[] DecompressLz4(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        var result = new byte[expectedSize];
        var decoded = LZ4Codec.Decode(compressed, result);
        if (decoded < 0)
        {
            throw new InvalidDataException("LZ4 decompression failed.  LZ4 解压失败。");
        }

        return decoded == expectedSize ? result : result.AsSpan(0, Math.Min(decoded, expectedSize)).ToArray();
    }

    private static byte[] CompressZstd(ReadOnlySpan<byte> data, int level)
    {
        using var compressor = new ZstdSharp.Compressor(level > 0 ? level : 3);
        var result = compressor.Wrap(data);
        return result.ToArray();
    }

    private static byte[] DecompressZstd(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        var result = decompressor.Unwrap(compressed, expectedSize);
        return result.ToArray();
    }
}