using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Lz4;
using UotanStudio.Compress.Codecs.Zstd;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Contract tests for the span-based LZ4 and Zstd block codecs the filesystem and OTA-delta formats consume:
/// the level anchors those formats translate onto, the partial-decode prefix a truncated LZ4DIFF source still
/// yields, and the window-log ceiling that makes an over-large zstd frame fail instead of allocate.
/// <para>面向文件系统与 OTA 增量格式所消费的 span 级 LZ4/Zstd 块编解码器的契约测试：格式级别所翻译到的锚点、
/// 被截断的 LZ4DIFF 源仍能给出的部分解压前缀，以及让过大 zstd 帧失败而不是分配窗口内存的窗口上限。</para>
/// </summary>
public class CompressionBlockCodecTests
{
    private const int ClusterSize = 128 * 1024;

    public static TheoryData<Lz4BlockLevel> BlockLevels => new()
    {
        Lz4BlockLevel.Fast,
        Lz4BlockLevel.High,
        Lz4BlockLevel.VeryHigh,
        Lz4BlockLevel.Ultra,
        Lz4BlockLevel.Maximum,
    };

    [Theory]
    [MemberData(nameof(BlockLevels))]
    public void Lz4BlockLevel_NamedAnchors_CarryTheSearchOrdinalsTheEncoderExpects(Lz4BlockLevel level)
    {
        var expected = level switch
        {
            Lz4BlockLevel.Fast => 0,
            Lz4BlockLevel.High => 3,
            Lz4BlockLevel.VeryHigh => 6,
            Lz4BlockLevel.Ultra => 9,
            Lz4BlockLevel.Maximum => 12,
            _ => -1,
        };

        Assert.Equal(expected, (int)level);
    }

