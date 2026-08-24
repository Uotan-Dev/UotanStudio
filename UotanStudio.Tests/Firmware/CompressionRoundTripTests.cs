using UotanStudio.Firmware.Compress;
using UotanStudio.Firmware.Compress.Compressors;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Compression round-trip tests: byte-array round-trip for the 9 supported
/// compressible formats, plus hand-constructed LZOP samples for decompression.
/// <para>压缩往返测试：9 种可压缩格式的字节数组往返，外加手构造的 LZOP 解压样本。</para>
/// </summary>
public class CompressionRoundTripTests
{
    public static TheoryData<CompressionFormat> RoundTripFormats => new()
    {
        CompressionFormat.Gzip,
        CompressionFormat.Zlib,
        CompressionFormat.Deflate,
        CompressionFormat.Brotli,
        CompressionFormat.Bzip2,
        CompressionFormat.Lz4,
        CompressionFormat.Lzma,
        CompressionFormat.Xz,
        CompressionFormat.Zstd,
    };

    [Theory]
    [MemberData(nameof(RoundTripFormats))]
    public void CompressThenDecompress_RoundTripsByteArray(CompressionFormat format)
    {
        var payload = BuildPayload(256 * 1024);

        var compressed = CompressionService.Compress(payload, format);
        Assert.NotEmpty(compressed);

        var restored = CompressionService.Decompress(compressed, format);
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void Gzip_Detection_IdentifiesMagic()
    {
        var compressed = CompressionService.Compress("hello gzip"u8.ToArray(), CompressionFormat.Gzip);
        Assert.Equal(CompressionFormat.Gzip, CompressionFormats.Detect(compressed));
    }

    [Fact]
    public void Lzop_StoredBlock_Decompresses()
    {
        var payload = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x10, 0x20, 0xFF, 0x41, 0x42, 0x43, 0x7F, 0x80 };
        var lzop = BuildMinimalLzop(payload, compressedBlock: null);

        Assert.True(LzopCompressor.IsLzopFormat(lzop));
        Assert.Equal(payload, LzopCompressor.Decompress(lzop));
        Assert.Equal(payload, CompressionService.Decompress(lzop, CompressionFormat.Lzop));
        Assert.Equal(CompressionFormat.Lzop, CompressionFormats.Detect(lzop));
    }

    [Fact]
    public void Lzop_Lzo1xCompressedBlock_Decompresses()
    {
        // Hand-crafted LZO1X-1 stream: 4 literals + long match (distance 4, length 34) + EOF marker.
        // Decoded output = 38 × 'A'.
        var compressedBlock = new byte[]
        {
            0x15, // 17 + 4 literals
            (byte)'A', (byte)'A', (byte)'A', (byte)'A',
            0x20, 0x01, 0x0C, 0x00, // t=32, ext len byte 0x01 → length 34, offset field 0x000C → distance 4
            0x10, 0x01, 0x00, 0x00, // EOF match (distance 0)
        };

        var expected = Enumerable.Repeat((byte)'A', 38).ToArray();
        var lzop = BuildMinimalLzop(expected, compressedBlock);

        Assert.Equal(expected, LzopCompressor.Decompress(lzop));
        Assert.Equal(expected, CompressionService.Decompress(lzop, CompressionFormat.Lzop));
    }

    [Fact]
    public void Lzop_EmptyPayloadStoredBlock_Decompresses()
    {
        var lzop = BuildMinimalLzop([], compressedBlock: null);
        Assert.Empty(LzopCompressor.Decompress(lzop));
    }

    private static byte[] BuildPayload(int length)
    {
        var rng = new Random(0x5EED);
        var data = new byte[length];
        rng.NextBytes(data);
        // Add a strongly repetitive tail so every compressor has material to work with.
        var pattern = "the quick brown fox jumps over the lazy dog - 0123456789"u8.ToArray();
        for (var i = length / 2; i < length; i++)
        {
            data[i] = pattern[i % pattern.Length];
        }

        return data;
    }

    /// <summary>
    /// Builds a minimal valid lzop file (version 0x0900, no checksums, no filter):
    /// magic + header + one block (stored or LZO1X-compressed) + zero-length end marker.
    /// <para>构造最小有效 lzop 文件（版本 0x0900、无校验和、无过滤）：魔数 + 头 + 一个数据块
    /// （存储块或 LZO1X 压缩块）+ 零长度结束标记。</para>
    /// </summary>
    private static byte[] BuildMinimalLzop(byte[] payload, byte[]? compressedBlock)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A]); // magic

        var header = new MemoryStream();
        WriteUInt16BE(header, 0x0900); // version
        WriteUInt16BE(header, 0x0100); // lib_version
        header.WriteByte(1);           // method: LZO1X_1
        WriteUInt32BE(header, 0);      // flags: none
        WriteUInt32BE(header, 0);      // mode
        WriteUInt32BE(header, 0);      // mtime_low
        header.WriteByte(0);           // filename length

        var headerBytes = header.ToArray();
        ms.Write(headerBytes);

        var headerChecksum = Adler32(headerBytes);
        WriteUInt32BE(ms, headerChecksum);

        // One block.
        if (compressedBlock is null)
        {
            WriteUInt32BE(ms, (uint)payload.Length); // uncompressed size
            WriteUInt32BE(ms, (uint)payload.Length); // compressed size (stored block)
            ms.Write(payload);
        }
        else
        {
            WriteUInt32BE(ms, (uint)payload.Length);           // uncompressed size
            WriteUInt32BE(ms, (uint)compressedBlock.Length);   // compressed size
            ms.Write(compressedBlock);
        }

        // End marker.
        WriteUInt32BE(ms, 0);

        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint mod = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % mod;
            b = (b + a) % mod;
        }

        return (b << 16) | a;
    }

    private static void WriteUInt16BE(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32BE(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
