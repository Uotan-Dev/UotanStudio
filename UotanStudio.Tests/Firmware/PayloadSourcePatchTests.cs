using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PayloadSourcePatchTests
{
    private const uint BlockSize = 16;
    private const int BlockCount = 16;

    private static byte[] BuildSource()
    {
        var data = new byte[BlockCount * (int)BlockSize];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 3 + 1);
        }

        return data;
    }

    private static byte[] BuildTarget()
    {
        var data = BuildSource();
        foreach (var block in new[] { 2, 5, 8, 9, 10 })
        {
            for (var i = 0; i < BlockSize; i++)
            {
                data[block * (int)BlockSize + i] = (byte)(0xE0 + block);
            }
        }

        return data;
    }

    [Fact]
    public async Task AddPartitionIncrementalImagediff_EmitsSourceImagediffOperationsWithSourcePartitionName()
    {
        var source = BuildSource();
        var target = BuildTarget();
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(BlockSize))
        {
            builder.AddPartitionIncrementalImagediff("system", new MemoryStream(target), new MemoryStream(source));
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        using var stream = File.OpenRead(payload);
        var (manifest, _) = PayloadParser.ReadManifest(stream);
        var partition = Assert.Single(manifest.Partitions);
        var imagediff = partition.Operations.Where(op => op.Type == InstallOperation.Types.Type.SourceImagediff).ToList();
        Assert.NotEmpty(imagediff);
        Assert.All(imagediff, op => Assert.Equal("system", op.SourcePartitionName));
        Assert.All(imagediff, op =>
        {
            Assert.True(op.DataLength > 0);
            Assert.Equal((ulong)op.DstLength, op.SrcLength);
            var sourceExtent = Assert.Single(op.SrcExtents);
            Assert.True((sourceExtent.StartBlock + sourceExtent.NumBlocks) * BlockSize <= (ulong)source.Length);
            Assert.Equal(SHA256.HashData(source.AsSpan((int)(sourceExtent.StartBlock * BlockSize), (int)op.SrcLength)), op.SrcSha256Hash.ToByteArray());
        });
        Assert.Equal(2U, manifest.MinorVersion);
        Assert.Equal(SHA256.HashData(target), partition.NewPartitionInfo!.Hash.ToByteArray());
        Assert.Equal(SHA256.HashData(source), partition.OldPartitionInfo!.Hash.ToByteArray());
    }

    [Fact]
    public async Task AddPartitionIncrementalImagediff_RebuildsTargetByteExactThroughManagedImgdiffApply()
    {
        var source = BuildSource();
        var target = BuildTarget();
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(BlockSize))
        {
            builder.AddPartitionIncrementalImagediff("system", new MemoryStream(target), new MemoryStream(source), "system_other");
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var bytes = File.ReadAllBytes(payload);
        var manifestLength = checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12)));
        var metadataSignatureLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        var blobStart = 24 + manifestLength + metadataSignatureLength;
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(bytes[24..(24 + manifestLength)]);
        var partition = Assert.Single(manifest.Partitions);
        var blob = bytes[blobStart..];
        var rebuilt = new byte[target.Length];
        foreach (var op in partition.Operations)
        {
            var destination = Assert.Single(op.DstExtents);
            var destinationOffset = (int)(destination.StartBlock * BlockSize);
            var length = (int)op.DstLength;
            switch (op.Type)
            {
                case InstallOperation.Types.Type.SourceCopy:
                    {
                        var extent = Assert.Single(op.SrcExtents);
                        source.AsSpan((int)(extent.StartBlock * BlockSize), length).CopyTo(rebuilt.AsSpan(destinationOffset));
                        break;
                    }

                case InstallOperation.Types.Type.SourceImagediff:
                    {
                        Assert.Equal("system_other", op.SourcePartitionName);
                        var extent = Assert.Single(op.SrcExtents);
                        var old = source.AsSpan((int)(extent.StartBlock * BlockSize), length).ToArray();
                        Assert.Equal(SHA256.HashData(old), op.SrcSha256Hash.ToByteArray());
                        var patch = blob[(int)op.DataOffset..((int)op.DataOffset + (int)op.DataLength)];
                        Assert.Equal(SHA256.HashData(patch), op.DataSha256Hash.ToByteArray());
                        Assert.Equal("IMGDIFF2"u8, patch[..8]);
                        using var output = new MemoryStream();
                        ImageDeltaPatch.Apply(new MemoryStream(old), new MemoryStream(patch), output, length);
                        Assert.Equal(length, (int)output.Length);
                        output.GetBuffer().AsSpan(0, length).CopyTo(rebuilt.AsSpan(destinationOffset));
                        break;
                    }

                case InstallOperation.Types.Type.Replace:
                    {
                        blob[(int)op.DataOffset..((int)op.DataOffset + (int)op.DataLength)].CopyTo(rebuilt.AsSpan(destinationOffset));
                        break;
                    }

                default:
                    throw new Xunit.Sdk.XunitException($"unexpected operation type {op.Type}");
            }
        }

        Assert.Equal(target, rebuilt);
    }

    [Fact]
    public async Task AddPartitionIncremental_ImagediffOperations_RoundTripThroughProtobufParse()
    {
        var source = BuildSource();
        var target = BuildTarget();
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(BlockSize))
        {
            builder.AddPartitionIncrementalImagediff("system", new MemoryStream(target), new MemoryStream(source), "vendor_a");
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var bytes = File.ReadAllBytes(payload);
        var manifestLength = checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12)));
        var first = DeltaArchiveManifest.Parser.ParseFrom(bytes[24..(24 + manifestLength)]);
        var serialized = first.ToByteArray();
        var second = DeltaArchiveManifest.Parser.ParseFrom(serialized);
        Assert.Equal(first, second);
        Assert.Contains("vendor_a", second.Partitions.SelectMany(update => update.Operations).Select(op => op.SourcePartitionName));
    }

    [Fact]
    public async Task AddPartitionIncremental_DefaultMode_EmitsNoSourceImagediffOperations()
    {
        var source = BuildSource();
        var target = BuildTarget();
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(BlockSize))
        {
            builder.AddPartitionIncremental("system", new MemoryStream(target), new MemoryStream(source));
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        using var stream = File.OpenRead(payload);
        var (manifest, _) = PayloadParser.ReadManifest(stream);
        var partition = Assert.Single(manifest.Partitions);
        Assert.All(partition.Operations, op => Assert.NotEqual(InstallOperation.Types.Type.SourceImagediff, op.Type));
        Assert.All(partition.Operations, op => Assert.Equal(string.Empty, op.SourcePartitionName));
    }

    [Fact]
    public async Task AddPartitionIncrementalImagediff_ParallelExtractor_ReportsUnsupportedOperation()
    {
        var source = BuildSource();
        var target = BuildTarget();
        using var temp = new TempDirectory();
        var sourcePath = temp.WriteBytes("source.img", source);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(BlockSize))
        {
            builder.AddPartitionIncrementalImagediff("system", new MemoryStream(target), new MemoryStream(source));
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var output = Path.Combine(temp.Path, "out");
        await Assert.ThrowsAnyAsync<PayloadException>(() => PayloadParser.ExtractAsync(payload, ["system"], output,
            cancellationToken: TestContext.Current.CancellationToken,
            sourceImages: new Dictionary<string, string> { ["system"] = sourcePath }));
    }
}