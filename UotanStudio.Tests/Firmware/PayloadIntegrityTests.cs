using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.Payload;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class PayloadIntegrityTests
{
    [Fact]
    public void SourceCopy_ReadsOriginalBaseAfterReplaceOverwritesSameRange()
    {
        var replace = Operation(InstallOperation.Types.Type.Replace, 0);
        var copy = Operation(InstallOperation.Types.Type.SourceCopy, 1);
        copy.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
        copy.SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData("base"u8));
        var partition = new PartitionUpdate { PartitionName = "system", OldPartitionInfo = Info("baseold!"u8.ToArray()), NewPartitionInfo = Info("new!base"u8.ToArray()) };
        partition.Operations.Add(replace); partition.Operations.Add(copy);
        using var temp = new TempDirectory();
        Extract(temp, partition, "baseold!"u8.ToArray(), new Dictionary<InstallOperation, byte[]> { [replace] = "new!"u8.ToArray() });
        Assert.Equal("new!base"u8.ToArray(), File.ReadAllBytes(Path.Combine(temp.Path, "system.img")));
    }

    [Fact]
    public void OldPartitionHash_IsParsedFromWireAndValidated()
    {
        var copy = Operation(InstallOperation.Types.Type.SourceCopy, 0);
        copy.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
        var partition = new PartitionUpdate { PartitionName = "system", OldPartitionInfo = Info("good"u8.ToArray()) };
        partition.Operations.Add(copy);
        using var temp = new TempDirectory(); temp.WriteBytes("system.img", "existing"u8.ToArray());
        Assert.Throws<PartitionExtractionException>(() => Extract(temp, partition, "bad!"u8.ToArray(), new Dictionary<InstallOperation, byte[]>()));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(Path.Combine(temp.Path, "system.img")));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(65535)]
    public void UnknownOperation_IsRejectedBeforeCommit(int operationType)
    {
        var partition = new PartitionUpdate { PartitionName = "system" };
        partition.Operations.Add(Operation((InstallOperation.Types.Type)operationType, 0));
        using var temp = new TempDirectory(); temp.WriteBytes("system.img", "existing"u8.ToArray());
        Assert.Throws<PayloadUnsupportedOperationException>(() => Extract(temp, partition, null, new Dictionary<InstallOperation, byte[]>()));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(Path.Combine(temp.Path, "system.img")));
    }

    private static InstallOperation Operation(InstallOperation.Types.Type type, ulong target)
    { var op = new InstallOperation { Type = type }; op.DstExtents.Add(new Extent { StartBlock = target, NumBlocks = 1 }); return op; }
    private static PartitionInfo Info(byte[] data) => new() { Size = (ulong)data.Length, Hash = ByteString.CopyFrom(SHA256.HashData(data)) };
    private static void Extract(TempDirectory temp, PartitionUpdate partition, byte[]? original, Dictionary<InstallOperation, byte[]> data)
    {
        var manifest = new DeltaArchiveManifest { BlockSize = 4, MinorVersion = 9 }; manifest.Partitions.Add(partition);
        var payload = temp.WriteBytes("payload.bin", PayloadDeltaTests.BuildPayload(manifest, data));
        var images = original is null ? null : new Dictionary<string, string> { ["system"] = temp.WriteBytes("base.img", original) };
        PayloadParser.Extract(payload, ["system"], temp.Path, sourceImages: images);
    }
}