    [Theory]
    [MemberData(nameof(BlockLevels))]
    public void Lz4BlockCodec_EncodeThenDecode_EveryAnchorRoundTripsTheBlock(Lz4BlockLevel level)
    {
        var payload = BuildRepeatingPayload(ClusterSize);
        var block = new byte[Lz4BlockCodec.MaximumOutputSize(payload.Length)];

        var written = Lz4BlockCodec.Encode(payload, block, (int)level);
        Assert.InRange(written, 1, block.Length);

        var decoded = new byte[payload.Length];
        Assert.Equal(payload.Length, Lz4BlockCodec.Decode(block.AsSpan(0, written), decoded));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Lz4BlockCodec_Decode_BlockLargerThanDestination_IsReportedAsNegative()
    {
        var payload = BuildRepeatingPayload(ClusterSize);
        var block = new byte[Lz4BlockCodec.MaximumOutputSize(payload.Length)];
        var written = Lz4BlockCodec.Encode(payload, block, (int)Lz4BlockLevel.Fast);

        Assert.True(Lz4BlockCodec.Decode(block.AsSpan(0, written), new byte[64]) < 0);
    }

    [Fact]
    public void Lz4BlockCodec_PartialDecode_TruncatedDestination_ReturnsTheDecodablePrefix()
    {
        var payload = BuildRepeatingPayload(ClusterSize);
        var block = new byte[Lz4BlockCodec.MaximumOutputSize(payload.Length)];
        var written = Lz4BlockCodec.Encode(payload, block, (int)Lz4BlockLevel.Fast);

        var prefix = new byte[payload.Length / 2];
        var produced = Lz4BlockCodec.PartialDecode(block.AsSpan(0, written), prefix);

        Assert.Equal(prefix.Length, produced);
        Assert.True(payload.AsSpan(0, prefix.Length).SequenceEqual(prefix), "Partial decode must produce the block prefix byte for byte.");
    }

    [Fact]
    public void Lz4BlockCodec_PartialDecode_CompleteDestination_MatchesFullDecode()
    {
        var payload = BuildRepeatingPayload(ClusterSize);
        var block = new byte[Lz4BlockCodec.MaximumOutputSize(payload.Length)];
        var written = Lz4BlockCodec.Encode(payload, block, (int)Lz4BlockLevel.High);

        var partial = new byte[payload.Length];
        var full = new byte[payload.Length];

        Assert.Equal(
            Lz4BlockCodec.Decode(block.AsSpan(0, written), full),
            Lz4BlockCodec.PartialDecode(block.AsSpan(0, written), partial));
        Assert.Equal(full, partial);
    }

    [Fact]
    public void Lz4BlockCodec_Encode_EmptySource_WritesNothing()
    {
        Assert.Equal(0, Lz4BlockCodec.Encode([], new byte[Lz4BlockCodec.MaximumOutputSize(0)], (int)Lz4BlockLevel.Fast));
    }

    [Fact]
    public void ZstdBlockCodec_CompressThenDecompress_SpanAndArrayPathsAgree()
    {
        var payload = BuildRepeatingPayload(ClusterSize);
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var written = ZstdBlockCodec.Compress(payload, frame);
        Assert.InRange(written, 1, frame.Length);

        var spanDecoded = new byte[payload.Length];
        Assert.Equal(payload.Length, ZstdBlockCodec.Decompress(frame.AsSpan(0, written), spanDecoded));

        var arrayDecoded = ZstdBlockCodec.Decompress(frame.AsSpan(0, written), payload.Length);
        Assert.Equal(payload, spanDecoded);
        Assert.Equal(spanDecoded, arrayDecoded);
    }

    [Fact]
    public void ZstdBlockCodec_Decompress_ArrayHintSmallerThanFrame_IsRejected()
    {
        var payload = BuildRepeatingPayload(ClusterSize);
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var written = ZstdBlockCodec.Compress(payload, frame);

        Assert.Throws<CompressionException>(() => ZstdBlockCodec.Decompress(frame.AsSpan(0, written), payload.Length / 2));
    }

    [Fact]
    public void ZstdBlockCodec_Decompress_WithWindowCeiling_RoundTripsIdenticalBytes()
    {
        var payload = BuildRepeatingPayload(256 * 1024);
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var written = ZstdBlockCodec.Compress(payload, frame, level: 19);
        Assert.InRange(written, 1, frame.Length);

        var unconstrained = new byte[payload.Length];
        var constrained = new byte[payload.Length];
        Assert.Equal(payload.Length, ZstdBlockCodec.Decompress(frame.AsSpan(0, written), unconstrained));
        Assert.Equal(payload.Length, ZstdBlockCodec.Decompress(frame.AsSpan(0, written), constrained, maximumWindowLog: 32));
        Assert.Equal(unconstrained, constrained);
    }

    [Fact]
    public void ZstdBlockCodec_Decompress_WindowCeilingBelowDeclaredWindow_FailsWithoutWritingTheDestination()
    {
        var payload = BuildRepeatingPayload(256 * 1024);
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var written = ZstdBlockCodec.Compress(payload, frame, level: 19);

        var destination = new byte[payload.Length];
        var exception = Assert.Throws<CompressionException>(
            () => ZstdBlockCodec.Decompress(frame.AsSpan(0, written), destination, maximumWindowLog: 12));

        Assert.True(destination.All(static value => value == 0),
            "A refused frame must fail on its declared window before any byte is decoded.");
        Assert.Contains("ceiling", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZstdBlockCodec_Decompress_ZeroCeiling_KeepsTheUnboundedPath()
    {
        var payload = BuildRepeatingPayload(256 * 1024);
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var written = ZstdBlockCodec.Compress(payload, frame, level: 19);

        Assert.Equal(payload.Length, ZstdBlockCodec.Decompress(frame.AsSpan(0, written), new byte[payload.Length], maximumWindowLog: 0));
    }

    [Fact]
    public void ZstdBlockCodec_Decompress_ArrayHintLargerThanFrame_IsRejected()
    {
        var payload = BuildRepeatingPayload(ClusterSize);
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var written = ZstdBlockCodec.Compress(payload, frame);

        var exception = Assert.Throws<CompressionException>(
            () => ZstdBlockCodec.Decompress(frame.AsSpan(0, written), payload.Length * 2));

        Assert.Contains("declared", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZstdBlockCodec_Compress_WindowedFrame_DiffersFromDefaultFrameForTheSamePayload()
    {
        var payload = BuildDistantlyRepeatedPayload(64 * 1024);
        var bounded = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var boundedLength = ZstdBlockCodec.Compress(payload, bounded, level: 19, windowLog: 12);
        var plain = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var plainLength = ZstdBlockCodec.Compress(payload, plain, level: 19);

        Assert.True(boundedLength > 0 && plainLength > 0);
        Assert.True(boundedLength > plainLength, $"A 4 KiB window must lose the 64 KiB cross-half match: {boundedLength} vs {plainLength}.");

        var decoded = new byte[payload.Length];
        Assert.Equal(payload.Length, ZstdBlockCodec.Decompress(bounded.AsSpan(0, boundedLength), decoded, maximumWindowLog: 12));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void ZstdBlockCodec_Compress_WindowedCallThenDefaultCall_WritesTheDefaultFrameAgain()
    {
        var payload = BuildDistantlyRepeatedPayload(64 * 1024);
        var first = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var firstLength = ZstdBlockCodec.Compress(payload, first, level: 19);
        var windowed = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var windowedLength = ZstdBlockCodec.Compress(payload, windowed, level: 19, windowLog: 12);
        Assert.True(windowedLength != firstLength, $"The window override must be observable on the same payload: {windowedLength} vs {firstLength}.");

        var again = new byte[ZstdBlockCodec.MaximumCompressedSize(payload.Length)];
        var againLength = ZstdBlockCodec.Compress(payload, again, level: 19);

        Assert.Equal(firstLength, againLength);
        Assert.True(first.AsSpan(0, firstLength).SequenceEqual(again.AsSpan(0, againLength)),
            "A window log left on the cached compressor would reappear in the next caller's frame.");
    }

    [Fact]
    public void ZstdBlockCodec_Compress_EmptySource_ProducesNoFrameAndNoFailure()
    {
        Assert.Equal(0, ZstdBlockCodec.Compress([], new byte[64]));
    }

    private static byte[] BuildRepeatingPayload(int length)
    {
        var payload = new byte[length];
        for (var offset = 0; offset < length; offset++)
        {
            payload[offset] = (byte)('a' + offset % 23);
        }

        return payload;
    }

    private static byte[] BuildDistantlyRepeatedPayload(int halfLength)
    {
        var first = new byte[halfLength];
        for (var offset = 0; offset < halfLength; offset++)
        {
            first[offset] = (byte)((offset * 31 + (offset >> 5)) % 251);
        }

        var payload = new byte[halfLength * 2];
        first.CopyTo(payload, 0);
        first.CopyTo(payload, halfLength);
        return payload;
    }
}