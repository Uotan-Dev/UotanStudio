using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.CrossCutting.Lzo;
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
        // 覆盖内核入口分支与外层块循环边界（l > 20）附近的所有尺寸。
        // Covers the kernel entry branch and the sizes around the outer-block boundary (l > 20).
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
        // 大片零区 + 文本区交替，压缩比高、覆盖字面量与匹配两种指令。
        // Alternating zero stretches and text regions: high ratio, covers both literal and
        // match instructions.
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
        // 跨越多个外层块（每块 ≤ 0xc000 字节）的输入触发块间字面量拼接（ii -= ti 重定基）。
        // Inputs spanning several outer blocks (each ≤ 0xc000 bytes) exercise the cross-block
        // literal carry (the ii -= ti rebase).
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
        // The kernel header stores length/checksum/reserved fields; the inode supplies LZO.
        // 内核簇头只存长度、校验和及保留字段，LZO 算法由 inode 指定。
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
        // 手工构造 LZO-RLE（bitstream 版本 1）零游程位流。注意：版本前缀后的首条指令
        // 不能是会被内核入口分支（*ip > 17 视为字面量）拦截的操作码，因此先放一条
        // 6 字节字面量指令，再接零游程与 EOF：
        //   17 01                    位流版本前缀
        //   03 01..06                字面量指令（操作码 3 → 拷贝 6 字节）
        //   18 FC FF 00              零游程（t&7=0, run=(0<<3)+4=4）
        //   11 00 00                 EOF 标记
        // 期望输出：6 个字面量字节 + 4 个零字节。
        // Hand-built LZO-RLE (bitstream version 1) zero-run bitstream. The first instruction
        // after the version prefix must not be an opcode intercepted by the kernel entry branch
        // (*ip > 17 means a literal), so a 6-byte literal instruction leads, followed by the
        // zero run and EOF:
        //   17 01                    bitstream version prefix
        //   03 01..06                literal instruction (opcode 3 → copy 6 bytes)
        //   18 FC FF 00              zero run (t&7=0, run=(0<<3)+4=4)
        //   11 00 00                 EOF marker
        // Expected output: 6 literal bytes + 4 zero bytes.
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