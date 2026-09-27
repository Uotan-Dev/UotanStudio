using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Incremental (block-delta) payload generation tests: SOURCE_COPY for unchanged runs, REPLACE for
/// changed runs, correct old/new partition hashes, and a byte-for-byte round-trip through the managed
/// parser/extractor with a source base image. This is a structure-tier conformance check; a live
/// update_engine install is not exercised here (no such harness is available in this environment).
/// <para>增量（块级差量）载荷生成测试：未变更区输出 SOURCE_COPY、变更块输出 REPLACE，校验新旧分区哈希，
/// 并通过托管解析器/提取器在提供源基座的情况下逐字节往返还原。本测试为结构层一致性验证；
/// 未在本环境执行真实 update_engine 安装（无可用安装桩）。</para>
/// </summary>
public class PayloadIncrementalBuilderTests
{
    private const uint BlockSize = 16;
    private const int BlockCount = 16;

    private static byte[] BuildBase()
    {
        var data = new byte[BlockCount * (int)BlockSize];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 3 + 1);
        return data;
    }

    private static byte[] BuildTarget()
    {
        var data = BuildBase();
        foreach (var block in new[] { 2, 5, 8, 9, 10 })
            for (var i = 0; i < BlockSize; i++)
                data[block * (int)BlockSize + i] = (byte)(0xE0 + block);
        return data;
    }

    [Fact]
    public async Task AddPartitionIncremental_EmitsSourceCopyAndReplaceWithCorrectHashes()
    {
        var source = BuildBase();
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

        Assert.Equal(2U, manifest.MinorVersion);
        Assert.Equal((ulong)source.Length, partition.OldPartitionInfo!.Size);
        Assert.Equal(SHA256.HashData(source), partition.OldPartitionInfo.Hash.ToByteArray());
        Assert.Equal((ulong)target.Length, partition.NewPartitionInfo!.Size);
        Assert.Equal(SHA256.HashData(target), partition.NewPartitionInfo.Hash.ToByteArray());

        int[][] expectedCopy = [[0, 1], [3, 4], [6, 7], [11, 15]];
        (int Start, int Blocks)[] expectedReplace = [(2, 1), (5, 1), (8, 3)];
        var copies = partition.Operations.Where(op => op.Type == InstallOperation.Types.Type.SourceCopy).ToList();
        var replaces = partition.Operations.Where(op => op.Type is InstallOperation.Types.Type.Replace).ToList();
        Assert.Equal(expectedCopy.Length, copies.Count);
        Assert.Equal(expectedReplace.Length, replaces.Count);

        foreach (var op in copies)
        {
            var src = Assert.Single(op.SrcExtents);
            var dst = Assert.Single(op.DstExtents);
            Assert.Equal(src.StartBlock, dst.StartBlock);
            Assert.Equal(src.NumBlocks, dst.NumBlocks);
            Assert.Equal(0UL, op.DataLength);
            Assert.Equal((ulong)(op.DstLength), src.NumBlocks * BlockSize);
            var expectedHash = SHA256.HashData(source[(int)(src.StartBlock * BlockSize)..((int)(src.StartBlock + src.NumBlocks) * (int)BlockSize)]);
            Assert.Equal(expectedHash, op.SrcSha256Hash.ToByteArray());
        }

        foreach (var op in replaces)
        {
            var dst = Assert.Single(op.DstExtents);
            Assert.Equal((ulong)(op.DstLength), dst.NumBlocks * BlockSize);
            Assert.True(op.DataLength > 0);
        }

        Assert.Equal(BlockCount, (int)partition.Operations.SelectMany(op => op.DstExtents).Sum(e => (int)e.NumBlocks));
        ulong next = 0;
        foreach (var op in partition.Operations)
            foreach (var extent in op.DstExtents)
            {
                Assert.Equal(next, extent.StartBlock);
                next += extent.NumBlocks;
            }
        Assert.Equal((ulong)BlockCount, next);
    }

    [Fact]
    public async Task AddPartitionIncremental_RoundTripsToTargetThroughManagedExtractor()
    {
        var source = BuildBase();
        var target = BuildTarget();
        using var temp = new TempDirectory();
        var sourcePath = temp.WriteBytes("source.img", source);
        var targetPath = temp.WriteBytes("target.img", target);
        var payload = Path.Combine(temp.Path, "payload.bin");
        var outputDir = Path.Combine(temp.Path, "out");

        using (var builder = new PayloadBuilder().WithBlockSize(BlockSize))
            builder.AddPartitionIncremental("system", targetPath, sourcePath).Build(payload);

        PayloadParser.Extract(payload, ["system"], outputDir,
            sourceImages: new Dictionary<string, string> { ["system"] = sourcePath });

        Assert.Equal(target, File.ReadAllBytes(Path.Combine(outputDir, "system.img")));
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.BZip2)]
    [InlineData(CompressionType.Xz)]
    public async Task AddPartitionIncremental_WithCompression_RoundTripsToTarget(CompressionType compression)
    {
        var source = BuildBase();
        var target = BuildTarget();
        using var temp = new TempDirectory();
        var sourcePath = temp.WriteBytes("source.img", source);
        var targetPath = temp.WriteBytes("target.img", target);
        var payload = Path.Combine(temp.Path, "payload.bin");
        var outputDir = Path.Combine(temp.Path, "out");

        using (var builder = new PayloadBuilder().WithBlockSize(BlockSize))
            builder.AddPartitionIncremental("system", targetPath, sourcePath, compression).Build(payload);

        PayloadParser.Extract(payload, ["system"], outputDir,
            sourceImages: new Dictionary<string, string> { ["system"] = sourcePath });

        Assert.Equal(target, File.ReadAllBytes(Path.Combine(outputDir, "system.img")));
    }

    [Fact]
    public async Task AddPartitionIncremental_IdenticalImages_UsesOnlySourceCopy()
    {
        var data = BuildBase();
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("image.img", data);
        var payload = Path.Combine(temp.Path, "payload.bin");

        using (var builder = new PayloadBuilder().WithBlockSize(BlockSize))
            builder.AddPartitionIncremental("system", path, path).Build(payload);

        using var stream = File.OpenRead(payload);
        var (manifest, _) = PayloadParser.ReadManifest(stream);
        var partition = Assert.Single(manifest.Partitions);
        Assert.All(partition.Operations, op => Assert.Equal(InstallOperation.Types.Type.SourceCopy, op.Type));
        Assert.Single(partition.Operations);
        Assert.Equal(0L, partition.Operations.Sum(op => (long)op.DataLength));
    }

    [Theory]
    [InlineData(3, 7, CompressionType.None)]
    [InlineData(7, 3, CompressionType.None)]
    [InlineData(3, 7, CompressionType.BZip2)]
    [InlineData(7, 3, CompressionType.Xz)]
    [InlineData(255, 259, CompressionType.None)]
    [InlineData(259, 255, CompressionType.None)]
    public async Task AddPartitionIncremental_DifferentAlignedLengths_AppliesExactTarget(int sourceBlocks, int targetBlocks, CompressionType compression)
    {
        const int blockSize = 4096;
        var source = new byte[sourceBlocks * blockSize];
        new Random(42).NextBytes(source);
        var target = new byte[targetBlocks * blockSize];
        source.AsSpan(0, Math.Min(source.Length, target.Length)).CopyTo(target);
        target.AsSpan(blockSize, blockSize).Fill(0xE7);
        using var temp = new TempDirectory();
        var sourcePath = temp.WriteBytes("source.img", source);
        var payload = Path.Combine(temp.Path, "payload.bin");
        var output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "system.img"), new byte[source.Length + target.Length]);
        using (var builder = new PayloadBuilder().WithBlockSize(blockSize))
        {
            var targetStream = new MemoryStream(new byte[31].Concat(target).ToArray()) { Position = 31 };
            var sourceStream = new MemoryStream(new byte[17].Concat(source).ToArray()) { Position = 17 };
            builder.AddPartitionIncremental("system", targetStream, sourceStream, compression);
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        using (var stream = File.OpenRead(payload))
        {
            var (manifest, _) = PayloadParser.ReadManifest(stream);
            var partition = Assert.Single(manifest.Partitions);
            Assert.NotNull(partition.OldPartitionInfo);
            Assert.NotNull(partition.NewPartitionInfo);
            Assert.Equal((ulong)source.Length, partition.OldPartitionInfo.Size);
            Assert.Equal(SHA256.HashData(source), partition.OldPartitionInfo.Hash.ToByteArray());
            Assert.Equal((ulong)target.Length, partition.NewPartitionInfo.Size);
            Assert.Equal(SHA256.HashData(target), partition.NewPartitionInfo.Hash.ToByteArray());
            ulong next = 0;
            foreach (var operation in partition.Operations)
            {
                var destination = Assert.Single(operation.DstExtents);
                Assert.Equal(next, destination.StartBlock);
                next += destination.NumBlocks;
                Assert.True(operation.DstLength <= 1024 * 1024);
                if (operation.Type == InstallOperation.Types.Type.SourceCopy)
                {
                    var extent = Assert.Single(operation.SrcExtents);
                    Assert.True(extent.StartBlock + extent.NumBlocks <= (ulong)sourceBlocks);
                    Assert.Equal(extent.NumBlocks, destination.NumBlocks);
                    Assert.Equal(SHA256.HashData(source.AsSpan((int)extent.StartBlock * blockSize, (int)extent.NumBlocks * blockSize)), operation.SrcSha256Hash.ToByteArray());
                }
                else
                {
                    Assert.Empty(operation.SrcExtents);
                }
            }
            Assert.Equal((ulong)targetBlocks, next);
        }

        await PayloadParser.ExtractAsync(payload, ["system"], output,
            cancellationToken: TestContext.Current.CancellationToken,
            sourceImages: new Dictionary<string, string> { ["system"] = sourcePath });
        Assert.Equal(target, File.ReadAllBytes(Path.Combine(output, "system.img")));
    }

    [Fact]
    public async Task AddPartitionIncremental_ShrunkSourceTailCorrupted_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var source = new byte[3 * 1024 * 1024];
        var target = source[..4096];
        var sourcePath = temp.WriteBytes("source.img", source);
        var targetPath = temp.WriteBytes("target.img", target);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder())
        {
            builder.AddPartitionIncremental("system", targetPath, sourcePath);
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }
        source[^1] = 1;
        File.WriteAllBytes(sourcePath, source);
        var output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        var existing = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(output, "system.img"), existing);
        await Assert.ThrowsAsync<UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions.PartitionExtractionException>(() => PayloadParser.ExtractAsync(payload, ["system"], output,
            cancellationToken: TestContext.Current.CancellationToken,
            sourceImages: new Dictionary<string, string> { ["system"] = sourcePath }));
        Assert.Equal(existing, File.ReadAllBytes(Path.Combine(output, "system.img")));
        Assert.Single(Directory.GetFiles(output));
    }

    [Fact]
    public void AddPartitionIncremental_UnalignedSource_PreservesExistingPayload()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.WriteBytes("source.img", BuildBase()[..^1]);
        var shortTarget = temp.WriteBytes("target.img", BuildBase());
        var payload = temp.WriteBytes("payload.bin", [1, 2, 3]);
        using var builder = new PayloadBuilder().WithBlockSize(BlockSize);
        builder.AddPartitionIncremental("system", shortTarget, sourcePath);
        Assert.Throws<UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions.PayloadGenerationException>(() =>
            builder.Build(payload));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(payload));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }
}
