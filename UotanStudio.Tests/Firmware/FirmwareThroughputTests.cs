using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class FirmwareThroughputTests
{
    public static bool LargeDataEnabled => Environment.GetEnvironmentVariable("UOTAN_LARGE_DATA") == "1";

    [Theory(Skip = "Set UOTAN_LARGE_DATA=1 to build the 5 MB three-mode serial/parallel payload comparison.", SkipUnless = nameof(LargeDataEnabled))]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.BZip2)]
    [InlineData(CompressionType.Xz)]
    [Trait("Category", "LargeData")]
    public void ParallelBuild_PreservesOrderedPayloadAndExtractedHash(CompressionType compression)
    {
        using var temp = new TempDirectory();
        var bytes = CreateInput(5 * 1024 * 1024 + 4096);
        var source = temp.WriteBytes("input.img", bytes);
        var serial = Path.Combine(temp.Path, "serial.bin");
        var parallel = Path.Combine(temp.Path, "parallel.bin");
        using (var builder = new PayloadBuilder())
            builder.WithCompressionWorkers(1).AddPartitionCompressed("system", source, compression).Build(serial);
        using (var builder = new PayloadBuilder())
            builder.WithCompressionWorkers(4).AddPartitionCompressed("system", source, compression).Build(parallel);
        Assert.Equal(File.ReadAllBytes(serial), File.ReadAllBytes(parallel));
        PayloadParser.Extract(parallel, ["system"], temp.Path);
        using var restored = File.OpenRead(Path.Combine(temp.Path, "system.img"));
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(restored));
    }

    [Fact]
    public void Build_NonSeekableShortReads_PreservesTailAndMultiplePartitions()
    {
        using var temp = new TempDirectory();
        var first = CreateInput(2 * 1024 * 1024 + 4096);
        var second = CreateInput(8192);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder())
            builder.WithCompressionWorkers(4)
                .AddPartition("first", new ShortReadStream(first))
                .AddZeroPartition("zero", 4096)
                .AddPartition("second", new ShortReadStream(second)).Build(payload);
        PayloadParser.Extract(payload, ["first", "zero", "second"], temp.Path);
        Assert.Equal(first, File.ReadAllBytes(Path.Combine(temp.Path, "first.img")));
        Assert.Equal(second, File.ReadAllBytes(Path.Combine(temp.Path, "second.img")));
        Assert.Equal(new byte[4096], File.ReadAllBytes(Path.Combine(temp.Path, "zero.img")));
    }

    [Fact]
    public void Build_UnalignedNonSeekableTail_DoesNotPublishPartialResult()
    {
        using var temp = new TempDirectory();
        var payload = temp.WriteBytes("payload.bin", "existing"u8.ToArray());
        using var builder = new PayloadBuilder();
        builder.AddPartition("system", new ShortReadStream(CreateInput(1024 * 1024 + 1)));
        Assert.Throws<PayloadGenerationException>(() => builder.Build(payload));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(payload));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task Build_CancelDuringRead_PreservesDestinationAndCleansTemporaryFile()
    {
        using var temp = new TempDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var payload = temp.WriteBytes("payload.bin", "existing"u8.ToArray());
        using var input = new ShortReadStream(CreateInput(3 * 1024 * 1024), cancellation);
        using var builder = new PayloadBuilder();
        builder.WithCompressionWorkers(4).AddPartition("system", input);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(payload, cancellation.Token));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(payload));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void Build_RejectsUnboundedWorkerCount(int workers)
    {
        using var builder = new PayloadBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCompressionWorkers(workers));
    }

    [Fact]
    public void Bzip2_StreamCodecHandlesConcatenationShortReadsAndLeavesStreamsOpen()
    {
        var first = CreateInput(900001);
        var second = CreateInput(700001);
        using var compressed = new MemoryStream();
        using (var input = new ShortReadStream(first))
        {
            Bzip2Compressor.Compress(input, compressed);
            Assert.True(input.CanRead);
        }
        using (var input = new ShortReadStream(second))
            Bzip2Compressor.Compress(input, compressed);
        Assert.True(compressed.CanWrite);
        using var packed = new ShortReadStream(compressed.ToArray());
        using var restored = new MemoryStream();
        Bzip2Compressor.Decompress(packed, restored);
        Assert.True(packed.CanRead);
        Assert.True(restored.CanWrite);
        Assert.Equal(first.Concat(second), restored.ToArray());
    }

    [Fact]
    public void Bzip2_EmptyStreamProducesValidStream()
    {
        using var input = new MemoryStream();
        using var compressed = new MemoryStream();
        Bzip2Compressor.Compress(input, compressed);
        Assert.NotEmpty(compressed.ToArray());
        Assert.Empty(Bzip2Compressor.Decompress(compressed.ToArray()));
    }

    private static byte[] CreateInput(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)((i / 4096 + i % 17) % 251);
        return bytes;
    }

    private sealed class ShortReadStream(byte[] bytes, CancellationTokenSource? cancellation = null) : Stream
    {
        private readonly MemoryStream _input = new(bytes, writable: false);
        public override bool CanRead => _input.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = _input.Read(buffer[..Math.Min(buffer.Length, 4093)]);
            if (_input.Position >= 1024 * 1024) cancellation?.Cancel();
            return count;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _input.Dispose();
            base.Dispose(disposing);
        }
    }
}