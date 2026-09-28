using System.Security.Cryptography;
using BsDiff;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// 多区间（multi-extent）操作语义测试，对齐上游 delta_performer：源区间拼接为一条数据流
/// 整体校验哈希后按顺序写入全部目标区间。
/// <para>Multi-extent operation semantics tests, mirroring upstream delta_performer: source
/// extents concatenate into one data stream, verified as a whole, then written sequentially
/// across ALL destination extents.</para>
/// </summary>
public class PayloadMultiExtentTests
{
    private const uint BlockSize = 4;

    [Fact]
    public void SourceCopy_MultipleNonContiguousSourceExtents_WritesSequentially()
    {
        var baseData = new byte[24];
        for (var i = 0; i < baseData.Length; i++)
        {
            baseData[i] = (byte)(0x10 + i);
        }

        var op = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            SrcLength = 8,
            DstLength = 8,
            SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData(baseData[0..4].Concat(baseData[12..16]).ToArray()))
        };
        op.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
        op.SrcExtents.Add(new Extent { StartBlock = 3, NumBlocks = 1 });
        op.DstExtents.Add(new Extent { StartBlock = 4, NumBlocks = 2 });

        var output = Extract(new[] { op }, baseData, []);

        var expected = (byte[])baseData.Clone();
        baseData[0..4].CopyTo(expected, 16);
        baseData[12..16].CopyTo(expected, 20);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void SourceCopy_MultipleDestinationExtents_MapsSequentially()
    {
        var baseData = new byte[28];
        for (var i = 0; i < baseData.Length; i++)
        {
            baseData[i] = (byte)(0x20 + i);
        }

        var op = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            SrcLength = 8,
            DstLength = 8
        };
        op.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
        op.SrcExtents.Add(new Extent { StartBlock = 3, NumBlocks = 1 });
        op.DstExtents.Add(new Extent { StartBlock = 4, NumBlocks = 1 });
        op.DstExtents.Add(new Extent { StartBlock = 6, NumBlocks = 1 });

        var output = Extract(new[] { op }, baseData, []);

        var expected = (byte[])baseData.Clone();
        baseData[0..4].CopyTo(expected, 16);
        baseData[12..16].CopyTo(expected, 24);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void SourceBsdiff_MultipleDestinationExtents_MapsSequentially()
    {
        var baseData = new byte[28];
        for (var i = 0; i < baseData.Length; i++)
        {
            baseData[i] = (byte)(0x30 + i);
        }

        var oldData = baseData[0..8];
        var newData = new byte[8];
        for (var i = 0; i < newData.Length; i++)
        {
            newData[i] = (byte)(0xC0 + i);
        }

        var patch = CreateBsdiffPatch(oldData, newData);

        var op = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceBsdiff,
            SrcLength = 8,
            DstLength = 8,
            SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData(oldData))
        };
        op.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 2 });
        op.DstExtents.Add(new Extent { StartBlock = 4, NumBlocks = 1 });
        op.DstExtents.Add(new Extent { StartBlock = 6, NumBlocks = 1 });

        var output = Extract(new[] { op }, baseData, [new KeyValuePair<InstallOperation, byte[]>(op, patch)]);

        var expected = (byte[])baseData.Clone();
        newData[0..4].CopyTo(expected, 16);
        newData[4..8].CopyTo(expected, 24);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Bsdiff_MultipleDestinationExtents_MapsSequentially()
    {
        var baseData = new byte[24];
        for (var i = 0; i < baseData.Length; i++)
        {
            baseData[i] = (byte)(0x40 + i);
        }

        var oldData = baseData[0..8];
        var newData = Enumerable.Range(0, 8).Select(static i => (byte)(0xD0 + i)).ToArray();
        var patch = CreateBsdiffPatch(oldData, newData);

        var op = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Bsdiff,
            SrcLength = 8,
            DstLength = 8
        };
        op.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 2 });
        op.DstExtents.Add(new Extent { StartBlock = 3, NumBlocks = 1 });
        op.DstExtents.Add(new Extent { StartBlock = 5, NumBlocks = 1 });

        var output = Extract(new[] { op }, baseData, [new KeyValuePair<InstallOperation, byte[]>(op, patch)]);

        var expected = (byte[])baseData.Clone();
        newData[0..4].CopyTo(expected, 12);
        newData[4..8].CopyTo(expected, 20);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void SourceCopy_PatchLongerThanDestination_Throws()
    {
        var baseData = new byte[16];
        var oldData = baseData[0..8];
        var newData = new byte[12];
        var patch = CreateBsdiffPatch(oldData, newData);

        var op = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceBsdiff,
            SrcLength = 8,
            DstLength = 8
        };
        op.SrcExtents.Add(new Extent { StartBlock = 0, NumBlocks = 2 });
        op.DstExtents.Add(new Extent { StartBlock = 2, NumBlocks = 2 });

        Assert.ThrowsAny<Exception>(() => Extract(new[] { op }, baseData, [new KeyValuePair<InstallOperation, byte[]>(op, patch)]));
    }

    private static byte[] Extract(
        InstallOperation[] ops,
        byte[] baseData,
        IReadOnlyList<KeyValuePair<InstallOperation, byte[]>> opData)
    {
        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        var partition = new PartitionUpdate { PartitionName = "system" };
        foreach (var op in ops)
        {
            partition.Operations.Add(op);
        }

        manifest.Partitions.Add(partition);

        var payload = BuildPayload(manifest, opData);
        var payloadPath = WriteTempFile(payload);
        var basePath = WriteTempFile(baseData);
        var outputDir = CreateTempDir();
        try
        {
            PayloadParser.Extract(payloadPath, new[] { "system" }, outputDir,
                sourceImages: new Dictionary<string, string> { ["system"] = basePath });

            return File.ReadAllBytes(Path.Combine(outputDir, "system.img"));
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

    private static byte[] BuildPayload(DeltaArchiveManifest manifest, IReadOnlyList<KeyValuePair<InstallOperation, byte[]>> opData)
    {
        var dataOps = new List<(InstallOperation Op, byte[] Data)>();
        foreach (var p in manifest.Partitions)
        {
            foreach (var op in p.Operations)
            {
                var match = opData.FirstOrDefault(kv => ReferenceEquals(kv.Key, op));
                if (match.Key != null && match.Value != null)
                {
                    dataOps.Add((op, match.Value));
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
        var path = Path.Combine(Path.GetTempPath(), $"uotan-payload-multi-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uotan-payload-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}