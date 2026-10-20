using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class PayloadVariableLengthTests
{
    private const int BlockSize = 4096;

    [Theory]
    [InlineData(255, 259)]
    [InlineData(259, 255)]
    [InlineData(1, 257)]
    [InlineData(257, 1)]
    public async Task Build_VariableLength_IndependentExtentReplayMatchesTarget(int sourceBlocks, int targetBlocks)
    {
        using var temp = new TempDirectory();
        var source = new byte[sourceBlocks * BlockSize];
        new Random(731).NextBytes(source);
        var target = new byte[targetBlocks * BlockSize];
        source.AsSpan(0, Math.Min(source.Length, target.Length)).CopyTo(target);
        target.AsSpan(target.Length - BlockSize).Fill(0xA7);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using var builder = new PayloadBuilder();
        builder.AddPartitionIncremental("system", new MemoryStream(target), new MemoryStream(source));
        await builder.BuildAsync(payload, TestContext.Current.CancellationToken);

        using var input = File.OpenRead(payload);
        var header = new byte[24];
        input.ReadExactly(header);
        Assert.Equal("CrAU"u8.ToArray(), header[..4]);
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4)));
        var manifestBytes = new byte[checked((int)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)))];
        input.ReadExactly(manifestBytes);
        var dataStart = input.Position + BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestBytes);
        Assert.True(manifest.MinorVersion >= 2);
        Assert.Equal((uint)BlockSize, manifest.BlockSize);
        var partition = Assert.Single(manifest.Partitions);
        Assert.NotNull(partition.OldPartitionInfo);
        Assert.NotNull(partition.NewPartitionInfo);
        Assert.Equal((ulong)source.Length, partition.OldPartitionInfo.Size);
        Assert.Equal(SHA256.HashData(source), partition.OldPartitionInfo.Hash.ToByteArray());
        Assert.Equal((ulong)target.Length, partition.NewPartitionInfo.Size);
        var replay = new byte[target.Length];
        var written = new bool[targetBlocks];
        foreach (var operation in partition.Operations)
        {
            var destination = Assert.Single(operation.DstExtents);
            var count = checked((int)destination.NumBlocks * BlockSize);
            var offset = checked((int)destination.StartBlock * BlockSize);
            var bytes = new byte[count];
            if (operation.Type == InstallOperation.Types.Type.SourceCopy)
            {
                var extent = Assert.Single(operation.SrcExtents);
                Assert.Equal(destination.NumBlocks, extent.NumBlocks);
                source.AsSpan(checked((int)extent.StartBlock * BlockSize), count).CopyTo(bytes);
                Assert.Equal(SHA256.HashData(bytes), operation.SrcSha256Hash.ToByteArray());
                Assert.Equal(0UL, operation.DataLength);
            }
            else
            {
                Assert.Equal(InstallOperation.Types.Type.Replace, operation.Type);
                Assert.Empty(operation.SrcExtents);
                Assert.Equal((ulong)count, operation.DataLength);
                input.Position = checked(dataStart + (long)operation.DataOffset);
                input.ReadExactly(bytes);
                Assert.Equal(SHA256.HashData(bytes), operation.DataSha256Hash.ToByteArray());
            }

            bytes.CopyTo(replay, offset);
            for (var block = offset / BlockSize; block < (offset + count) / BlockSize; block++)
            {
                Assert.False(written[block]);
                written[block] = true;
            }
        }

        Assert.All(written, Assert.True);
        Assert.Equal(target, replay);
        Assert.Equal(SHA256.HashData(replay), partition.NewPartitionInfo.Hash.ToByteArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_CanceledDuringShortRead_PreservesExistingPayload(bool cancelSourceTail)
    {
        using var temp = new TempDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var payload = temp.WriteBytes("payload.bin", [1, 2, 3]);
        using var interrupted = new ControlledReadStream(new byte[3 * 1024 * 1024],
            cancelSourceTail ? BlockSize + 113 : 0, cancellation);
        using var other = new MemoryStream(new byte[BlockSize]);
        using var builder = new PayloadBuilder();
        builder.AddPartitionIncremental("system",
            cancelSourceTail ? other : interrupted, cancelSourceTail ? interrupted : other);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(payload, cancellation.Token));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(payload));
        Assert.Single(Directory.GetFiles(temp.Path));
        Assert.Equal(0, interrupted.ReadsAfterCancellation);
        Assert.InRange(interrupted.MaximumRequest, 1, 64 * 1024);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Build_TruncatedInput_PreservesExistingPayload(bool truncateSource)
    {
        using var temp = new TempDirectory();
        var payload = temp.WriteBytes("payload.bin", [1, 2, 3]);
        using var truncated = new ControlledReadStream(new byte[BlockSize * 3], BlockSize + 13);
        using var other = new MemoryStream(new byte[BlockSize]);
        using var builder = new PayloadBuilder();
        builder.AddPartitionIncremental("system", truncateSource ? other : truncated, truncateSource ? truncated : other);

        await Assert.ThrowsAsync<PayloadGenerationException>(() => builder.BuildAsync(payload, TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(payload));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData(4095, 8192)]
    [InlineData(4096, 8193)]
    public async Task Build_UnalignedTail_RejectsWithoutReplacingPayload(int sourceLength, int targetLength)
    {
        using var temp = new TempDirectory();
        var payload = temp.WriteBytes("payload.bin", [1, 2, 3]);
        using var builder = new PayloadBuilder();
        builder.AddPartitionIncremental("system", new MemoryStream(new byte[targetLength]), new MemoryStream(new byte[sourceLength]));

        await Assert.ThrowsAsync<PayloadGenerationException>(() => builder.BuildAsync(payload, TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(payload));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData(-4096)]
    [InlineData(4096)]
    public async Task Extract_WrongSourceLength_PreservesExistingImage(int sizeDifference)
    {
        using var temp = new TempDirectory();
        var source = new byte[3 * BlockSize];
        var sourcePath = temp.WriteBytes("source.img", source);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using var builder = new PayloadBuilder();
        builder.AddPartitionIncremental("system", new MemoryStream(new byte[BlockSize]), new MemoryStream(source));
        await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        File.WriteAllBytes(sourcePath, new byte[source.Length + sizeDifference]);
        var output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        var image = Path.Combine(output, "system.img");
        File.WriteAllBytes(image, [1, 2, 3]);

        await Assert.ThrowsAsync<PartitionExtractionException>(() => PayloadParser.ExtractAsync(payload, ["system"], output,
            cancellationToken: TestContext.Current.CancellationToken, sourceImages: new Dictionary<string, string> { ["system"] = sourcePath }));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(image));
        Assert.Single(Directory.GetFiles(output));
    }

    [Theory]
    [InlineData(3, 7)]
    [InlineData(7, 3)]
    public async Task ExtractPartition_CanceledBeforePublication_PreservesExistingImage(int sourceBlocks, int targetBlocks)
    {
        using var temp = new TempDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var source = new byte[sourceBlocks * BlockSize];
        var target = new byte[targetBlocks * BlockSize];
        target.AsSpan(target.Length - BlockSize).Fill(0xA7);
        var sourcePath = temp.WriteBytes("source.img", source);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using var builder = new PayloadBuilder();
        builder.AddPartitionIncremental("system", new MemoryStream(target), new MemoryStream(source));
        await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        var output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        var image = Path.Combine(output, "system.img");
        File.WriteAllBytes(image, [1, 2, 3]);
        var progress = new CancelingProgress(cancellation);
        using var input = File.OpenRead(payload);
        var (manifest, header) = PayloadParser.ReadManifest(input);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PayloadExtractor().ExtractPartitionAsync(
            Assert.Single(manifest.Partitions), image, input, header.BaseOffset, manifest.BlockSize,
            cancellationToken: cancellation.Token, sourceImages: new Dictionary<string, string> { ["system"] = sourcePath },
            byteProgress: progress));

        Assert.True(progress.SawProcessedBytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(image));
        Assert.Single(Directory.GetFiles(output));
    }

    private sealed class CancelingProgress(CancellationTokenSource cancellation) : IProgress<(long Processed, long Total)>
    {
        public bool SawProcessedBytes { get; private set; }

        public void Report((long Processed, long Total) value)
        {
            if (value.Processed > 0)
            {
                SawProcessedBytes = true;
                cancellation.Cancel();
            }
        }
    }

    private sealed class ControlledReadStream(byte[] content, long stopAt, CancellationTokenSource? cancellation = null) : MemoryStream(content)
    {
        public int ReadsAfterCancellation { get; private set; }
        public int MaximumRequest { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            MaximumRequest = Math.Max(MaximumRequest, buffer.Length);
            if (cancellation?.IsCancellationRequested == true)
            {
                ReadsAfterCancellation++;
            }

            if (cancellation is null && Position >= stopAt)
            {
                return 0;
            }

            var read = base.Read(buffer[..Math.Min(buffer.Length, 113)]);
            if (Position >= stopAt)
            {
                cancellation?.Cancel();
            }

            return read;
        }
    }
}