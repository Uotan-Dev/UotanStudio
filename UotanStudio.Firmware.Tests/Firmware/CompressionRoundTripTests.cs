using System.Buffers.Binary;
using System.IO.Hashing;
using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;
using UotanStudio.Firmware.BoundedContexts.Compression.Detection;
using UotanStudio.Firmware.BoundedContexts.Compression.Files;
using UotanStudio.Firmware.SharedKernel.Streams;
using UotanStudio.Tests.Comm.TestDoubles;
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
        Assert.Equal(CompressionFormat.Gzip, CompressionDetector.Detect(compressed));
    }

    [Fact]
    public void CompressFile_WithProgress_ReportsMonotonicBytesUpToSourceLength()
    {
        var payload = BuildPayload(256 * 1024);
        using var temp = new TempDirectory();
        var inputPath = temp.WriteBytes("input.bin", payload);
        var outputPath = Path.Combine(temp.Path, "output.br");

        var progress = new SyncProgress<(long Processed, long Total)>();
        CompressionFile.CompressFile(inputPath, outputPath, CompressionFormat.Brotli,
            progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(outputPath));
        var reports = progress.Reports;
        Assert.NotEmpty(reports);
        Assert.Equal(payload.Length, reports[^1].Total);
        Assert.Equal(payload.Length, reports[^1].Processed);
        for (var i = 1; i < reports.Count; i++)
            Assert.True(reports[i].Processed >= reports[i - 1].Processed, "Progress must not decrease.");
    }

    [Fact]
    public void Lzop_StoredBlock_Decompresses()
    {
        var payload = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x10, 0x20, 0xFF, 0x41, 0x42, 0x43, 0x7F, 0x80 };
        var lzop = BuildMinimalLzop(payload, compressedBlock: null);

        Assert.True(LzopCompressor.IsLzopFormat(lzop));
        Assert.Equal(payload, LzopCompressor.Decompress(lzop));
        Assert.Equal(payload, CompressionService.Decompress(lzop, CompressionFormat.Lzop));
        Assert.Equal(CompressionFormat.Lzop, CompressionDetector.Detect(lzop));
    }

    [Fact]
    public void Lzop_StoredBlockWithAllChecksumFlags_Decompresses()
    {
        var payload = BuildPayload(1024);
        const uint flags = 0x00000001 | 0x00000002 | 0x00010000 | 0x00020000;
        var lzop = BuildMinimalLzop(payload, compressedBlock: null, flags);

        Assert.Equal(payload, LzopCompressor.Decompress(lzop));
    }

    [Fact]
    public void Lzop_Lzo1xCompressedBlock_Decompresses()
    {
        var compressedBlock = new byte[]
        {
            0x15,
            (byte)'A', (byte)'A', (byte)'A', (byte)'A',
            0x20, 0x01, 0x0C, 0x00,
            0x10, 0x01, 0x00, 0x00,
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

    [Fact]
    public void Lzop_StoredBlockLargerThan256KiB_Decompresses()
    {
        var payload = BuildPayload(300 * 1024);
        var lzop = BuildMinimalLzop(payload, compressedBlock: null);

        Assert.Equal(payload, LzopCompressor.Decompress(lzop));
    }

    [Theory]
    [InlineData(64U * 1024 * 1024 + 1, 1U)]
    [InlineData(64U * 1024 * 1024 + 1, 64U * 1024 * 1024 + 1)]
    public void Lzop_BlockBeyondSupportedCeilingIsRejectedBeforeAllocation(uint uncompressedSize, uint compressedSize)
    {
        var lzop = BuildMinimalLzop([0], compressedBlock: null);
        BinaryPrimitives.WriteUInt32BigEndian(lzop.AsSpan(31, 4), uncompressedSize);
        BinaryPrimitives.WriteUInt32BigEndian(lzop.AsSpan(35, 4), compressedSize);

        var exception = Assert.Throws<CompressionException>(() => LzopCompressor.Decompress(lzop));
        Assert.Contains("supported maximum", exception.Message);
    }

    [Theory]
    [InlineData(1025U, 1U)]
    [InlineData(1025U, 1025U)]
    public void Lzop_BlockDeclarationsExceedingOutputLimitAreRejectedBeforeAllocation(uint uncompressedSize, uint compressedSize)
    {
        var lzop = BuildMinimalLzop([0], compressedBlock: null);
        BinaryPrimitives.WriteUInt32BigEndian(lzop.AsSpan(31, 4), uncompressedSize);
        BinaryPrimitives.WriteUInt32BigEndian(lzop.AsSpan(35, 4), compressedSize);

        var exception = Assert.Throws<CompressionException>(() => LzopCompressor.Decompress(lzop, 1024));
        Assert.Contains("output limit", exception.Message);
    }

    [Fact]
    public void BoundedStreams_RestrictRangeAndOutput()
    {
        using var source = new MemoryStream([0, 1, 2, 3, 4, 5]);
        using var bounded = new BoundedReadStream(source, 2, 3, leaveOpen: true);
        using var destination = new MemoryStream();
        StreamCopy.CopyExactly(bounded, destination, 3);
        Assert.Equal(new byte[] { 2, 3, 4 }, destination.ToArray());
        Assert.Equal(3, bounded.Position);

        using var limited = new CountingWriteStream(Stream.Null, 2);
        limited.Write([1, 2]);
        Assert.Throws<IOException>(() => limited.WriteByte(3));
    }

    [Fact]
    public void Decompress_OutputLimitIsEnforced()
    {
        var payload = BuildPayload(4096);
        var compressed = CompressionService.Compress(payload, CompressionFormat.Gzip);
        using var input = new MemoryStream(compressed);
        using var output = new MemoryStream();

        Assert.ThrowsAny<Exception>(() => CompressionService.Decompress(input, output, CompressionFormat.Gzip, 1024, TestContext.Current.CancellationToken));
        Assert.True(output.Length <= 1024);
    }

    [Fact]
    public void Lz4Legacy_RejectsOversizedAndTruncatedBlockLength()
    {
        byte[] oversized = [0x02, 0x21, 0x4C, 0x18, 0xFF, 0xFF, 0xFF, 0x7F];
        byte[] truncated = [0x02, 0x21, 0x4C, 0x18, 0x01, 0x02];

        Assert.Throws<CompressionException>(() => Lz4Compressor.DecompressLegacy(oversized));
        Assert.Throws<CompressionException>(() => Lz4Compressor.DecompressLegacy(truncated));
    }

    private static byte[] BuildPayload(int length)
    {
        var rng = new Random(0x5EED);
        var data = new byte[length];
        rng.NextBytes(data);
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
    private static byte[] BuildMinimalLzop(byte[] payload, byte[]? compressedBlock, uint flags = 0)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new MemoryStream();
        WriteUInt16BE(header, 0x0900);
        WriteUInt16BE(header, 0x0100);
        header.WriteByte(1);
        WriteUInt32BE(header, flags);
        WriteUInt32BE(header, 0);
        WriteUInt32BE(header, 0);
        header.WriteByte(0);

        var headerBytes = header.ToArray();
        ms.Write(headerBytes);

        var headerChecksum = Adler32(headerBytes);
        WriteUInt32BE(ms, headerChecksum);

        var block = compressedBlock ?? payload;
        WriteUInt32BE(ms, (uint)payload.Length);
        WriteUInt32BE(ms, (uint)block.Length);
        if ((flags & 0x00000001) != 0) WriteUInt32BE(ms, Adler32(payload));
        if ((flags & 0x00010000) != 0) WriteUInt32BE(ms, Crc32.HashToUInt32(payload));
        if ((flags & 0x00000002) != 0) WriteUInt32BE(ms, Adler32(block));
        if ((flags & 0x00020000) != 0) WriteUInt32BE(ms, Crc32.HashToUInt32(block));
        ms.Write(block);

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