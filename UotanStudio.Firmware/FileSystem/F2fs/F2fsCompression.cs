using System.Buffers;
using UotanStudio.Compress.Codecs.Lz4;
using UotanStudio.Compress.Codecs.Lzo;
using UotanStudio.Compress.Codecs.Zstd;

namespace UotanStudio.Firmware.FileSystem.F2fs;

/// <summary>
/// Cluster compression codecs selectable for F2FS file data. The numeric values are library
/// identifiers, not the kernel ids stored in inodes, which are translated separately.
/// <para>F2FS 文件数据可选的簇压缩编解码器。数值为库内部标识，并非 inode 中存储的内核 ID，两者另行换算。</para>
/// </summary>
public enum CompressionAlgorithm : byte
{
    /// <summary>LZ4 block codec, the default cluster compression. <para>LZ4 块编解码器，默认的簇压缩算法。</para></summary>
    Lz4 = 0,

    /// <summary>Zstandard block compression. <para>Zstandard 块压缩。</para></summary>
    Zstd = 1,

    /// <summary>LZO-1x compression. <para>LZO-1x 压缩。</para></summary>
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
    /// <param name="data">Cluster payload bytes, at most 1 MiB; an empty span yields no cluster and is not an error.
    /// <para>簇载荷字节，最多 1 MiB；空区间不产出簇，也不算错误。</para></param>
    /// <param name="algorithm">Codec used for the payload; a value outside <see cref="CompressionAlgorithm"/> is rejected.
    /// <para>用于载荷的编解码器；超出 <see cref="CompressionAlgorithm"/> 定义的值会被拒绝。</para></param>
    /// <param name="level">Codec level. LZ4 reads 0, 1, 3, 5 and 9 as fast through maximum effort and maps any other value to the fast path; Zstd uses the value when positive and falls back to level 3 otherwise; LZO-1x ignores it. Defaults to 0.
    /// <para>编解码等级。LZ4 将 0、1、3、5、9 视为从快速到最高压缩强度，其他取值走快速路径；Zstd 在取值为正时使用该等级，否则用等级 3；LZO-1x 忽略该参数。默认为 0。</para></param>
    /// <returns>Cluster header followed by the compressed payload, or <see langword="null"/> when the input is empty, the codec reports a failure, or the output is not smaller than the input, all of which leave the cluster stored uncompressed. A <see cref="NotSupportedException"/> from the codec is rethrown.
    /// <para>压缩簇头部紧跟压缩后的载荷；输入为空、编解码器报错或输出不比输入小时返回 <see langword="null"/>，这三种情况都按未压缩存储该簇。编解码器抛出的 <see cref="NotSupportedException"/> 会继续向外抛出。</para></returns>
    public static byte[]? CompressCluster(ReadOnlySpan<byte> data, CompressionAlgorithm algorithm, int level = 0)
    {
        if (data.Length > 1024 * 1024)
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
        compressed.AsSpan().CopyTo(result.AsSpan(F2fsCompressedCluster.HeaderSize));
        return result;
    }

    /// <summary>Decompresses a data cluster.<para>解压缩一个数据簇。</para></summary>
    /// <param name="compressedData">Codec payload following the 24-byte cluster header; it is passed to the decoder unchanged.
    /// <para>24 字节簇头部之后的编解码载荷，原样交给解码器。</para></param>
    /// <param name="algorithm">Codec that produced the payload; a value outside <see cref="CompressionAlgorithm"/> is rejected.
    /// <para>生成该载荷所使用的编解码器；超出 <see cref="CompressionAlgorithm"/> 定义的值会被拒绝。</para></param>
    /// <param name="expectedSize">Uncompressed cluster size the caller expects, which must be between 0 and 1 MiB and serves as the decode capacity unless <paramref name="maxOutputSize"/> is smaller.
    /// <para>调用方预期的解压后大小，取值必须在 0 到 1 MiB 之间；当 <paramref name="maxOutputSize"/> 更小时以后者作为解码容量。</para></param>
    /// <param name="maxOutputSize">Hard ceiling on produced bytes; a bound of 0 returns an empty array without calling the codec.
    /// <para>产出字节的硬上限；上限为 0 时直接返回空数组，不会调用编解码器。</para></param>
    /// <returns>Decoded bytes limited to the smaller of the two sizes, which the LZ4 path shortens further to the number the decoder actually wrote.
    /// <para>解压得到的字节，长度以两个容量中较小者为准；LZ4 路径还会进一步缩短为解码器实际写出的字节数。</para></returns>
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
    /// <param name="data">Cluster header bytes, at least <see cref="ClusterHeaderSize"/> long, read from the start of a stored cluster.
    /// <para>簇头部字节，长度至少为 <see cref="ClusterHeaderSize"/>，取自存储簇的起始处。</para></param>
    /// <returns>Cluster metadata holding the stored compressed size and checksum with the uncompressed size set to the full cluster size.
    /// <para>压缩簇元数据，含存储的压缩后大小与校验和，解压后大小按整簇大小填充。</para></returns>
    public static F2fsCompressedCluster ParseHeader(ReadOnlySpan<byte> data)
        => F2fsCompressedCluster.FromBytes(data);

