using System.Buffers.Binary;
using System.Security.Cryptography;
using BsDiff;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Incremental (delta) OTA payload tests: source base injection (SOURCE_COPY/SOURCE_BSDIFF against
/// a base image), LZ4DIFF_BSDIFF patch application and the ZUCCHINI base-image requirement.
/// <para>增量 OTA 载荷测试：源基座注入（SOURCE_COPY/SOURCE_BSDIFF 对基座镜像应用）、
/// LZ4DIFF_BSDIFF 补丁应用与 ZUCCHINI 基座镜像要求。</para>
/// </summary>
public class PayloadDeltaTests
{
    private const uint BlockSize = 4;

    [Fact]
    public void SourceCopyAndSourceBsdiff_WithBaseImage_ExtractsCorrectly()
    {
        var baseData = new byte[16];
        var newData = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            baseData[i] = (byte)(0x10 + i);
            newData[i] = (byte)(0xA0 + i);
        }

        var patch = CreateBsdiffPatch(baseData, newData);
        Assert.NotEmpty(patch);

        var copyOp = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            SrcLength = (ulong)baseData.Length,
            DstLength = (ulong)baseData.Length
        };
        copyOp.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 4 });
        copyOp.DstExtents.Add(new Extent { StartBlock = 4, NumBlocks = 4 });

        var bsdiffOp = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceBsdiff,
            SrcLength = (ulong)baseData.Length,
            DstLength = (ulong)newData.Length,
            SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData(baseData))
        };
        bsdiffOp.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 4 });
        bsdiffOp.DstExtents.Add(new Extent { StartBlock = 8, NumBlocks = 4 });

        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        var partition = new PartitionUpdate { PartitionName = "system" };
        partition.Operations.Add(copyOp);
        partition.Operations.Add(bsdiffOp);
        manifest.Partitions.Add(partition);

        var payload = BuildPayload(manifest, new Dictionary<InstallOperation, byte[]> { [bsdiffOp] = patch });
        var payloadPath = WriteTempFile(payload);
        var basePath = WriteTempFile(baseData);
        var outputDir = CreateTempDir();
        try
        {
            PayloadParser.Extract(payloadPath, new[] { "system" }, outputDir,
                sourceImages: new Dictionary<string, string> { ["system"] = basePath });

            var output = File.ReadAllBytes(Path.Combine(outputDir, "system.img"));
            var expected = new byte[48];
            baseData.CopyTo(expected, 0);
            baseData.CopyTo(expected, 16);
            newData.CopyTo(expected, 32);

            Assert.Equal(expected, output);
        }
        finally
        {
            File.Delete(payloadPath);
            File.Delete(basePath);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SourceCopyWithoutBase_ThrowsPartitionExtractionException()
    {
        var baseData = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();

        var copyOp = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            SrcLength = (ulong)baseData.Length,
            DstLength = (ulong)baseData.Length
        };
        copyOp.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 4 });
        copyOp.DstExtents.Add(new Extent { StartBlock = 4, NumBlocks = 4 });

        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        var partition = new PartitionUpdate { PartitionName = "system" };
        partition.Operations.Add(copyOp);
        manifest.Partitions.Add(partition);

        var payload = BuildPayload(manifest, new Dictionary<InstallOperation, byte[]>());
        var payloadPath = WriteTempFile(payload);
        var outputDir = CreateTempDir();
        try
        {
            var ex = Assert.Throws<PartitionExtractionException>(() =>
                PayloadParser.Extract(payloadPath, new[] { "system" }, outputDir));
            Assert.Equal("system", ex.PartitionName);
        }
        finally
        {
            File.Delete(payloadPath);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    private sealed record StoredBlock(ulong Offset, ulong Length, ulong Stored);

    private static byte[] BuildStoredLz4Patch(int length, byte[] inner)
    {
        var blocks = new[] { new StoredBlock(0, (ulong)length, (ulong)length) };
        var header = Message(writer =>
        {
            Bytes(writer, 1, Info(blocks));
            Bytes(writer, 2, Info(blocks));
        });
        var patch = new byte[16 + header.Length + inner.Length];
        "LZ4DIFF"u8.CopyTo(patch);
        BinaryPrimitives.WriteUInt32BigEndian(patch.AsSpan(7), 1);
        BinaryPrimitives.WriteUInt32BigEndian(patch.AsSpan(11), (uint)header.Length);
        header.CopyTo(patch, 16);
        inner.CopyTo(patch, 16 + header.Length);
        return patch;
    }

    private static byte[] Info(StoredBlock[] blocks) => Message(writer =>
    {
        Bytes(writer, 1, Message(child =>
        {
            Number(child, 1, 1);
            Number(child, 2, 9);
        }));
        foreach (var block in blocks)
        {
            Bytes(writer, 2, Message(child =>
            {
                Number(child, 1, block.Offset);
                Number(child, 2, block.Length);
                Number(child, 3, block.Stored);
            }));
        }
        Number(writer, 3, 0);
    });

    private static void Number(CodedOutputStream writer, int field, ulong value)
    {
        writer.WriteTag(field, WireFormat.WireType.Varint);
        writer.WriteUInt64(value);
    }

    private static void Bytes(CodedOutputStream writer, int field, byte[] value)
    {
        writer.WriteTag(field, WireFormat.WireType.LengthDelimited);
        writer.WriteBytes(ByteString.CopyFrom(value));
    }

    private static byte[] Message(Action<CodedOutputStream> write)
    {
        using var result = new MemoryStream();
        using (var writer = new CodedOutputStream(result, true))
        {
            write(writer);
        }
        return result.ToArray();
    }

    [Fact]
    public void Lz4diffBsdiff_WithBaseImage_ExtractsCorrectly()
    {
        var baseData = new byte[16];
        var newData = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            baseData[i] = (byte)(0x20 + i);
            newData[i] = (byte)(0xB0 + i);
        }

        var bsdiffPatch = CreateBsdiffPatch(baseData, newData);
        Assert.NotEmpty(bsdiffPatch);
        var lz4Patch = BuildStoredLz4Patch(baseData.Length, bsdiffPatch);
        Assert.NotEmpty(lz4Patch);

        var op = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Lz4diffBsdiff,
            SrcLength = (ulong)baseData.Length,
            DstLength = (ulong)newData.Length,
            SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData(baseData))
        };
        op.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 4 });
        op.DstExtents.Add(new Extent { StartBlock = 4, NumBlocks = 4 });

        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        var partition = new PartitionUpdate { PartitionName = "system" };
        partition.Operations.Add(op);
        manifest.Partitions.Add(partition);

        var payload = BuildPayload(manifest, new Dictionary<InstallOperation, byte[]> { [op] = lz4Patch });
        var payloadPath = WriteTempFile(payload);
        var basePath = WriteTempFile(baseData);
        var outputDir = CreateTempDir();
        try
        {
            using var payloadStream = File.OpenRead(payloadPath);
            PayloadParser.Extract(payloadStream, new[] { "system" }, outputDir,
                sourceImages: new Dictionary<string, string> { ["system"] = basePath });

            var output = File.ReadAllBytes(Path.Combine(outputDir, "system.img"));
            var expected = new byte[32];
            baseData.CopyTo(expected, 0);
            newData.CopyTo(expected, 16);

            Assert.Equal(expected, output);
        }
        finally
        {
            File.Delete(payloadPath);
            File.Delete(basePath);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Zucchini_WithoutBaseImage_ThrowsPartitionExtractionException()
    {
        var op = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Zucchini,
            DstLength = 4 * BlockSize
        };
        op.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 4 });

        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        var partition = new PartitionUpdate { PartitionName = "system" };
        partition.Operations.Add(op);
        manifest.Partitions.Add(partition);

        var payload = BuildPayload(manifest, new Dictionary<InstallOperation, byte[]>());
        var payloadPath = WriteTempFile(payload);
        var outputDir = CreateTempDir();
        try
        {
            var ex = Assert.Throws<PartitionExtractionException>(() =>
            {
                using var payloadStream = File.OpenRead(payloadPath);
                PayloadParser.Extract(payloadStream, new[] { "system" }, outputDir);
            });
            Assert.Equal("system", ex.PartitionName);
        }
        finally
        {
            File.Delete(payloadPath);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SourcePayloadPath_ProvidesBaseImageForIncrementalPayload()
    {
        var baseData = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            baseData[i] = (byte)(0x30 + i);
        }

        var replaceOp = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Replace,
            DstLength = (ulong)baseData.Length
        };
        replaceOp.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 4 });

        var sourceManifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        var sourcePartition = new PartitionUpdate { PartitionName = "system" };
        sourcePartition.Operations.Add(replaceOp);
        sourceManifest.Partitions.Add(sourcePartition);
        var sourcePayload = BuildPayload(sourceManifest, new Dictionary<InstallOperation, byte[]> { [replaceOp] = baseData });

        var copyOp = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            SrcLength = (ulong)baseData.Length,
            DstLength = (ulong)baseData.Length
        };
        copyOp.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 4 });
        copyOp.DstExtents.Add(new Extent { StartBlock = 4, NumBlocks = 4 });

        var deltaManifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        var deltaPartition = new PartitionUpdate { PartitionName = "system" };
        deltaPartition.Operations.Add(copyOp);
        deltaManifest.Partitions.Add(deltaPartition);
        var deltaPayload = BuildPayload(deltaManifest, new Dictionary<InstallOperation, byte[]>());

        var sourcePayloadPath = WriteTempFile(sourcePayload);
        var deltaPayloadPath = WriteTempFile(deltaPayload);
        var outputDir = CreateTempDir();
        try
        {
            PayloadParser.Extract(deltaPayloadPath, new[] { "system" }, outputDir, sourcePayloadPath: sourcePayloadPath);

            var output = File.ReadAllBytes(Path.Combine(outputDir, "system.img"));
            var expected = new byte[32];
            baseData.CopyTo(expected, 0);
            baseData.CopyTo(expected, 16);

            Assert.Equal(expected, output);
        }
        finally
        {
            File.Delete(sourcePayloadPath);
            File.Delete(deltaPayloadPath);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    internal static byte[] BuildPayload(DeltaArchiveManifest manifest, IReadOnlyDictionary<InstallOperation, byte[]> opData)
    {
        var dataOps = new List<(InstallOperation Op, byte[] Data)>();
        foreach (var p in manifest.Partitions)
        {
            foreach (var op in p.Operations)
            {
                if (opData.TryGetValue(op, out var bytes))
                {
                    dataOps.Add((op, bytes));
                }
            }
        }

        ulong dataOffset = 0;
        foreach (var (op, bytes) in dataOps)
        {
            op.DataOffset = dataOffset;
            op.DataLength = (ulong)bytes.Length;
            op.DataSha256Hash = ByteString.CopyFrom(SHA256.HashData(bytes));
            dataOffset = checked(dataOffset + (ulong)bytes.Length);
        }

        var manifestBytes = manifest.ToByteArray();
        const uint metadataSignatureLength = 32;
        var header = PayloadHeaderBuilder.BuildHeader((ulong)manifestBytes.Length, metadataSignatureLength);
        using var ms = new MemoryStream();
        ms.Write(header, 0, header.Length);
        ms.Write(manifestBytes, 0, manifestBytes.Length);
        ms.Write(new byte[metadataSignatureLength]);
        foreach (var (_, bytes) in dataOps)
        {
            ms.Write(bytes, 0, bytes.Length);
        }

        return ms.ToArray();
    }

    private static byte[] CreateBsdiffPatch(byte[] oldData, byte[] newData)
    {
        using var patchStream = new MemoryStream();
        BinaryPatch.Create(oldData, newData, patchStream);
        return patchStream.ToArray();
    }

    private static string WriteTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-payload-delta-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uotan-payload-delta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}