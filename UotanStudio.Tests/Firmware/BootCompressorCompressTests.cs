using UotanStudio.Compress;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// BootCompressor compression-side tests: gzip and lz4_legacy round trips, magic re-detection of produced streams,
/// rejection of labels that have no boot-family encoding, the library-routed decompression branches for gzip,
/// bzip2, XZ, Brotli and LZ4 frame containers, and the buffer pass-through when no magic is detected.
/// <para>BootCompressor 压缩侧测试：gzip 与 lz4_legacy 往返、产出流的魔数复检、拒绝没有 boot 家族合法编码的标签、
/// gzip、bzip2、XZ、Brotli 与 LZ4 帧容器经由压缩库的解压分支，以及未检出魔数时的缓冲区透传。</para>
/// </summary>
public class BootCompressorCompressTests
{
    private static readonly byte[] Payload = BuildCompressiblePayload(128 * 1024);

    public static TheoryData<CompressionType, CompressionFormat> FacadeDecodedContainers => new()
    {
        (CompressionType.Gzip, CompressionFormat.Gzip),
        (CompressionType.Bzip2, CompressionFormat.Bzip2),
        (CompressionType.Xz, CompressionFormat.Xz),
        (CompressionType.Brotli, CompressionFormat.Brotli),
        (CompressionType.Lz4, CompressionFormat.Lz4),
    };

    [Fact]
    public void Compress_Gzip_ProducesDetectableStreamAndRoundTrips()
    {
        var compressed = BootCompressor.Compress(Payload, CompressionType.Gzip);

        Assert.Equal(CompressionType.Gzip, BootCompressor.Detect(compressed));
        Assert.Equal(Payload, BootCompressor.TryDecompress(compressed, CompressionType.Gzip));
        Assert.Equal(Payload, BootCompressor.DecompressOrReturn(compressed));
    }

    [Fact]
    public void Compress_Lz4Legacy_ProducesDetectableStreamAndRoundTrips()
    {
        var compressed = BootCompressor.Compress(Payload, CompressionType.Lz4Legacy);

        Assert.Equal(CompressionType.Lz4Legacy, BootCompressor.Detect(compressed));
        Assert.Equal(Payload, BootCompressor.TryDecompress(compressed, CompressionType.Lz4Legacy));
        Assert.Equal(Payload, BootCompressor.DecompressOrReturn(compressed));
    }

    [Fact]
    public void Compress_Lz4Lg_EncodesAsLegacyFrame()
    {
        var compressed = BootCompressor.Compress(Payload, CompressionType.Lz4Lg);

        Assert.Equal(CompressionType.Lz4Legacy, BootCompressor.Detect(compressed));
        Assert.Equal(Payload, BootCompressor.TryDecompress(compressed, CompressionType.Lz4Legacy));
    }

    [Fact]
    public void Compress_Zopfli_EncodesAsGzipFrame()
    {
        var compressed = BootCompressor.Compress(Payload, CompressionType.Zopfli);

        Assert.Equal(CompressionType.Gzip, BootCompressor.Detect(compressed));
        Assert.Equal(Payload, BootCompressor.TryDecompress(compressed, CompressionType.Gzip));
    }

    [Fact]
    public void Compress_EmptyPayloadGzip_ProducesEmptyStreamDetectedAsNone()
    {
        var compressed = BootCompressor.Compress([], CompressionType.Gzip);

        Assert.Empty(compressed);
        Assert.Equal(CompressionType.None, BootCompressor.Detect(compressed));
        Assert.Null(BootCompressor.TryDecompress(compressed, CompressionType.Gzip));
    }