    private static byte[] CompressLz4(ReadOnlySpan<byte> data, int level)
    {
        var lz4Level = level switch
        {
            0 => 0,
            1 => 3,
            3 => 6,
            5 => 9,
            9 => 12,
            _ => 0,
        };

        var maxOutput = Lz4BlockCodec.MaximumOutputSize(data.Length);
        var target = ArrayPool<byte>.Shared.Rent(maxOutput);
        try
        {
            var written = Lz4BlockCodec.Encode(data, target.AsSpan(0, maxOutput), lz4Level);
            if (written <= 0)
                throw new InvalidOperationException("LZ4 compression produced no output.");
            return target.AsSpan(0, written).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(target); }
    }

    private static byte[] DecompressLz4(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        var result = new byte[expectedSize];
        var decoded = Lz4BlockCodec.Decode(compressed, result);
        if (decoded < 0)
        {
            throw new FileSystemCorruptedException("LZ4 decompression failed.  LZ4 解压失败。");
        }

        return decoded == expectedSize ? result : result.AsSpan(0, Math.Min(decoded, expectedSize)).ToArray();
    }

    private static byte[] CompressZstd(ReadOnlySpan<byte> data, int level)
    {
        var maxOutput = ZstdBlockCodec.MaximumCompressedSize(data.Length);
        var target = ArrayPool<byte>.Shared.Rent(maxOutput);
        try
        {
            var written = ZstdBlockCodec.Compress(data, target.AsSpan(0, maxOutput), level > 0 ? level : 3);
            return target.AsSpan(0, written).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(target); }
    }

    /// <summary>
    /// Decompresses one zstd frame into a single result array of exactly <paramref name="expectedSize"/> bytes.
    /// <para>把一个 zstd 帧解压到长度恰为 <paramref name="expectedSize"/> 的单一结果数组。</para>
    /// </summary>
    /// <param name="compressed">Complete frame bytes following the cluster header.
    /// <para>簇头部之后的完整帧字节。</para></param>
    /// <param name="expectedSize">Array length and decode capacity; the pool-backed context decodes into that array in
    /// place, and bytes the frame leaves unused stay zero exactly as a freshly allocated buffer would.
    /// <para>数组长度与解码容量；池化上下文直接解码到该数组中，帧未覆盖的尾部保持为零，与新建缓冲区的结果相同。</para></param>
    /// <returns>The decoded array, whose length is always <paramref name="expectedSize"/>.
    /// <para>解码后的数组，长度始终为 <paramref name="expectedSize"/>。</para></returns>
    /// <exception cref="ZstdSharp.ZstdException">The frame is malformed or expands beyond the capacity.
    /// <para>帧无效或展开后超出容量。</para></exception>
    private static byte[] DecompressZstd(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        var result = GC.AllocateUninitializedArray<byte>(expectedSize);
        var decompressor = new ZstdDecoder();
        try
        {
            var written = decompressor.Decompress(compressed, result);
            if (written < expectedSize)
            {
                result.AsSpan(written).Clear();
            }

            return result;
        }
        finally
        {
            decompressor.Dispose();
        }
    }
}