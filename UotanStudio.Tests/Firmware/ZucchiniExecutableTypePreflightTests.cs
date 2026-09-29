using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Diff.Zucchini;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Zucchini executable-type preflight tests. The executable type lives inside the Brotli-compressed Zucchini blob and
/// never in the protobuf manifest, so these tests pin that a partition carrying a non-NoOp element is rejected before
/// the extractor creates its temporary output, and that the rejection keeps the element's executable type and
/// destination offset instead of being flattened into a generic partition failure.
/// <para>Zucchini 可执行类型预扫描测试。可执行类型位于 Brotli 压缩的 Zucchini 数据块内部，从不出现在 protobuf
/// 清单里，因此本测试钉住：携带非 NoOp 元素的分区会在提取器创建临时输出之前被拒绝，
/// 且拒绝结果保留该元素的可执行类型与目标偏移，而不是被压平成泛化的分区失败。</para>
/// </summary>
public class ZucchiniExecutableTypePreflightTests
{
    private const uint BlockSize = 4;
    private const uint UnsupportedExeType = 0x3634666Cu;

    private static readonly byte[] OldData = [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17];

    private static readonly byte[] NewData = [0x10, 0x11, 0x12, 0x13, 0xA0, 0x15, 0x16, 0x17];

    [Fact]
    public void ScanOperation_NonRawExecutableType_ReportsExeTypeAndOffset()
    {
        var blob = Brotli(BuildPatch([(ExeType: UnsupportedExeType, NewOffset: 4u)]));
        using var payload = Payload(blob);
        var operation = ZucchiniOperation((ulong)blob.Length);

        var exception = Assert.Throws<ZucchiniUnsupportedExecutableTypeException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));

        Assert.Equal(UnsupportedExeType, exception.ExeType);
        Assert.Equal(4u, exception.Offset);
    }

    [Fact]
    public void ScanOperation_SecondElementNonRaw_ReportsThatElementsOffset()
    {
        var blob = Brotli(BuildPatch(
            [(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u), (ExeType: UnsupportedExeType, NewOffset: 4u)]));
        using var payload = Payload(blob);
        var operation = ZucchiniOperation((ulong)blob.Length);

        var exception = Assert.Throws<ZucchiniUnsupportedExecutableTypeException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));

        Assert.Equal(UnsupportedExeType, exception.ExeType);
        Assert.Equal(4u, exception.Offset);
    }

    [Fact]
    public void ScanOperations_RawElements_AcceptsContainerAndRestoresStreamPosition()
    {
        var blob = Brotli(BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]));
        using var payload = Payload(blob);
        payload.Position = 3;
        var operations = new[] { ZucchiniOperation((ulong)blob.Length) };

        ZucchiniExecutableTypeScanner.ScanOperations(payload, operations, 0, TestContext.Current.CancellationToken);

        Assert.Equal(3, payload.Position);
    }

    [Fact]
    public void ScanOperation_BadMagic_ThrowsZucchiniFormatException()
    {
        var blob = BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]);
        blob[0] ^= 0xFF;
        using var payload = Payload(Brotli(blob));
        var operation = ZucchiniOperation((ulong)payload.Length);

        Assert.Throws<ZucchiniFormatException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ScanOperation_UnsupportedMajorVersion_ThrowsZucchiniFormatException()
    {
        var blob = BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]);
        blob[4] = 9;
        using var payload = Payload(Brotli(blob));
        var operation = ZucchiniOperation((ulong)payload.Length);

        var exception = Assert.Throws<ZucchiniFormatException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));

        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanOperation_TruncatedElementTable_ThrowsZucchiniFormatException()
    {
        var blob = BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]);
        using var payload = Payload(Brotli(blob.AsSpan(0, ZucchiniConstants.HeaderByteSize + 4).ToArray()));
        var operation = ZucchiniOperation((ulong)payload.Length);

        var exception = Assert.Throws<ZucchiniFormatException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));

        Assert.Contains("Truncated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanOperation_TruncatedBrotliContainer_ThrowsZucchiniFormatException()
    {
        var blob = Brotli(BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]));
        using var payload = Payload(blob.AsSpan(0, blob.Length / 2).ToArray());
        var operation = ZucchiniOperation((ulong)payload.Length);

        var exception = Assert.Throws<ZucchiniFormatException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));

        Assert.Contains("Brotli", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanOperation_DeclaredElementCountBeyondBudget_ThrowsZucchiniFormatException()
    {
        var blob = BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]);
        WriteUInt32(blob.AsSpan(24), (uint)(ZucchiniExecutableTypeScanner.MaximumDecodedBlobBytes
            / ZucchiniExecutableTypeScanner.MinimumElementBytes) + 1);
        using var payload = Payload(Brotli(blob));
        var operation = ZucchiniOperation((ulong)payload.Length);

        var exception = Assert.Throws<ZucchiniFormatException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));

        Assert.Contains("element count", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0UL, 999UL)]
    [InlineData(999UL, 1UL)]
    public void ScanOperation_BlobRangeOutsidePayload_ThrowsPayloadUnsupportedOperation(ulong dataOffset, ulong dataLength)
    {
        var blob = Brotli(BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]));
        using var payload = Payload(blob);
        var operation = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Zucchini,
            DataOffset = dataOffset,
            DataLength = dataLength
        };

        var exception = Assert.Throws<PayloadUnsupportedOperationException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));

        Assert.Equal(InstallOperation.Types.Type.Zucchini.ToString(), exception.OperationType);
    }

    [Fact]
    public void ScanOperation_OffsetOverflow_ThrowsPayloadUnsupportedOperation()
    {
        var blob = Brotli(BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]));
        using var payload = Payload(blob);
        var operation = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Zucchini,
            DataOffset = ulong.MaxValue,
            DataLength = 1
        };

        Assert.Throws<PayloadUnsupportedOperationException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperation(payload, operation, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ScanOperations_MixedSequentialOperations_OnlyReadsZucchiniBlobs()
    {
        var zucchini = Brotli(BuildPatch([(ExeType: UnsupportedExeType, NewOffset: 0u)]));
        var unrelated = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var payload = Payload([.. zucchini, .. unrelated]);
        var copy = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            DataOffset = (ulong)zucchini.Length,
            DataLength = (ulong)unrelated.Length
        };
        var operations = new[] { copy, ZucchiniOperation((ulong)zucchini.Length) };

        Assert.True(ZucchiniExecutableTypeScanner.HasZucchiniOperation(operations));
        var exception = Assert.Throws<ZucchiniUnsupportedExecutableTypeException>(() =>
            ZucchiniExecutableTypeScanner.ScanOperations(payload, operations, 0, TestContext.Current.CancellationToken));

        Assert.Equal(UnsupportedExeType, exception.ExeType);
    }

    [Fact]
    public void ScanOperations_NonSeekablePayload_SkipsScanWithoutReading()
    {
        var blob = Brotli(BuildPatch([(ExeType: UnsupportedExeType, NewOffset: 0u)]));
        using var forward = new NonSeekableReadStream(blob);
        var operations = new[] { ZucchiniOperation((ulong)blob.Length) };

        ZucchiniExecutableTypeScanner.ScanOperations(forward, operations, 0, TestContext.Current.CancellationToken);

        Assert.Equal(0, forward.BytesRead);
    }

    [Fact]
    public void HasZucchiniOperation_NoZucchiniOperations_ReportsFalse()
    {
        var operations = new[]
        {
            new InstallOperation { Type = InstallOperation.Types.Type.Replace },
            new InstallOperation { Type = InstallOperation.Types.Type.SourceCopy },
            new InstallOperation { Type = InstallOperation.Types.Type.Puffdiff }
        };

        Assert.False(ZucchiniExecutableTypeScanner.HasZucchiniOperation(operations));
    }

    [Fact]
    public void Extract_NonRawZucchiniPartition_RejectsBeforeCreatingAnyOutput()
    {
        var blob = Brotli(BuildPatch([(ExeType: UnsupportedExeType, NewOffset: 0u)]));
        var operation = ZucchiniInstallOperation(OldData, blob);
        var partition = new PartitionUpdate { PartitionName = "system" };
        partition.Operations.Add(operation);

        using var temp = new TempDirectory();
        var outputDirectory = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(outputDirectory);
        var payload = WritePayload(temp, partition, operation, blob);
        var basePath = temp.WriteBytes("base.img", OldData);

        var exception = Assert.Throws<ZucchiniUnsupportedExecutableTypeException>(() =>
            PayloadParser.Extract(payload, ["system"], outputDirectory,
                sourceImages: new Dictionary<string, string> { ["system"] = basePath }));

        Assert.Equal(UnsupportedExeType, exception.ExeType);
        Assert.Empty(Directory.GetFiles(outputDirectory));
    }

    [Fact]
    public async Task ExtractAsync_NonRawZucchiniPartition_LeavesNoTemporaryOutputBehind()
    {
        var blob = Brotli(BuildPatch([(ExeType: UnsupportedExeType, NewOffset: 0u)]));
        var operation = ZucchiniInstallOperation(OldData, blob);
        var partition = new PartitionUpdate
        {
            PartitionName = "system",
            OldPartitionInfo = WholeImageInfo(OldData)
        };
        partition.Operations.Add(operation);

        using var temp = new TempDirectory();
        var outputDirectory = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(outputDirectory);
        var payload = WritePayload(temp, partition, operation, blob);
        var basePath = temp.WriteBytes("base.img", OldData);

        await Assert.ThrowsAsync<ZucchiniUnsupportedExecutableTypeException>(() =>
            PayloadParser.ExtractAsync(payload, ["system"], outputDirectory,
                cancellationToken: TestContext.Current.CancellationToken,
                sourceImages: new Dictionary<string, string> { ["system"] = basePath }));

        Assert.Empty(Directory.GetFiles(outputDirectory));
    }

    [Fact]
    public void Extract_RawZucchiniPartition_StillExtractsTargetImage()
    {
        var blob = Brotli(BuildPatch([(ExeType: (uint)ZucchiniExecutableType.Raw, NewOffset: 0u)]));
        var operation = ZucchiniInstallOperation(OldData, blob);
        var partition = new PartitionUpdate
        {
            PartitionName = "system",
            OldPartitionInfo = WholeImageInfo(OldData),
            NewPartitionInfo = WholeImageInfo(NewData)
        };
        partition.Operations.Add(operation);

        using var temp = new TempDirectory();
        var outputDirectory = Path.Combine(temp.Path, "out");
        var payload = WritePayload(temp, partition, operation, blob);
        var basePath = temp.WriteBytes("base.img", OldData);

        PayloadParser.Extract(payload, ["system"], outputDirectory,
            sourceImages: new Dictionary<string, string> { ["system"] = basePath });

        Assert.Equal(NewData, File.ReadAllBytes(Path.Combine(outputDirectory, "system.img")));
    }

    private static PartitionInfo WholeImageInfo(byte[] image)
        => new() { Size = (ulong)image.Length, Hash = ByteString.CopyFrom(SHA256.HashData(image)) };

    private static InstallOperation ZucchiniOperation(ulong dataLength) => new()
    {
        Type = InstallOperation.Types.Type.Zucchini,
        DataOffset = 0,
        DataLength = dataLength
    };

    private static InstallOperation ZucchiniInstallOperation(byte[] oldData, byte[] blob)
    {
        var operation = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Zucchini,
            SrcLength = (ulong)oldData.Length,
            DstLength = (ulong)NewData.Length,
            DataOffset = 0,
            DataLength = (ulong)blob.Length,
            DataSha256Hash = ByteString.CopyFrom(SHA256.HashData(blob)),
            SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData(oldData))
        };
        operation.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = (ulong)(oldData.Length / BlockSize) });
        operation.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = (ulong)(NewData.Length / BlockSize) });
        return operation;
    }

    private static string WritePayload(TempDirectory temp, PartitionUpdate partition, InstallOperation operation, byte[] blob)
    {
        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        manifest.Partitions.Add(partition);
        return temp.WriteBytes("payload.bin",
            PayloadDeltaTests.BuildPayload(manifest, new Dictionary<InstallOperation, byte[]> { [operation] = blob }));
    }

    private static MemoryStream Payload(byte[] bytes) => new(bytes, writable: false);

    private static byte[] Brotli(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var encoder = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            encoder.Write(raw);
        }

        return output.ToArray();
    }

    private static byte[] BuildPatch(IReadOnlyList<(uint ExeType, uint NewOffset)> elements)
    {
        var sourceSkips = new List<byte>();
        var targetSkips = new List<byte>();
        var lengths = new List<byte>();
        var extra = new List<byte>();
        var deltaSkips = new List<byte>();
        var differences = new List<byte>();
        ZucchiniLeb128.Write(0, sourceSkips);
        ZucchiniLeb128.Write(0, targetSkips);
        ZucchiniLeb128.Write((uint)OldData.Length, lengths);
        uint previousDelta = 0;
        for (var index = 0; index < NewData.Length; index++)
        {
            var difference = unchecked((byte)(NewData[index] - OldData[index]));
            if (difference == 0)
            {
                continue;
            }

            ZucchiniLeb128.Write((uint)index - previousDelta, deltaSkips);
            differences.Add(difference);
            previousDelta = (uint)index + 1;
        }

        var patch = new List<byte>();
        WriteUInt32(patch, ZucchiniConstants.Magic);
        WriteUInt32(patch, ZucchiniConstants.Version);
        WriteUInt32(patch, (uint)OldData.Length);
        WriteUInt32(patch, Crc32(OldData));
        WriteUInt32(patch, (uint)NewData.Length);
        WriteUInt32(patch, Crc32(NewData));
        WriteUInt32(patch, (uint)elements.Count);
        foreach (var (exeType, newOffset) in elements)
        {
            WriteUInt32(patch, 0);
            WriteUInt32(patch, (uint)OldData.Length);
            WriteUInt32(patch, newOffset);
            WriteUInt32(patch, (uint)NewData.Length);
            WriteUInt32(patch, exeType);
            patch.Add(1);
            patch.Add(0);
            foreach (var stream in new[] { sourceSkips, targetSkips, lengths, extra, deltaSkips, differences, new List<byte>() })
            {
                WriteUInt32(patch, (uint)stream.Count);
                patch.AddRange(stream);
            }

            WriteUInt32(patch, 0);
        }

        return [.. patch];
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Append(data);
        return crc.GetCurrentHashAsUInt32();
    }

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)value);
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 24));
    }

    private static void WriteUInt32(Span<byte> buffer, uint value)
    {
        buffer[0] = (byte)value;
        buffer[1] = (byte)(value >> 8);
        buffer[2] = (byte)(value >> 16);
        buffer[3] = (byte)(value >> 24);
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        internal NonSeekableReadStream(byte[] data)
        {
            _data = data;
        }

        internal long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _data.Length || buffer.IsEmpty)
            {
                return 0;
            }

            var count = Math.Min(buffer.Length, _data.Length - _position);
            _data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}