    [Fact]
    public void Compress_EmptyPayload_Lz4LegacyProducesMagicOnlyFrame()
    {
        var compressed = BootCompressor.Compress([], CompressionType.Lz4Legacy);

        Assert.Equal([0x02, 0x21, 0x4C, 0x18], compressed);
        Assert.Equal(CompressionType.Lz4Legacy, BootCompressor.Detect(compressed));
        Assert.Empty(BootCompressor.TryDecompress(compressed, CompressionType.Lz4Legacy)!);
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Lz4)]
    [InlineData(CompressionType.Lzma)]
    [InlineData(CompressionType.Xz)]
    [InlineData(CompressionType.Bzip2)]
    [InlineData(CompressionType.Lzop)]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Brotli)]
    public void TryCompress_WithoutBootApplicationEncoding_ReturnsNull(CompressionType type)
    {
        Assert.Null(BootCompressor.TryCompress(Payload, type));
    }

    [Fact]
    public void TryCompress_NullPayload_ReturnsNull()
    {
        Assert.Null(BootCompressor.TryCompress(null!, CompressionType.Gzip));
    }

    [Fact]
    public void TryCompress_SpanOverload_RoundTripsGzip()
    {
        Assert.True(BootCompressor.TryCompress(Payload.AsSpan(), CompressionType.Gzip, out var compressed));
        Assert.NotNull(compressed);
        Assert.Equal(Payload, BootCompressor.TryDecompress(compressed, CompressionType.Gzip));
    }

    [Fact]
    public void TryCompress_SpanOverload_UnsupportedLabel_ReturnsFalseAndNull()
    {
        Assert.False(BootCompressor.TryCompress(Payload.AsSpan(), CompressionType.Xz, out var compressed));
        Assert.Null(compressed);
    }

    [Fact]
    public void Compress_WithoutBootApplicationEncoding_Throws()
    {
        Assert.Throws<NotSupportedException>(() => BootCompressor.Compress(Payload, CompressionType.Xz));
    }

    [Theory]
    [MemberData(nameof(FacadeDecodedContainers))]
    public void TryDecompress_LibraryEncodedContainer_RoundTripsThroughFacade(CompressionType type, CompressionFormat format)
    {
        var compressed = CompressionService.Compress(Payload, format);

        Assert.Equal(Payload, BootCompressor.TryDecompress(compressed, type));
    }

    [Theory]
    [MemberData(nameof(FacadeDecodedContainers))]
    public void TryDecompress_CorruptedContainerHeader_ReturnsNull(CompressionType type, CompressionFormat format)
    {
        var compressed = CompressionService.Compress(Payload, format);

        Assert.Null(BootCompressor.TryDecompress(DamageContainerHeader(compressed), type));
    }

    [Fact]
    public void TryDecompress_ConcatenatedBzip2Streams_DecodesEveryStream()
    {
        var head = Payload[..(Payload.Length / 2)];
        var tail = Payload[(Payload.Length / 2)..];
        byte[] concat = [.. CompressionService.Compress(head, CompressionFormat.Bzip2), .. CompressionService.Compress(tail, CompressionFormat.Bzip2)];

        Assert.Equal(Payload, BootCompressor.TryDecompress(concat, CompressionType.Bzip2));
    }

    [Fact]
    public void TryDecompress_NoneLabel_YieldsEmptyArrayInsteadOfInputBytes()
    {
        byte[] data = [0x55, 0xAA, 0x55, 0xAA, 0x5A, 0xA5];

        Assert.Equal(CompressionType.None, BootCompressor.Detect(data));
        Assert.Empty(BootCompressor.TryDecompress(data, CompressionType.None)!);
    }

    [Fact]
    public void DecompressOrReturn_BufferWithoutMagic_ReturnsSameArrayInstance()
    {
        byte[] data = [0x55, 0xAA, 0x55, 0xAA, 0x5A, 0xA5];

        Assert.Equal(CompressionType.None, BootCompressor.Detect(data));
        Assert.Same(data, BootCompressor.DecompressOrReturn(data));
    }

    private static byte[] DamageContainerHeader(byte[] payload)
    {
        var damaged = new byte[payload.Length];
        damaged[0] = payload[0];
        damaged[1] = payload[1];
        return damaged;
    }

    private static byte[] BuildCompressiblePayload(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        return data;
    }
}