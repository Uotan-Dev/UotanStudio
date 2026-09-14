using System.Security.Cryptography;
using BsDiff;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Firmware.CrossCutting.Compress;
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
            baseData.CopyTo(expected, 0);    // blocks 0-3 retain the base image (in-place delta semantics)
            baseData.CopyTo(expected, 16);   // SOURCE_COPY copies base blocks 0-3 into blocks 4-7
            newData.CopyTo(expected, 32);    // SOURCE_BSDIFF writes patched data into blocks 8-11

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
        var lz4Patch = PayloadLz4DiffTests.BuildStoredPatch(baseData.Length, bsdiffPatch);
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
            baseData.CopyTo(expected, 0);    // blocks 0-3 retain the base image (in-place delta semantics)
            newData.CopyTo(expected, 16);    // LZ4DIFF_BSDIFF writes patched data into blocks 4-7

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
        // ZUCCHINI is a source-dependent operation: it reads its source extents against the base
        // partition image, so a missing base must be rejected instead of silently reading zeros.
        // ZUCCHINI 是源依赖操作：其源区间从基座分区镜像解析，因此缺少基座时必须拒绝，
        // 而不是静默读取零填充数据。
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

        // Full source payload: partition "system" == baseData via a REPLACE operation.
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

        // Incremental payload: SOURCE_COPY from base blocks 0-3 into destination blocks 4-7.
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
            baseData.CopyTo(expected, 0);    // blocks 0-3 retain the base image (in-place delta semantics)
            baseData.CopyTo(expected, 16);   // SOURCE_COPY copies base blocks 0-3 into blocks 4-7

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
        // Capture the (operation, data) pairs BEFORE mutating any op field: InstallOperation is a
        // protobuf message whose GetHashCode depends on field values, so using the dictionary after
        // setting DataOffset/DataLength/DataSha256Hash would fail lookups.
        // 在修改任何 op 字段之前先捕获 (操作, 数据) 对：InstallOperation 是 protobuf 消息，
        // GetHashCode 依赖字段值，设置 DataOffset/DataLength/DataSha256Hash 后再查字典会失败。
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
        // 与 PayloadBuilder 保持一致：元数据签名段固定 32 字节（零填充），Header 后紧跟
        // manifest，随后签名段，最后是数据块；BaseOffset = HeaderSize + Manifest + 签名段。
        // Match PayloadBuilder: a fixed 32-byte (zero-filled) metadata signature section follows
        // the header and manifest; BaseOffset = HeaderSize + ManifestLength + MetadataSignatureLength.
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