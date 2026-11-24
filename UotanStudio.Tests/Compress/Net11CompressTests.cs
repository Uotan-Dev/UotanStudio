using System.Buffers;
using System.IO.Compression;
using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Lzma;
using UotanStudio.Compress.Codecs.Zstd;
using UotanStudio.Compress.Internal;
using UotanStudio.Compress.Zip;
using Xunit;

namespace UotanStudio.Tests.Compress;

public sealed class Net11CompressTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(131071)]
    [InlineData(131072)]
    [InlineData(131073)]
    [InlineData(196609)]
    public async Task Encode_WindowBoundary_IsByteStableAndDecodesWithExistingDecoder(int length)
    {
        var data = BuildDeterministic(length);

        var sequential = Lzma2Encoder.Encode(data, maxDegreeOfParallelism: 1);
        var parallel = Lzma2Encoder.Encode(data, maxDegreeOfParallelism: 2);

        Assert.Equal(sequential, parallel);
        Assert.Equal(0, parallel[^1]);
        using var input = new MemoryStream(parallel, writable: false);
        using var decoder = new LzmaDecoderStream([0], input);
        using var output = new MemoryStream();
        await decoder.CopyToAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void Decompress_ExactDeclaredSize_ReturnsPayloadAndRejectsMismatch()
    {
        var source = BuildDeterministic(4096);
        var frame = CompressFrame(source);

        var exact = ZstdBlockCodec.Decompress(frame, source.Length);

        Assert.Equal(source, exact);
        Assert.Throws<CompressionException>(() => ZstdBlockCodec.Decompress(frame, source.Length - 1));
        Assert.Throws<CompressionException>(() => ZstdBlockCodec.Decompress(frame, source.Length + 1));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(100_000)]
    public void DecompressAuto_UnknownContentSize_ReturnsExactPayload(int length)
    {
        var source = BuildDeterministic(length);
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(source.Length)];
        using var encoder = new ZstandardEncoder();

        var status = encoder.Compress(source, frame, out var consumed, out var written, isFinalBlock: true);

        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(source.Length, consumed);
        var unknownSizeFrame = BuildUnknownSizeFrame(frame.AsSpan(0, written));
        Assert.True(ZstandardDecoder.TryGetMaxDecompressedLength(unknownSizeFrame, out var windowBound));
        Assert.Equal(128 * 1024, windowBound);
        var decoded = ZstdBlockCodec.DecompressAuto(unknownSizeFrame);
        Assert.Equal(source, decoded);
    }

    [Fact]
    public void DecompressAuto_UnknownContentSizeNonSingleSegment_ReturnsExactPayload()
    {
        var source = BuildDeterministic(1024 * 1024);
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(source.Length)];
        using var encoder = new ZstandardEncoder();

        var status = encoder.Compress(source, frame, out var consumed, out var written, isFinalBlock: true);

        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(source.Length, consumed);
        var unknownSizeFrame = BuildUnknownSizeFrame(frame.AsSpan(0, written));
        Assert.Equal(0, unknownSizeFrame[4] & 0x20);
        Assert.True(ZstandardDecoder.TryGetMaxDecompressedLength(unknownSizeFrame, out var windowBound));
        Assert.True(windowBound > 0);
        Assert.Equal(source, ZstdBlockCodec.DecompressAuto(unknownSizeFrame));
    }

    [Fact]
    public void DecompressAuto_ConcatenatedFrames_ReturnsCombinedPayload()
    {
        var first = BuildDeterministic(4096);
        var second = BuildDeterministic(70_000);
        var firstFrame = CompressFrame(first);
        var secondFrame = CompressFrame(second);
        var frames = new byte[firstFrame.Length + secondFrame.Length];
        firstFrame.CopyTo(frames, 0);
        secondFrame.CopyTo(frames, firstFrame.Length);
        var expected = new byte[first.Length + second.Length];
        first.CopyTo(expected, 0);
        second.CopyTo(expected, first.Length);

        var decoded = ZstdBlockCodec.DecompressAuto(frames);

        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void DecompressAuto_CorruptMagic_ThrowsCompressionException()
    {
        var frame = CompressFrame(BuildDeterministic(4096));
        frame[0] ^= 0xFF;

        Assert.Throws<CompressionException>(() => ZstdBlockCodec.DecompressAuto(frame));
    }

    [Fact]
    public void DecompressAuto_TruncatedFrame_ThrowsCompressionException()
    {
        var frame = CompressFrame(BuildDeterministic(4096));

        Assert.Throws<CompressionException>(() => ZstdBlockCodec.DecompressAuto(frame.AsSpan(0, frame.Length - 1)));
    }

    [Fact]
    public void DecompressAuto_TrailingIncompleteFrame_ThrowsInsteadOfReturningPrefix()
    {
        var frame = CompressFrame(BuildDeterministic(4096));
        var damaged = new byte[frame.Length + 2];
        frame.CopyTo(damaged, 0);
        damaged[^2] = 0x28;
        damaged[^1] = 0xB5;

        Assert.Throws<CompressionException>(() => ZstdBlockCodec.DecompressAuto(damaged));
    }

    [Theory]
    [InlineData("boot.img", "boot.img")]
    [InlineData("images/system.img", "images/system.img")]
    [InlineData("images\\system.img", "images/system.img")]
    [InlineData(".hidden", ".hidden")]
    [InlineData("images/.../system.img", "images/.../system.img")]
    [InlineData("images/a..b", "images/a..b")]
    public void NormalizeEntryPath_ValidRelativeName_ReturnsNormalizedPath(string entryName, string expected)
    {
        var normalized = ZipContainer.NormalizeEntryPath(entryName);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("//boot.img")]
    [InlineData("\\boot.img")]
    [InlineData("images//system.img")]
    [InlineData("images/")]
    [InlineData("images\\")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("./boot.img")]
    [InlineData("../boot.img")]
    [InlineData("images/./boot.img")]
    [InlineData("images/../boot.img")]
    [InlineData("C:/boot.img")]
    [InlineData("C:\\boot.img")]
    [InlineData("images/C:boot.img")]
    public void NormalizeEntryPath_InvalidSegmentOrDrive_ThrowsArgumentException(string entryName)
    {
        Assert.Throws<ArgumentException>(() => ZipContainer.NormalizeEntryPath(entryName));
    }

    [Fact]
    public async Task ReadAsyncAndWriteAsync_MemoryOverloads_ForwardDataAndCancellation()
    {
        byte[] initial = [1, 2, 3, 4, 5, 6];
        using var inner = new RecordingStream(initial);
        using var wrapper = new NonDisposingStream(inner);
        inner.Position = 1;
        byte[] readBuffer = [99, 99, 99, 99];
        byte[] writeBuffer = [99, 8, 7, 99];
        using var writeCts = new CancellationTokenSource();

        var read = await wrapper.ReadAsync(readBuffer.AsMemory(1, 2), TestContext.Current.CancellationToken);
        await wrapper.WriteAsync(writeBuffer.AsMemory(1, 2), writeCts.Token);

        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 99, 2, 3, 99 }, readBuffer);
        Assert.Equal(1, inner.MemoryReadCount);
        Assert.Equal(TestContext.Current.CancellationToken, inner.LastReadToken);
        Assert.Equal(1, inner.MemoryWriteCount);
        Assert.Equal(writeCts.Token, inner.LastWriteToken);
        Assert.Equal(new byte[] { 1, 2, 3, 8, 7, 6 }, inner.ToArray());
    }

    [Fact]
    public async Task FlushAsync_ForwardsCancellation()
    {
        using var inner = new RecordingStream();
        using var wrapper = new NonDisposingStream(inner);
        using var cts = new CancellationTokenSource();

        await wrapper.FlushAsync(cts.Token);

        Assert.Equal(1, inner.FlushCount);
        Assert.Equal(cts.Token, inner.LastFlushToken);
    }

    [Fact]
    public void Dispose_DoesNotDisposeInnerStream()
    {
        using var inner = new RecordingStream();
        using var wrapper = new NonDisposingStream(inner);

        wrapper.Dispose();

        Assert.Equal(0, inner.DisposeCount);
        Assert.True(inner.CanRead);
        Assert.True(inner.CanWrite);
        inner.WriteByte(42);
        Assert.Equal(new byte[] { 42 }, inner.ToArray());
    }

    private static byte[] CompressFrame(byte[] source)
    {
        var frame = new byte[ZstdBlockCodec.MaximumCompressedSize(source.Length)];
        var written = ZstdBlockCodec.Compress(source, frame);
        return frame.AsSpan(0, written).ToArray();
    }

    private static byte[] BuildDeterministic(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 149 + (i >> 7) * 31 + 11) & 0xFF);
        }

        return data;
    }

    private static byte[] BuildUnknownSizeFrame(ReadOnlySpan<byte> frame)
    {
        var descriptor = frame[4];
        var singleSegment = (descriptor & 0x20) != 0;
        var dictionaryBytes = (descriptor & 0x03) switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            _ => 4
        };
        var contentBytes = (descriptor >> 6) switch
        {
            0 => singleSegment ? 1 : 0,
            1 => 2,
            2 => 4,
            _ => 8
        };
        var middleBytes = 5 + dictionaryBytes + (singleSegment ? 0 : 1);
        var insertedWindowBytes = singleSegment ? 1 : 0;
        var result = new byte[frame.Length - contentBytes + insertedWindowBytes];
        frame[..4].CopyTo(result);
        result[4] = (byte)((descriptor & 0x3F) & ~0x20);
        frame.Slice(5, middleBytes - 5).CopyTo(result.AsSpan(5));
        if (singleSegment)
        {
            result[middleBytes] = 0x38;
        }

        frame[(middleBytes + contentBytes)..].CopyTo(result.AsSpan(middleBytes + insertedWindowBytes));
        return result;
    }

    private sealed class RecordingStream : MemoryStream
    {
        public RecordingStream()
        {
        }

        public RecordingStream(byte[] buffer)
            : base(buffer)
        {
        }

        public int MemoryReadCount { get; private set; }
        public int MemoryWriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public int DisposeCount { get; private set; }
        public CancellationToken LastReadToken { get; private set; }
        public CancellationToken LastWriteToken { get; private set; }
        public CancellationToken LastFlushToken { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MemoryReadCount++;
            LastReadToken = cancellationToken;
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MemoryWriteCount++;
            LastWriteToken = cancellationToken;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            LastFlushToken = cancellationToken;
            return base.FlushAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}