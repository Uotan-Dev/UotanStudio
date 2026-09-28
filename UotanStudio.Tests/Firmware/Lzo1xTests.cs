using UotanStudio.Firmware.Compression.Codecs.Lzo;
using UotanStudio.Firmware.FileSystem.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// 托管 LZO1X 实现的回环与格式测试：压缩 → 解压逐字节一致（对齐内核 lzo1x_1_compress /
/// lzo1x_decompress_safe 语义），f2fs 压缩簇经 LZO 算法可读回，LZO-RLE 零游程位流可解压。
/// <para>Managed LZO1X round-trip and format tests: compress → decompress is byte-exact
/// (matching kernel lzo1x_1_compress / lzo1x_decompress_safe semantics), f2fs compressed
/// clusters round-trip through the LZO algorithm, and LZO-RLE zero-run bitstreams decompress.</para>
/// </summary>
public class Lzo1xTests
{
    [Fact]
    public void RoundTrip_EmptyInput_ProducesValidStream()
    {
        var compressed = Lzo1x.Compress([]);
        var decompressed = Lzo1x.Decompress(compressed, 0);
        Assert.Empty(decompressed);
    }

    [Fact]
    public void RoundTrip_TinyInputs_MatchOriginal()
    {
        foreach (var size in new[] { 1, 2, 3, 4, 5, 19, 20, 21, 24, 40, 64 })
        {
            var data = new byte[size];
            new Random(size).NextBytes(data);
            AssertRoundTrip(data);
        }
    }

    [Fact]
    public void RoundTrip_RandomData_MatchOriginal()
    {
        var data = new byte[100_000];
        new Random(42).NextBytes(data);
        AssertRoundTrip(data);
    }

    [Fact]
    public void RoundTrip_AllZeros_MatchOriginal()
    {
        var data = new byte[70_000];
        AssertRoundTrip(data);
    }

    [Fact]
    public void RoundTrip_RepetitivePattern_MatchOriginal()
    {
        var data = new byte[131_072];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        AssertRoundTrip(data);
    }

    [Fact]
    public void RoundTrip_MixedZerosAndText_MatchOriginal()
    {
        var data = new byte[100_000];
        var text = "the quick brown fox jumps over the lazy dog 0123456789"u8.ToArray();
        for (var i = 0; i < data.Length; i += 1000)
        {
            var len = Math.Min(text.Length, data.Length - i);
            text.AsSpan(0, len).CopyTo(data.AsSpan(i));
        }

        AssertRoundTrip(data);
    }

    [Fact]
    public void RoundTrip_CrossBlockSizes_MatchOriginal()
    {
        var data = new byte[0xc000 * 2 + 123];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 7) ^ (i >> 3));
        }

        AssertRoundTrip(data);
    }

    [Fact]
    public void CompressedData_IsSmallerForCompressibleInput()
    {
        var data = new byte[65_536];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 7);
        }

        var compressed = Lzo1x.Compress(data);
        Assert.True(compressed.Length < data.Length / 2, $"compressed length {compressed.Length}");
    }

    [Fact]
    public void F2fsCluster_LzoAlgorithm_UsesKernelHeaderAndInodeAlgorithm()
    {
        var data = new byte[F2fsCompression.ClusterBytes];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 13);
        }

        var cluster = F2fsCompression.CompressCluster(data, CompressionAlgorithm.Lzo);
        Assert.NotNull(cluster);

        var header = F2fsCompression.ParseHeader(cluster);
        Assert.Equal(24, F2fsCompression.ClusterHeaderSize);
        Assert.Equal(cluster.Length - 24, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(cluster));
        Assert.Equal(cluster.Length - 24, header.CompressedSize);
        Assert.Equal(0U, header.Checksum);
        Assert.True(cluster.AsSpan(8, 16).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(data.Length, header.UncompressedSize);

        var decompressed = F2fsCompression.DecompressCluster(
            cluster.AsSpan(F2fsCompression.ClusterHeaderSize),
            CompressionAlgorithm.Lzo,
            header.UncompressedSize,
            F2fsCompression.ClusterBytes);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Decompress_ZeroRunBitstream_LzoRle_ClearedOutput()
    {
        var bitstream = new byte[]
        {
            17, 1,
            0x03, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06,
            0x18, 0xFC, 0xFF, 0x00,
            0x11, 0x00, 0x00,
        };
        var output = new byte[16];
        var status = Lzo1x.TryDecompress(bitstream, output, out var written);

        Assert.Equal(LzoStatus.Ok, status);
        Assert.Equal(10, written);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 0, 0, 0, 0 }, output.AsSpan(0, 10).ToArray());
    }

    [Fact]
    public void Decompress_TruncatedInput_ReturnsError()
    {
        var compressed = Lzo1x.Compress(new byte[1000]);
        var output = new byte[1000];
        Assert.NotEqual(LzoStatus.Ok, Lzo1x.TryDecompress(compressed.AsSpan(0, compressed.Length / 2), output, out _));
    }

    [Fact]
    public void Decompress_OutputTooSmall_ReturnsOutputOverrun()
    {
        var data = new byte[1000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        var compressed = Lzo1x.Compress(data);
        Assert.Equal(LzoStatus.OutputOverrun, Lzo1x.TryDecompress(compressed, new byte[100], out _));
    }

    [Fact]
    public void Decompress_TrailingGarbage_ReturnsInputNotConsumed()
    {
        var compressed = Lzo1x.Compress(new byte[100]);
        var padded = new byte[compressed.Length + 5];
        compressed.CopyTo(padded, 0);
        Assert.Equal(LzoStatus.InputNotConsumed, Lzo1x.TryDecompress(padded, new byte[100], out _));
    }

    private static void AssertRoundTrip(byte[] data)
    {
        var compressed = Lzo1x.Compress(data);
        var decompressed = Lzo1x.Decompress(compressed, data.Length);
        Assert.Equal(data, decompressed);
    }
}