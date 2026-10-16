using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Compress;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.IO;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Protects bounded codec read-ahead, digest verification and the small-read I/O regression.</summary>
public class PayloadReadPerformanceTests
{
    [Theory]
    [InlineData(CompressionFormat.Xz, InstallOperation.Types.Type.ReplaceXz)]
    [InlineData(CompressionFormat.Bzip2, InstallOperation.Types.Type.ReplaceBz)]
    public void CompressedReplace_UsesBlockReadsAndPreservesContents(CompressionFormat format, InstallOperation.Types.Type type)
    {
        var expected = new byte[1024 * 1024];
        new Random(1234).NextBytes(expected);
        var compressed = CompressionService.Compress(expected, format);
        using var input = new CountedReadStream(compressed);
        using var directory = new TempDirectory();
        using var output = new FileStream(Path.Combine(directory.Path, "partition.img"), FileMode.CreateNew, FileAccess.ReadWrite);
        var operation = new InstallOperation
        {
            Type = type,
            DataLength = (ulong)compressed.Length,
            DataSha256Hash = ByteString.CopyFrom(SHA256.HashData(compressed))
        };
        operation.DstExtents.Add(new Extent { NumBlocks = (ulong)expected.Length / 4096 });

        OperationProcessor.ProcessOperation(operation, output, input, 0, 4096, new object(), new object(), TestContext.Current.CancellationToken);

        output.Position = 0;
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(output));
        Assert.InRange(input.ReadCalls, 1, 128);
        Assert.InRange(input.MaximumRead, 1, 128 * 1024);
        Assert.True(input.CanRead);
    }

    [Fact]
    public void VerifyingReader_ReadAheadPreservesConsumerPositionAcrossShortReads()
    {
        var bytes = new byte[200_003]; new Random(5678).NextBytes(bytes);
        using var input = new CountedReadStream(bytes, maximumRead: 997);
        using var reader = new VerifyingReadStream(input, SHA256.HashData(bytes));
        var actual = new byte[bytes.Length];
        Assert.Equal(0, reader.Read(Span<byte>.Empty));
        Assert.Equal(0, input.ReadCalls);
        for (var offset = 0; offset < actual.Length;)
        {
            Assert.Equal(offset, reader.Position);
            if (offset % 3 == 0) actual[offset++] = checked((byte)reader.ReadByte());
            else offset += reader.Read(actual.AsSpan(offset, Math.Min(1111, actual.Length - offset)));
        }
        reader.Complete();
        Assert.Equal(bytes, actual);
        Assert.Equal(bytes.Length, reader.Position);
        Assert.Equal(-1, reader.ReadByte());
        Assert.InRange(input.ReadCalls, 1, 205);
    }

    [Fact]
    public void VerifyingReader_CompleteVerifiesUnreadTail()
    {
        var bytes = new byte[200_003]; new Random(123).NextBytes(bytes);
        var expected = SHA256.HashData(bytes);
        bytes[^1] ^= 1;
        using var input = new CountedReadStream(bytes);
        using var reader = new VerifyingReadStream(input, expected);
        Assert.Equal(bytes[0], reader.ReadByte());
        Assert.Equal(1, reader.Position);
        Assert.Throws<PartitionExtractionException>(reader.Complete);
    }

    [Fact]
    public void VerifyingReader_DisposeIsIdempotentAndLeavesSourceOpen()
    {
        using var input = new MemoryStream([1, 2, 3]);
        var reader = new VerifyingReadStream(input, null);
        reader.ReadByte(); reader.Dispose(); reader.Dispose();
        Assert.True(input.CanRead);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
    }

    [Fact]
    public async Task SharedTransformedStream_ExtractsConcurrentPartitionsWithoutCursorCorruption()
    {
        var data = new byte[8 * 256 * 1024]; new Random(9012).NextBytes(data);
        var manifest = new DeltaArchiveManifest { BlockSize = 4096 };
        for (var i = 0; i < 8; i++)
        {
            var hash = ByteString.CopyFrom(SHA256.HashData(data.AsSpan(i * 256 * 1024, 256 * 1024)));
            var partition = new PartitionUpdate
            {
                PartitionName = "part" + i,
                NewPartitionInfo = new PartitionInfo { Size = 256 * 1024, Hash = hash }
            };
            var operation = new InstallOperation
            {
                Type = InstallOperation.Types.Type.Replace,
                DataOffset = (ulong)(i * 256 * 1024),
                DataLength = 256 * 1024,
                DataSha256Hash = hash
            };
            operation.DstExtents.Add(new Extent { NumBlocks = 64 }); partition.Operations.Add(operation);
            manifest.Partitions.Add(partition);
        }
        using var input = new CountedReadStream(data, maximumRead: 997);
        using var directory = new TempDirectory();
        await new PayloadExtractor().ExtractPartitionsAsync(manifest, input, 0, [], directory.Path, TestContext.Current.CancellationToken);
        for (var i = 0; i < 8; i++)
        {
            using var output = File.OpenRead(Path.Combine(directory.Path, "part" + i + ".img"));
            Assert.Equal(manifest.Partitions[i].NewPartitionInfo!.Hash.ToByteArray(), SHA256.HashData(output));
        }
    }

    private sealed class CountedReadStream(byte[] data, int maximumRead = int.MaxValue) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);
        internal int ReadCalls { get; private set; }
        internal int MaximumRead { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            return _inner.Read(buffer[..Math.Min(buffer.Length, maximumRead)]);
        }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}