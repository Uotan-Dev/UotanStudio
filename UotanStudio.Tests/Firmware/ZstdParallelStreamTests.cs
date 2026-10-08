using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Compression.Compressors;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the chunked zstd stream path: a pooled encoder context per parallel slot must reproduce the
/// fresh-context reference bytes, the degree must not change the container, and a non-seekable source must
/// keep falling back to the single streaming frame.
/// <para>守护分块 zstd 流式路径：每个并行槽复用编码器上下文时必须复现新建上下文的参照字节，并行度不得改变容器，
/// 不可寻址源必须继续回退到单帧流式路径。</para>
/// </summary>
public class ZstdParallelStreamTests
{
    private const int ChunkSize = 1 << 20;
    private const int Level = 3;

    [Fact]
    public void Compress_StreamWithWorkers_MatchesFreshContextByteArrayOracle()
    {
        var payload = BuildPayload(3 * ChunkSize + 1024);
        var oracle = CompressionService.Compress(payload, CompressionFormat.Zstd,
            new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });

        var compressed = CompressStream(payload, new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });

        Assert.Equal(oracle, compressed);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Compress_StreamDegrees_ProduceIdenticalBytes(int degree)
    {
        var payload = BuildPayload(5 * ChunkSize);

        var reference = CompressStream(payload, new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });
        var candidate = CompressStream(payload, new CompressionOptions { Level = Level, MaxDegreeOfParallelism = degree });

        Assert.Equal(reference, candidate);
    }

    [Fact]
    public void Compress_UnseekableStream_MatchesSequentialSingleFrameResult()
    {
        var payload = BuildPayload(4 * ChunkSize);
        using var sequential = new MemoryStream();
        ZstdCompressor.Compress(new MemoryStream(payload), sequential,
            new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });

        using var chunked = new MemoryStream();
        ZstdCompressor.Compress(new NonSeekableStream(payload), chunked,
            new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });

        using var single = new MemoryStream();
        ZstdCompressor.Compress(new MemoryStream(payload), single, new CompressionOptions { Level = Level });

        Assert.NotEqual(sequential.ToArray(), chunked.ToArray());
        Assert.Equal(single.ToArray(), chunked.ToArray());
    }

    [Fact]
    public void Compress_SmallStreamWithWorkers_UsesSingleFramePath()
    {
        var payload = BuildPayload(ChunkSize - 1);

        var chunked = CompressStream(payload, new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });
        var single = CompressStream(payload, new CompressionOptions { Level = Level });

        Assert.Equal(single, chunked);
        Assert.Equal(1, CountFrames(chunked));
    }

    [Fact]
    public void Compress_StreamWithWorkers_DecodesBackToPlaintext()
    {
        var payload = BuildPayload(3 * ChunkSize + 4096);

        var compressed = CompressStream(payload, new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });

        Assert.Equal(4, CountFrames(compressed));
        using var source = new MemoryStream(compressed);
        using var restored = new MemoryStream();
        ZstdCompressor.Decompress(source, restored);
        Assert.Equal(payload, restored.ToArray());
    }

    [Fact]
    public void Compress_StreamWithWorkers_HonoursStartOffset()
    {
        var payload = BuildPayload(2 * ChunkSize);
        using var stream = new MemoryStream();
        stream.Write("padding"u8.ToArray());
        stream.Write(payload);
        stream.Position = 7;

        var compressed = CompressStreamAt(stream, new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });
        var expected = CompressStream(payload, new CompressionOptions { Level = Level, MaxDegreeOfParallelism = 8 });

        using var source = new MemoryStream(compressed);
        using var restored = new MemoryStream();
        ZstdCompressor.Decompress(source, restored);
        Assert.Equal(payload, restored.ToArray());
        Assert.Equal(expected, compressed);
    }

    private static byte[] CompressStream(byte[] payload, CompressionOptions options)
    {
        using var input = new MemoryStream(payload, writable: false);
        return CompressStreamAt(input, options);
    }

    private static byte[] CompressStreamAt(Stream input, CompressionOptions options)
    {
        using var output = new MemoryStream();
        ZstdCompressor.Compress(input, output, options);
        return output.ToArray();
    }

    private static int CountFrames(byte[] data)
    {
        Span<byte> magic = [0x28, 0xB5, 0x2F, 0xFD];
        var count = 0;
        for (var i = 0; i + magic.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, magic.Length).SequenceEqual(magic)) count++;
        }

        return count;
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

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var available = Math.Min(buffer.Length, data.Length - _position);
            if (available <= 0) return 0;
            data.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}