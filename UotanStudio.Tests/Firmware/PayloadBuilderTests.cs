using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class PayloadBuilderTests
{
    [Theory]
    [InlineData(CompressionType.None, 0)]
    [InlineData(CompressionType.BZip2, 1)]
    [InlineData(CompressionType.Xz, 8)]
    public async Task Build_UsesBoundedStandardOperationsAndWholePartitionHash(CompressionType compression, int operationType)
    {
        using var temp = new TempDirectory(); var source = new byte[2 * 1024 * 1024 + 4096]; new Random(42).NextBytes(source);
        var image = temp.WriteBytes("source.img", source); var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder())
            await builder.AddPartitionCompressed("system", image, compression).BuildAsync(payload, TestContext.Current.CancellationToken);

        using (var stream = File.OpenRead(payload))
        {
            var header = new byte[24]; stream.ReadExactly(header);
            Assert.Equal("CrAU"u8.ToArray(), header[..4]);
            Assert.Equal(2UL, BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4)));
            Assert.Equal(0U, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)));
            var manifestLength = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12));
            var manifestData = new byte[checked((int)manifestLength)]; stream.ReadExactly(manifestData);
            var manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestData);
            var partition = Assert.Single(manifest.Partitions);
            Assert.NotNull(partition.NewPartitionInfo);
            Assert.Equal((ulong)source.Length, partition.NewPartitionInfo.Size);
            Assert.Equal(SHA256.HashData(source), partition.NewPartitionInfo.Hash.ToByteArray());
            Assert.Equal(3, partition.Operations.Count);
            foreach (var operation in partition.Operations)
            {
                Assert.Equal(operationType, (int)operation.Type);
                Assert.InRange(operation.DstLength, 1UL, 1024UL * 1024);
                Assert.Equal(operation.DstLength, Assert.Single(operation.DstExtents).NumBlocks * 4096UL);
                stream.Position = checked(24 + (long)manifestLength + (long)operation.DataOffset);
                var blob = new byte[checked((int)operation.DataLength)]; stream.ReadExactly(blob);
                Assert.Equal(SHA256.HashData(blob), operation.DataSha256Hash.ToByteArray());
                if (compression == CompressionType.Xz) Assert.Equal(1, blob[7]);
            }
        }
        PayloadParser.Extract(payload, ["system"], temp.Path);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(temp.Path, "system.img")));
    }

    [Theory]
    [InlineData(0U, 1)]
    [InlineData(2U, 1)]
    [InlineData(3U, 1)]
    [InlineData(4U, 6)]
    [InlineData(9U, 6)]
    public void Build_ZeroPartition_UsesVersionCompatibleBoundedOperations(uint minorVersion, int operationType)
    {
        using var temp = new TempDirectory(); var payload = Path.Combine(temp.Path, "payload.bin");
        const int length = 1024 * 1024 + 4096;
        using (var builder = new PayloadBuilder()) builder.WithMinorVersion(minorVersion).AddZeroPartition("zero", length).Build(payload);
        using (var stream = File.OpenRead(payload))
        {
            var header = new byte[24]; stream.ReadExactly(header);
            var manifestData = new byte[checked((int)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)))]; stream.ReadExactly(manifestData);
            var manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestData);
            Assert.Equal(minorVersion, manifest.MinorVersion);
            var partition = Assert.Single(manifest.Partitions); Assert.Equal(2, partition.Operations.Count);
            Assert.All(partition.Operations, operation => { Assert.Equal(operationType, (int)operation.Type); Assert.InRange(operation.DstLength, 1UL, 1024UL * 1024); });
        }
        PayloadParser.Extract(payload, ["zero"], temp.Path);
        Assert.Equal(new byte[length], File.ReadAllBytes(Path.Combine(temp.Path, "zero.img")));
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(11U)]
    public void WithMinorVersion_DeprecatedOrUnknownValues_AreRejected(uint minorVersion)
    {
        using var builder = new PayloadBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithMinorVersion(minorVersion));
    }

    [Fact]
    public void Build_MinorVersionTen_RoundTripsThroughParser()
    {
        using var temp = new TempDirectory(); var payload = Path.Combine(temp.Path, "payload.bin");
        const int length = 8192;
        using (var builder = new PayloadBuilder()) builder.WithMinorVersion(10).AddZeroPartition("zero", length).Build(payload);
        using (var stream = File.OpenRead(payload))
        {
            var header = new byte[24]; stream.ReadExactly(header);
            var manifestData = new byte[checked((int)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)))]; stream.ReadExactly(manifestData);
            Assert.Equal(10U, DeltaArchiveManifest.Parser.ParseFrom(manifestData).MinorVersion);
        }

        PayloadParser.Extract(payload, ["zero"], temp.Path);
        Assert.Equal(new byte[length], File.ReadAllBytes(Path.Combine(temp.Path, "zero.img")));
    }

    [Fact]
    public void Build_ZeroPartition_EnforcesOperationCountBeforeReading()
    {
        using var builder = new PayloadBuilder(); using var output = new MemoryStream();
        builder.WithMinorVersion(4).AddZeroPartition("zero", 65537L * 1024 * 1024);
        var error = Assert.Throws<PayloadGenerationException>(() => builder.Build(output));
        Assert.Contains("65536 operations", error.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Build_UnalignedInput_PreservesExistingPayload()
    {
        using var temp = new TempDirectory(); var payload = temp.WriteBytes("payload.bin", "existing"u8.ToArray());
        using var builder = new PayloadBuilder(); builder.AddPartition("system", temp.WriteBytes("source.img", new byte[4095]));
        Assert.Throws<PayloadGenerationException>(() => builder.Build(payload));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(payload));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Build_FewerWorkerSlotsThanChunks_ReportsWholePartitionSizeAndOperations(int workers)
    {
        using var temp = new TempDirectory(); var source = new byte[2 * 1024 * 1024 + 4096]; new Random(42).NextBytes(source);
        var image = temp.WriteBytes("source.img", source); var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder().WithCompressionWorkers(workers))
            await builder.AddPartitionCompressed("system", image, CompressionType.None).BuildAsync(payload, TestContext.Current.CancellationToken);

        using (var stream = File.OpenRead(payload))
        {
            var header = new byte[24]; stream.ReadExactly(header);
            var manifestData = new byte[checked((int)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)))]; stream.ReadExactly(manifestData);
            var partition = Assert.Single(DeltaArchiveManifest.Parser.ParseFrom(manifestData).Partitions);
            Assert.NotNull(partition.NewPartitionInfo);
            Assert.Equal((ulong)source.Length, partition.NewPartitionInfo.Size);
            Assert.Equal(SHA256.HashData(source), partition.NewPartitionInfo.Hash.ToByteArray());
            Assert.Equal(3, partition.Operations.Count);
            var covered = 0UL; foreach (var operation in partition.Operations) covered += operation.DstLength;
            Assert.Equal((ulong)source.Length, covered);
            Assert.All(partition.Operations, operation => Assert.InRange(operation.DstLength, 1UL, 1024UL * 1024));
        }

        PayloadParser.Extract(payload, ["system"], temp.Path);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(temp.Path, "system.img")));
    }

    [Theory]
    [InlineData(CompressionType.Brotli)]
    [InlineData(CompressionType.Zstd)]
    public void Build_RejectsUndefinedReplaceCompression(CompressionType compression)
    {
        using var temp = new TempDirectory(); using var builder = new PayloadBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddPartitionCompressed("system", temp.WriteBytes("source.img", new byte[4096]), compression));
    }

    [Fact]
    public async Task Build_LargeDeclaredSource_CancelsWithBoundedReadsAndPreservesPayload()
    {
        using var temp = new TempDirectory(); var payload = temp.WriteBytes("payload.bin", "existing"u8.ToArray());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var input = new LargeSource(cancellation); using var builder = new PayloadBuilder(); builder.AddPartition("system", input);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(payload, cancellation.Token));
        Assert.InRange(input.BytesRead, 2 * 1024 * 1024L, 3 * 1024 * 1024L);
        Assert.InRange(input.MaximumReadRequest, 1, 1024 * 1024);
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(payload));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    private sealed class LargeSource(CancellationTokenSource cancellation) : Stream
    {
        internal long BytesRead { get; private set; }
        internal int MaximumReadRequest { get; private set; }
        public override int Read(Span<byte> destination)
        {
            MaximumReadRequest = Math.Max(MaximumReadRequest, destination.Length);
            var count = Math.Min(destination.Length, 65536); destination[..count].Clear(); BytesRead += count;
            if (BytesRead >= 2 * 1024 * 1024) cancellation.Cancel();
            if (BytesRead > 16 * 1024 * 1024) throw new IOException("Cancellation was not observed.");
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 32L * 1024 * 1024 * 1024;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}