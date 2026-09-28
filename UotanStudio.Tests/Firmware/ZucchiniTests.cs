using System.IO.Hashing;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Diff.Zucchini;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks Chromium NoOp wire semantics and Brotli-wrapped Payload integration.</summary>
public class ZucchiniTests
{
    private const uint BlockSize = 4;

    [Fact]
    public void CreateRawPatch_WithMovedEquivalences_ReplaysThroughIndependentApplier()
    {
        var oldData = "abcdefghij0123456789"u8.ToArray();
        var newData = "12abXYefghZZ"u8.ToArray();
        var patch = ZucchiniPatchApplier.CreateRawPatch(oldData, newData,
        [
            new(10, 0, 2),
            new(0, 2, 2),
            new(4, 6, 4),
        ], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(newData, ZucchiniPatchApplier.Apply(oldData, patch, newData.Length));
        Assert.Equal("Zucc"u8.ToArray(), patch[..4]);
    }

    [Fact]
    public void CreateRawPatch_RejectsOverlappingOrOutOfOrderDestination()
    {
        var oldData = new byte[8];
        var newData = new byte[8];

        Assert.Throws<InvalidDataException>(() => ZucchiniPatchApplier.CreateRawPatch(oldData, newData,
            [new(0, 0, 4), new(4, 3, 2)], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_BackwardSourceSkipAndDeltaAfterGap_UsesCopiedByteOffsets()
    {
        var oldData = "0123456789"u8.ToArray(); var newData = "56XY02"u8.ToArray();
        var patch = BuildPatch(oldData, newData, [(Src: 5, Dst: 0, Len: 2), (Src: 0, Dst: 4, Len: 2)]);
        Assert.Equal(newData, ZucchiniPatchApplier.Apply(oldData, patch, newData.Length));
    }

    [Fact]
    public void Crc32_MatchesStandardCheckValue()
    {
        var crc = new Crc32();
        crc.Append("123456789"u8);
        Assert.Equal(0xCBF43926u, crc.GetCurrentHashAsUInt32());
    }

    [Fact]
    public void Apply_PureExtraInsertion_ProducesNewData()
    {
        var oldData = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var newData = Enumerable.Range(0, oldData.Length).Select(i => (byte)(0x50 + i)).ToArray();

        var patch = BuildPatch(oldData, newData, []);
        var result = ZucchiniPatchApplier.Apply(oldData, patch, newData.Length);

        Assert.Equal(newData, result);
    }

    [Fact]
    public void Apply_EquivalenceCopyPlusRawDelta_ProducesNewData()
    {
        var oldData = new byte[] { 0x55, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x20, 0x21, 0x22, 0x23 };
        var newData = new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0xAA, 0xBB, 0x22, 0x23 };

        var patch = BuildPatch(oldData, newData, [(Src: 0, Dst: 0, Len: 8), (Src: 10, Dst: 10, Len: 2)]);
        var result = ZucchiniPatchApplier.Apply(oldData, patch, newData.Length);

        Assert.Equal(newData, result);
    }

    [Fact]
    public void Apply_WrongCrc_ThrowsZucchiniChecksumException()
    {
        var oldData = new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17 };
        var newData = oldData.Select(b => (byte)(b ^ 0xFF)).ToArray();

        var patch = BuildPatch(oldData, newData, [(Src: 0, Dst: 0, Len: 8)]);
        patch[20] ^= 0xFF;

        var ex = Assert.Throws<ZucchiniChecksumException>(() =>
            ZucchiniPatchApplier.Apply(oldData, patch, newData.Length));
        Assert.Contains("CRC", ex.Message);
    }

    [Fact]
    public void Apply_WrongMagic_ThrowsZucchiniFormatException()
    {
        var oldData = new byte[] { 1, 2, 3, 4 };
        var newData = new byte[] { 5, 6, 7, 8 };

        var patch = BuildPatch(oldData, newData, []);
        patch[0] ^= 0xFF;

        var ex = Assert.Throws<ZucchiniFormatException>(() =>
            ZucchiniPatchApplier.Apply(oldData, patch, newData.Length));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_NonRawExecutableType_ThrowsZucchiniUnsupportedExecutableTypeException()
    {
        var oldData = new byte[] { 1, 2, 3, 4 };
        var newData = new byte[] { 5, 6, 7, 8 };

        var patch = BuildPatch(oldData, newData, [], exeType: (ZucchiniExecutableType)1);

        var ex = Assert.Throws<ZucchiniUnsupportedExecutableTypeException>(() =>
            ZucchiniPatchApplier.Apply(oldData, patch, newData.Length));
        Assert.Equal(1u, ex.ExeType);
        Assert.Equal(0u, ex.Offset);
        Assert.Contains("1", ex.Message);
        Assert.Contains("offset", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_TruncatedPatch_ThrowsZucchiniFormatException()
    {
        var oldData = new byte[] { 1, 2, 3, 4 };
        var newData = new byte[] { 5, 6, 7, 8 };

        var patch = BuildPatch(oldData, newData, []);
        Assert.Throws<ZucchiniFormatException>(() =>
            ZucchiniPatchApplier.Apply(oldData, patch.AsSpan(0, patch.Length - 3), newData.Length));
    }

    [Fact]
    public void ZucchiniOperation_WithBaseImage_ExtractsCorrectly()
    {
        var baseData = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            baseData[i] = (byte)(0x10 + i);
        }

        var newData = new byte[16];
        Array.Copy(baseData, newData, 8);
        for (var i = 8; i < 16; i++)
        {
            newData[i] = (byte)(0x40 + i);
        }

        var patch = BuildPatch(baseData, newData, [(Src: 0, Dst: 0, Len: 8)]);

        var op = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Zucchini,
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

        using var compressed = new MemoryStream();
        using (var brotli = new System.IO.Compression.BrotliStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true)) brotli.Write(patch);
        var payload = BuildPayload(manifest, new Dictionary<InstallOperation, byte[]> { [op] = compressed.ToArray() });
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
    public void ZucchiniOperation_WithoutBaseImage_ThrowsPartitionExtractionException()
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

    private static byte[] BuildPatch(byte[] oldData, byte[] newData,
        IReadOnlyList<(uint Src, uint Dst, uint Len)> equivalences,
        ZucchiniExecutableType exeType = ZucchiniExecutableType.Raw)
    {
        var sourceSkips = new List<byte>(); var targetSkips = new List<byte>(); var lengths = new List<byte>();
        var extra = new List<byte>(); var deltaSkips = new List<byte>(); var differences = new List<byte>();
        int sourceEnd = 0; uint targetEnd = 0; uint copied = 0; uint previousDelta = 0;
        foreach (var (source, target, length) in equivalences)
        {
            var delta = checked((int)source - sourceEnd);
            ZucchiniLeb128.Write(unchecked((uint)((delta << 1) ^ (delta >> 31))), sourceSkips);
            ZucchiniLeb128.Write(target - targetEnd, targetSkips);
            ZucchiniLeb128.Write(length, lengths);
            for (var i = targetEnd; i < target; i++) extra.Add(newData[i]);
            for (uint i = 0; i < length; i++)
            {
                var difference = unchecked((byte)(newData[target + i] - oldData[source + i]));
                if (difference == 0) continue;
                ZucchiniLeb128.Write(copied + i - previousDelta, deltaSkips);
                differences.Add(difference); previousDelta = copied + i + 1;
            }
            copied += length; targetEnd = target + length; sourceEnd = checked((int)(source + length));
        }
        for (var i = targetEnd; i < newData.Length; i++) extra.Add(newData[i]);
        var patch = new List<byte>();
        WriteUInt32(0x6363755A, patch); WriteUInt32(2, patch);
        WriteUInt32((uint)oldData.Length, patch); WriteUInt32(Crc32(oldData), patch);
        WriteUInt32((uint)newData.Length, patch); WriteUInt32(Crc32(newData), patch); WriteUInt32(1, patch);
        WriteUInt32(0, patch); WriteUInt32((uint)oldData.Length, patch);
        WriteUInt32(0, patch); WriteUInt32((uint)newData.Length, patch); WriteUInt32((uint)exeType, patch);
        patch.Add(1); patch.Add(0);
        foreach (var stream in new[] { sourceSkips, targetSkips, lengths, extra, deltaSkips, differences, new List<byte>() })
        { WriteUInt32((uint)stream.Count, patch); patch.AddRange(stream); }
        WriteUInt32(0, patch);
        return patch.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Append(data);
        return crc.GetCurrentHashAsUInt32();
    }

    private static void WriteUInt32(uint value, List<byte> buffer)
    {
        buffer.Add((byte)value);
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 24));
    }

    private static byte[] BuildPayload(DeltaArchiveManifest manifest, IReadOnlyDictionary<InstallOperation, byte[]> opData)
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

    private static string WriteTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-zucchini-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uotan-zucchini-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}