using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PayloadZstdOperationTests
{
    private const uint BlockSize = 4096;

    private static readonly byte[] CliZstdFrame = Convert.FromBase64String(
        "KLUv/WQAH1UIAAQQCzBVep/E6Q4zWH2ix+wRNluApcrvFDleg6jN8hc8YYar0PUaP2SJrtP4HUJnjLHW+yBFao+02f4jSG2St9wBJktwlbrfBClOc5i94gcsUXabwOUKL1R5nsPoDTJXfKHG6xA1Wn+kye4TOF2Cp8zxFjtgharP9Bk+Y4it0vccQWaLsNX6H0RpjrPY/SJHbJG22wAlSm+Uud4DKE1yl7zhBitQdZq/5AkuU3idwucMMVZ7oMXqDzRZfqPI7RI3XIGmy/AVOl+Eqc7zGD1ih6zR9htAZYqv1PkeQ2iNstf8IUZrkLXa/yRJbpO43QInTHGWu+AFKk90mb7jCC1Sd5zB5gEAAP0+0K8pZigdHA==");

    [Fact]
    public void ReplaceZstd_UsesExtensionOperationValueWithoutClaimingBuilderCompatibility()
    {
        Assert.Equal(14, (int)InstallOperation.Types.Type.ReplaceZstd);
    }

    [Fact]
    public void Build_ZstdPartition_RaisesMinorVersionToTenAndRoundTrips()
    {
        using var temp = new TempDirectory();
        var expected = Pattern(8192);
        var image = temp.WriteBytes("source.img", expected);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder()) builder.AddPartitionZstd("system", image).Build(payload);

        using (var stream = File.OpenRead(payload))
        {
            var header = new byte[24]; stream.ReadExactly(header);
            var manifestData = new byte[checked((int)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)))]; stream.ReadExactly(manifestData);
            Assert.Equal(10U, DeltaArchiveManifest.Parser.ParseFrom(manifestData).MinorVersion);
        }

        PayloadParser.Extract(payload, ["system"], temp.Path);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(temp.Path, "system.img")));
    }

    [Fact]
    public void Extract_CliGeneratedFrame_ProducesReferenceBytes()
    {
        using var temp = new TempDirectory();
        var expected = Pattern(8192);
        var payload = WritePayload(temp, CliZstdFrame, [new Extent { StartBlock = 0, NumBlocks = 2 }]);

        PayloadParser.Extract(payload, ["system"], temp.Path);

        var actual = File.ReadAllBytes(Path.Combine(temp.Path, "system.img"));
        Assert.Equal(expected, actual);
        Assert.Equal("1F5A16C4456F34C5459E4E66D8650D4DBB6B298810D0C21CEF6571DB21D69C81", Convert.ToHexString(SHA256.HashData(actual)));
    }

    [Fact]
    public void Extract_MultipleDestinationExtents_MapsSequentiallyAndPreservesHole()
    {
        using var temp = new TempDirectory();
        var payload = WritePayload(temp, CliZstdFrame,
        [
            new Extent { StartBlock = 0, NumBlocks = 1 },
            new Extent { StartBlock = 2, NumBlocks = 1 }
        ]);

        PayloadParser.Extract(payload, ["system"], temp.Path);

        var actual = File.ReadAllBytes(Path.Combine(temp.Path, "system.img"));
        Assert.Equal(Pattern(8192).AsSpan(0, 4096).ToArray(), actual.AsSpan(0, 4096).ToArray());
        Assert.Equal(new byte[4096], actual.AsSpan(4096, 4096).ToArray());
        Assert.Equal(Pattern(8192).AsSpan(4096, 4096).ToArray(), actual.AsSpan(8192, 4096).ToArray());
    }

    [Fact]
    public void Extract_InvalidStoredHash_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var payload = WritePayload(temp, CliZstdFrame, [new Extent { StartBlock = 0, NumBlocks = 2 }], storedHash: new byte[32]);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        Assert.Throws<PartitionExtractionException>(() => PayloadParser.Extract(payload, ["system"], temp.Path));

        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void Extract_TruncatedFrame_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var truncated = CliZstdFrame[..^8];
        var payload = WritePayload(temp, truncated, [new Extent { StartBlock = 0, NumBlocks = 2 }]);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        Assert.ThrowsAny<Exception>(() => PayloadParser.Extract(payload, ["system"], temp.Path));

        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extract_ConcatenatedFrameOrTrailingData_PreservesExistingOutput(bool concatenateFrame)
    {
        using var temp = new TempDirectory();
        var suffix = concatenateFrame ? CliZstdFrame : "trailing"u8.ToArray();
        byte[] stored = [.. CliZstdFrame, .. suffix];
        var payload = WritePayload(temp, stored, [new Extent { StartBlock = 0, NumBlocks = 2 }]);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        Assert.Throws<PartitionExtractionException>(() => PayloadParser.Extract(payload, ["system"], temp.Path));

        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void Extract_FrameAboveWindowLimit_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        byte[] oversizedWindowFrame = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x88, 0x01, 0x00, 0x00];
        var payload = WritePayload(temp, oversizedWindowFrame, [new Extent { StartBlock = 0, NumBlocks = 1 }]);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        Assert.Throws<PartitionExtractionException>(() => PayloadParser.Extract(payload, ["system"], temp.Path));

        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void Extract_FrameRequiringExternalDictionary_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        byte[] dictionaryFrame = [0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x00, 0x01, 0x01, 0x00, 0x00];
        var payload = WritePayload(temp, dictionaryFrame, [new Extent { StartBlock = 0, NumBlocks = 1 }]);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        Assert.Throws<PartitionExtractionException>(() => PayloadParser.Extract(payload, ["system"], temp.Path));

        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void Decode_CanceledAfterInputRead_StopsBeforeWriting()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var input = new CancelingReadStream(CliZstdFrame, cancellation);
        using var output = new MemoryStream();

        Assert.Throws<OperationCanceledException>(() => ZstdReplaceDecoder.Decode(input, output, cancellation.Token));

        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Extract_DecodedLengthDiffersFromExtents_PreservesExistingOutput(ulong destinationBlocks)
    {
        using var temp = new TempDirectory();
        var payload = WritePayload(temp, CliZstdFrame, [new Extent { StartBlock = 0, NumBlocks = destinationBlocks }]);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        Assert.ThrowsAny<Exception>(() => PayloadParser.Extract(payload, ["system"], temp.Path));

        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void Extract_OverflowingExtent_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var payload = WritePayload(temp, CliZstdFrame, [new Extent { StartBlock = ulong.MaxValue, NumBlocks = 2 }]);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        Assert.ThrowsAny<Exception>(() => PayloadParser.Extract(payload, ["system"], temp.Path));

        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task Extract_PreCanceled_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var payload = WritePayload(temp, CliZstdFrame, [new Extent { StartBlock = 0, NumBlocks = 2 }]);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PayloadParser.ExtractAsync(payload, ["system"], temp.Path, cancellation.Token));

        Assert.Equal("existing"u8.ToArray(), await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_PartitionExceedsConfiguredLimit_PreservesExistingOutput(bool declaredSize)
    {
        using var temp = new TempDirectory();
        var extents = declaredSize
            ? new[] { new Extent { StartBlock = 0, NumBlocks = 2 } }
            : new[] { new Extent { StartBlock = 0, NumBlocks = 3 } };
        var payload = WritePayload(temp, CliZstdFrame, extents, declaredSize: declaredSize ? 3UL * BlockSize : null);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        var exception = Assert.Throws<PartitionExtractionException>(() =>
            PayloadParser.Extract(payload, ["system"], temp.Path, maximumPartitionBytes: 2L * BlockSize));

        Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void Extract_NonzeroDestinationLengthDiffersFromExtents_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var payload = WritePayload(temp, CliZstdFrame,
            [new Extent { StartBlock = 0, NumBlocks = 2 }], destinationLength: BlockSize);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        var exception = Assert.Throws<PartitionExtractionException>(() =>
            PayloadParser.Extract(payload, ["system"], temp.Path));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task FormatHandler_MaximumOutputBytesRejectsOversizedPartitionBeforePublication()
    {
        using var temp = new TempDirectory();
        var payload = WritePayload(temp, CliZstdFrame,
            [new Extent { StartBlock = 0, NumBlocks = 2 }], declaredSize: 3UL * BlockSize);
        var output = temp.WriteBytes("system.img", "existing"u8.ToArray());

        var result = await new PayloadFormatHandler().UnpackAsync(payload, temp.Path,
            new UnpackOptions { MaximumOutputBytes = 2L * BlockSize }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("limit", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("existing"u8.ToArray(), await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    private static string WritePayload(TempDirectory temp, byte[] operationData, Extent[] extents,
        byte[]? storedHash = null, ulong? declaredSize = null, ulong? destinationLength = null)
    {
        var operation = new InstallOperation
        {
            Type = InstallOperation.Types.Type.ReplaceZstd,
            DataLength = (ulong)operationData.Length,
            DstLength = destinationLength
                ?? extents.Aggregate(0UL, (length, extent) => checked(length + checked(extent.NumBlocks * BlockSize))),
            DataSha256Hash = ByteString.CopyFrom(storedHash ?? SHA256.HashData(operationData))
        };
        operation.DstExtents.Add(extents);
        var partition = new PartitionUpdate { PartitionName = "system" };
        if (declaredSize is { } size)
        {
            partition.NewPartitionInfo = new PartitionInfo { Size = size };
        }
        partition.Operations.Add(operation);
        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        manifest.Partitions.Add(partition);
        var manifestBytes = manifest.ToByteArray();
        const uint signatureLength = 32;
        var header = PayloadHeaderBuilder.BuildHeader((ulong)manifestBytes.Length, signatureLength);
        using var payload = new MemoryStream();
        payload.Write(header);
        payload.Write(manifestBytes);
        payload.Write(new byte[signatureLength]);
        payload.Write(operationData);
        return temp.WriteBytes("payload.bin", payload.ToArray());
    }

    private static byte[] Pattern(int length) =>
        Enumerable.Range(0, length).Select(static i => (byte)(i * 37 + 11)).ToArray();

    private sealed class CancelingReadStream(byte[] content, CancellationTokenSource cancellation) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return read;
        }
    }
}