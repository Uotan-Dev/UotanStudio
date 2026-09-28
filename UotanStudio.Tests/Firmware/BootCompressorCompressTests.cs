using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// BootCompressor compression-side tests: gzip and lz4_legacy round trips, magic re-detection of produced streams,
/// and rejection of labels that have no boot-family encoding.
/// <para>BootCompressor 压缩侧测试：gzip 与 lz4_legacy 往返、产出流的魔数复检，
/// 以及拒绝没有 boot 家族合法编码的标签。</para>
/// </summary>
public class BootCompressorCompressTests
{
    private static readonly byte[] Payload = BuildCompressiblePayload(128 * 1024);

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