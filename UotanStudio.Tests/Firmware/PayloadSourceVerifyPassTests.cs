using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Source read-pass tests for delta extraction. A base image whose whole-partition digest was already proven covers
/// every byte its source extents can address, so the per-operation source hashing pass is redundant and is skipped;
/// these tests count the bytes actually pulled from the base image to pin that one-pass behaviour, and keep the
/// per-operation digest enforced whenever no whole-partition digest exists to replace it.
/// <para>差分提取的源读取趟数测试。整分区摘要已被证明的基座镜像，覆盖了其源 extent 可能触及的每个字节，
/// 因此逐操作的源哈希趟是冗余的并被跳过；本测试统计实际从基座镜像取出的字节数以钉住"单趟"行为，
/// 并在不存在可替代的整分区摘要时，继续强制逐操作摘要校验。</para>
/// </summary>
public class PayloadSourceVerifyPassTests
{
    private const uint BlockSize = 4;
    private const int SourceLength = 16;

    private static byte[] BuildSource()
    {
        var data = new byte[SourceLength];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = unchecked((byte)(i * 7 + 3));
        }

        return data;
    }

    private static byte[] BuildTarget(byte[] source)
    {
        var data = (byte[])source.Clone();
        data[4] ^= 0xA5;
        data[11] ^= 0x5A;
        return data;
    }

    private static byte[] CreateBsdiffPatch(byte[] source, byte[] target)
    {
        using var sourceStream = new MemoryStream(source, writable: false);
        using var targetStream = new MemoryStream(target, writable: false);
        using var patch = new MemoryStream();
        BinaryDeltaPatch.Create(sourceStream, targetStream, patch, TestContext.Current.CancellationToken);
        return patch.ToArray();
    }

    private static InstallOperation SourceBsdiffOperation(byte[] source, byte[] patch, byte[] srcHash)
    {
        var operation = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceBsdiff,
            SrcLength = (ulong)source.Length,
            DstLength = (ulong)source.Length,
            DataOffset = 0,
            DataLength = (ulong)patch.Length,
            DataSha256Hash = ByteString.CopyFrom(SHA256.HashData(patch)),
            SrcSha256Hash = ByteString.CopyFrom(srcHash)
        };
        operation.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = (ulong)(source.Length / BlockSize) });
        operation.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = (ulong)(source.Length / BlockSize) });
        return operation;
    }

    private static PartitionInfo WholeImageInfo(byte[] image)
        => new() { Size = (ulong)image.Length, Hash = ByteString.CopyFrom(SHA256.HashData(image)) };

    [Fact]
    public void ProcessDeltaOperation_SourceAlreadyVerified_ReadsSourceExtentsExactlyOnce()
    {
        var source = BuildSource();
        var patch = CreateBsdiffPatch(source, BuildTarget(source));
        var operation = SourceBsdiffOperation(source, patch, SHA256.HashData(source));
        using var temp = new TempDirectory();
        using var countingSource = new ReadCountingStream(source);
        using var payload = new MemoryStream(patch, writable: false);
        using var target = new FileStream(temp.WriteBytes("target.img", new byte[SourceLength]), FileMode.Open, FileAccess.ReadWrite);

        OperationProcessor.ProcessDeltaOperation(
            operation, countingSource, target, payload, 0, BlockSize,
            TestContext.Current.CancellationToken, sourceAlreadyVerified: true);

        Assert.Equal(SourceLength, countingSource.BytesRead);
        target.Position = 0;
        var actual = new byte[SourceLength];
        target.ReadExactly(actual);
        Assert.Equal(BuildTarget(source), actual);
    }

    [Fact]
    public void ProcessDeltaOperation_SourceNotVerified_HashesSourceExtentsBeforeApplying()
    {
        var source = BuildSource();
        var patch = CreateBsdiffPatch(source, BuildTarget(source));
        var operation = SourceBsdiffOperation(source, patch, SHA256.HashData(source));
        using var temp = new TempDirectory();
        using var countingSource = new ReadCountingStream(source);
        using var payload = new MemoryStream(patch, writable: false);
        using var target = new FileStream(temp.WriteBytes("target.img", new byte[SourceLength]), FileMode.Open, FileAccess.ReadWrite);

        OperationProcessor.ProcessDeltaOperation(
            operation, countingSource, target, payload, 0, BlockSize,
            TestContext.Current.CancellationToken, sourceAlreadyVerified: false);

        Assert.Equal(SourceLength * 2, countingSource.BytesRead);
        target.Position = 0;
        var actual = new byte[SourceLength];
        target.ReadExactly(actual);
        Assert.Equal(BuildTarget(source), actual);
    }

    [Fact]
    public void ProcessDeltaOperation_SourceNotVerifiedAndCorrupted_RejectsBeforeWritingTarget()
    {
        var source = BuildSource();
        var corrupted = BuildSource();
        corrupted[0] ^= 0xFF;
        var patch = CreateBsdiffPatch(source, BuildTarget(source));
        var operation = SourceBsdiffOperation(source, patch, SHA256.HashData(source));
        using var temp = new TempDirectory();
        using var countingSource = new ReadCountingStream(corrupted);
        using var payload = new MemoryStream(patch, writable: false);
        using var target = new FileStream(temp.WriteBytes("target.img", new byte[SourceLength]), FileMode.Open, FileAccess.ReadWrite);

        var exception = Assert.Throws<PartitionExtractionException>(() => OperationProcessor.ProcessDeltaOperation(
            operation, countingSource, target, payload, 0, BlockSize,
            TestContext.Current.CancellationToken, sourceAlreadyVerified: false));

        Assert.Contains("SHA256", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SourceLength, countingSource.BytesRead);
    }

    [Fact]
    public void ExtractPartition_OldPartitionHashVerified_WholeImageDigestReplacesPerOperationDigest()
    {
        var source = BuildSource();
        var patch = CreateBsdiffPatch(source, BuildTarget(source));
        var operation = SourceBsdiffOperation(source, patch, SHA256.HashData(new byte[SourceLength]));
        var partition = new PartitionUpdate
        {
            PartitionName = "system",
            OldPartitionInfo = WholeImageInfo(source)
        };
        partition.Operations.Add(operation);

        using var temp = new TempDirectory();
        var outputDirectory = Path.Combine(temp.Path, "out");
        var payload = WritePayload(temp, partition, operation, patch);
        var basePath = temp.WriteBytes("base.img", source);

        PayloadParser.Extract(payload, ["system"], outputDirectory,
            sourceImages: new Dictionary<string, string> { ["system"] = basePath });

        Assert.Equal(BuildTarget(source), File.ReadAllBytes(Path.Combine(outputDirectory, "system.img")));
    }

    [Fact]
    public void ExtractPartition_OldPartitionHashAbsent_PerOperationSourceDigestIsStillEnforced()
    {
        var source = BuildSource();
        var patch = CreateBsdiffPatch(source, BuildTarget(source));
        var operation = SourceBsdiffOperation(source, patch, SHA256.HashData(new byte[SourceLength]));
        var partition = new PartitionUpdate { PartitionName = "system" };
        partition.Operations.Add(operation);

        using var temp = new TempDirectory();
        var outputDirectory = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(outputDirectory);
        var payload = WritePayload(temp, partition, operation, patch);
        var basePath = temp.WriteBytes("base.img", source);

        var exception = Assert.Throws<PartitionExtractionException>(() => PayloadParser.Extract(payload, ["system"], outputDirectory,
            sourceImages: new Dictionary<string, string> { ["system"] = basePath }));

        Assert.Contains("SHA256", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(outputDirectory));
    }

    [Fact]
    public void ExtractPartition_OldPartitionHashWithCorruptBase_PreservesExistingOutput()
    {
        var source = BuildSource();
        var corrupt = BuildSource();
        corrupt[9] ^= 0xFF;
        var patch = CreateBsdiffPatch(source, BuildTarget(source));
        var operation = SourceBsdiffOperation(source, patch, SHA256.HashData(source));
        var partition = new PartitionUpdate
        {
            PartitionName = "system",
            OldPartitionInfo = WholeImageInfo(source)
        };
        partition.Operations.Add(operation);

        using var temp = new TempDirectory();
        var outputDirectory = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(outputDirectory);
        var existing = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(outputDirectory, "system.img"), existing);
        var payload = WritePayload(temp, partition, operation, patch);
        var basePath = temp.WriteBytes("base.img", corrupt);

        Assert.Throws<PartitionExtractionException>(() => PayloadParser.Extract(payload, ["system"], outputDirectory,
            sourceImages: new Dictionary<string, string> { ["system"] = basePath }));

        Assert.Equal(existing, File.ReadAllBytes(Path.Combine(outputDirectory, "system.img")));
        Assert.Empty(Directory.GetFiles(outputDirectory, "*.tmp-*"));
    }

    [Fact]
    public async Task ExtractPartition_RandomAccessBaseWithRewoundExtents_RoundTripsToTarget()
    {
        const int blockSize = 512;
        var source = new byte[blockSize * 8];
        new Random(0x6E7).NextBytes(source);
        var target = new byte[source.Length];
        source.AsSpan(blockSize * 6, blockSize * 2).CopyTo(target);
        source.AsSpan(blockSize * 2, blockSize * 4).CopyTo(target.AsSpan(blockSize * 2));
        source.AsSpan(0, blockSize * 2).CopyTo(target.AsSpan(blockSize * 6));
        target.AsSpan(blockSize * 4, blockSize).Fill(0xC3);

        using var temp = new TempDirectory();
        var sourcePath = temp.WriteBytes("source.img", source);
        var targetPath = temp.WriteBytes("target.img", target);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(blockSize))
        {
            builder.AddPartitionIncremental("system", targetPath, sourcePath);
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        using var stream = File.OpenRead(payload);
        var (manifest, _) = PayloadParser.ReadManifest(stream);
        Assert.Contains(Assert.Single(manifest.Partitions).Operations,
            operation => operation.Type is InstallOperation.Types.Type.SourceCopy or InstallOperation.Types.Type.Move);

        var outputDirectory = Path.Combine(temp.Path, "out");
        await PayloadParser.ExtractAsync(payload, ["system"], outputDirectory,
            cancellationToken: TestContext.Current.CancellationToken,
            sourceImages: new Dictionary<string, string> { ["system"] = sourcePath });

        Assert.Equal(target, File.ReadAllBytes(Path.Combine(outputDirectory, "system.img")));
    }

    private static string WritePayload(TempDirectory temp, PartitionUpdate partition, InstallOperation operation, byte[] blob)
    {
        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        manifest.Partitions.Add(partition);
        return temp.WriteBytes("payload.bin",
            PayloadDeltaTests.BuildPayload(manifest, new Dictionary<InstallOperation, byte[]> { [operation] = blob }));
    }

    private sealed class ReadCountingStream : Stream
    {
        private readonly byte[] _data;
        private long _position;

        internal ReadCountingStream(byte[] data)
        {
            _data = data;
        }

        internal long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _data.Length || buffer.IsEmpty)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, _data.Length - _position);
            _data.AsSpan((int)_position, count).CopyTo(buffer);
            _position += count;
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _data.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}