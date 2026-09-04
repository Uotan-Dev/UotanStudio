using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs 压缩算法枚举。
/// <para>F2FS compression algorithm identifiers.</para>
/// </summary>
public enum CompressionAlgorithm : byte
{
    /// <summary>
    /// LZ4 压缩（快速，推荐）。
    /// <para>LZ4 compression (fast, recommended).</para>
    /// </summary>
    Lz4 = 0,

    /// <summary>
    /// Zstandard 压缩（高压缩比）。
    /// <para>Zstandard compression (high ratio).</para>
    /// </summary>
    Zstd = 1,

    /// <summary>
    /// LZO 原始压缩（不支持，调用时抛出异常）。
    /// <para>LZO legacy compression (unsupported; throws on use).</para>
    /// </summary>
    Lzo = 2,
}

/// <summary>
/// f2fs 压缩/解压缩工具：以 16 页（64 KB）簇为单位压缩和解压文件数据。
/// 支持 LZ4 和 ZSTD 算法；LZO 不可用（抛出 <see cref="NotSupportedException"/>）。
/// 解压时强制上限校验以防止 zip-bomb 攻击。
/// <para>F2FS compression/decompression utility: compresses and decompresses file data in
/// 16-page (64 KB) clusters. Supports LZ4 and ZSTD; LZO is unavailable (throws
/// <see cref="NotSupportedException"/>). Decompression enforces a bounded output limit to prevent
/// zip-bomb attacks.</para>
/// </summary>
internal static class F2fsCompression
{
    /// <summary>
    /// 压缩簇磁盘头大小（字节）。
    /// <para>On-disk cluster header size in bytes.</para>
    /// </summary>
    public const int ClusterHeaderSize = F2fsCompressedCluster.HeaderSize;

    /// <summary>
    /// 每簇页数（CLUSTER_BLOCK_SIZE）。
    /// <para>Pages per cluster (CLUSTER_BLOCK_SIZE).</para>
    /// </summary>
    public const int ClusterPages = F2fsCompressedCluster.ClusterSizePages;

    /// <summary>
    /// 每簇字节数。
    /// <para>Bytes per cluster.</para>
    /// </summary>
    public const int ClusterBytes = F2fsCompressedCluster.ClusterSizeBytes;

    /// <summary>
    /// inode 标志位：文件已压缩（F2FS_COMPRESSED_FILE）。
    /// <para>Inode flag: compressed file (F2FS_COMPRESSED_FILE).</para>
    /// </summary>
    public const uint CompressedFileFlag = 0x0000_0008;

    /// <summary>
    /// 压缩一个数据簇。返回压缩后的字节数组（含12 字节头部 + 压缩数据），
    /// 若压缩后不小于原始数据则返回 null（表示不值得压缩）。
    /// <para>Compresses a data cluster. Returns the compressed byte array (12-byte header + data)
    /// or null if the result is not smaller than the input.</para>
    /// </summary>
    /// <param name="data">原始数据（≤ 65536 字节）。Input data (≤ 65536 bytes).</param>
    /// <param name="algorithm">压缩算法。Compression algorithm.</param>
    /// <param name="level">压缩级别（0 = 默认）。Compression level (0 = default).</param>
    /// <returns>含头部的压缩字节数组，或 null。Compressed bytes with header, or null.</returns>
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
                CompressionAlgorithm.Lzo => throw new NotSupportedException(
                    "LZO compression is not supported. LZO 压缩不受支持。"),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
            };
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 压缩失败：视为不可压缩。Compression failure: treat as incompressible.
            System.Diagnostics.Trace.TraceWarning(
                $"F2FS compression failed ({algorithm}): {ex.Message}  压缩失败");
            return null;
        }

        if (compressed.Length >= data.Length)
        {
            return null; // 不值得压缩。Not worth compressing.
        }

        // 构建含头部的输出。Build output with header.
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
    /// 解压缩一个数据簇。返回解压后的字节数组，长度不超过 <paramref name="maxOutputSize"/>。
    /// <para>Decompresses a data cluster. Returns the decompressed byte array, bounded by
    /// <paramref name="maxOutputSize"/>.</para>
    /// </summary>
    /// <param name="compressedData">含头部的压缩数据。Compressed data with header.</param>
    /// <param name="algorithm">压缩算法（覆盖头部内算法字段）。Compression algorithm.</param>
    /// <param name="expectedSize">期望输出大小（字节）。Expected output size in bytes.</param>
    /// <param name="maxOutputSize">最大输出大小上限。Maximum output size cap.</param>
    /// <returns>解压后的字节数组。Decompressed byte array.</returns>
    public static byte[] DecompressCluster(
        ReadOnlySpan<byte> compressedData,
        CompressionAlgorithm algorithm,
        int expectedSize,
        int maxOutputSize)
    {
        if (expectedSize < 0 || expectedSize > ClusterBytes)
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
            CompressionAlgorithm.Lzo => throw new NotSupportedException(
                "LZO decompression is not supported. LZO 解压不受支持。"),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
    }

    /// <summary>
    /// 从压缩簇头部解析元数据。
    /// <para>Parses metadata from a compressed cluster header.</para>
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
        var target = new byte[maxOutput];
        var written = LZ4Codec.Encode(data, target, lz4Level);
        if (written <= 0)
        {
            throw new InvalidOperationException("LZ4 compression produced no output.");
        }

        return target.AsSpan(0, written).ToArray();
    }

    private static byte[] DecompressLz4(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        var result = new byte[expectedSize];
        var decoded = LZ4Codec.Decode(compressed, result);
        if (decoded < 0)
        {
            throw new InvalidDataException("LZ4 decompression failed.  LZ4 解压失败。");
        }

        return result.AsSpan(0, Math.Min(decoded, expectedSize)).ToArray();
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
