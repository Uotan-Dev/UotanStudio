using System.IO.Hashing;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.Payload;
using UotanStudio.Firmware.Payload.Core;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;
using UotanStudio.Firmware.Zucchini;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Zucchini binary delta patch (RAW element subset) tests. There is no real zucchini sample to
/// verify against, so the tests build patches with a minimal RAW patch writer that shares the exact
/// encoding conventions with <see cref="ZucchiniPatchApplier"/> (magic/version constants, LEB128
/// varints, element layout, equivalence/extra/raw-delta stream semantics). The tests are therefore a
/// self-consistent round-trip validation of those shared conventions; interoperability with upstream
/// zucchini patches is not exercised here.
/// <para>Zucchini 二进制差分补丁（RAW 元素子集）测试。由于没有真实 zucchini 样本可供核对，
/// 测试用一个最小 RAW 补丁写入器构造补丁，该写入器与 <see cref="ZucchiniPatchApplier"/> 共用
/// 完全相同的编码约定（魔数/版本常量、LEB128 varint、元素布局、等价/附加/原始差分流语义）。
/// 因此这些测试是对共享约定的自洽回环验证；与上游 zucchini 补丁的互通性不在此测试。</para>
/// </summary>
public class ZucchiniTests
{
    private const uint BlockSize = 4;

    [Fact]
    public void Crc32_MatchesStandardCheckValue()
    {
        // Guards the byte-order convention shared by the writer and the applier: the patch header
        // stores the standard CRC-32 (poly 0xEDB88320) value as a little-endian uint32, and both
        // sides compute it with System.IO.Hashing.Crc32.GetCurrentHashUInt32().
        // 守护写入器与应用器共享的字节序约定：补丁头部以 little-endian uint32 存储标准 CRC-32
        //（多项式 0xEDB88320）值，两侧均用 System.IO.Hashing.Crc32.GetCurrentHashUInt32() 计算。
        var crc = new Crc32();
        crc.Append("123456789"u8);
        Assert.Equal(0xCBF43926u, crc.GetCurrentHashAsUInt32());
    }

    [Fact]
    public void Apply_PureExtraInsertion_ProducesNewData()
    {
        // Empty equivalence stream: the destination is produced entirely from the extra-data stream.
        // 空等价流：目标完全由附加数据流产生。
        var oldData = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var newData = Enumerable.Range(0, oldData.Length).Select(i => (byte)(0x50 + i)).ToArray();

        var patch = BuildPatch(oldData, newData, []);
        var result = ZucchiniPatchApplier.Apply(oldData, patch, newData.Length);

        Assert.Equal(newData, result);
    }

    [Fact]
    public void Apply_EquivalenceCopyPlusRawDelta_ProducesNewData()
    {
        // Equivalence copy with a destination gap (filled by extra data) plus raw-delta corrections:
        //   - equivalence 1 copies old[0..8) -> new[0..8); old[0]=0x55 differs from target 0x10 so a
        //     raw delta corrects it after the copy.
        //   - extra data fills the gap new[8..10) with {0xAA, 0xBB}.
        //   - equivalence 2 copies old[10..12) -> new[10..12).
        // 等价拷贝 + 目标空隙（由附加数据填充）+ 原始差修正：
        //   - 等价 1 拷贝 old[0..8) -> new[0..8)；old[0]=0x55 与目标 0x10 不同，拷贝后由原始差修正。
        //   - 附加数据以 {0xAA, 0xBB} 填充空隙 new[8..10)。
        //   - 等价 2 拷贝 old[10..12) -> new[10..12)。
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
        // Corrupt the new_crc field at header byte offset 20 (magic=0, version=4, old_size=8,
        // old_crc=12, new_size=16, new_crc=20, num_elems=24).
        // 破坏头部字节偏移 20 处的 new_crc 字段。
        patch[20] ^= 0xFF;

        var ex = Assert.Throws<ZucchiniChecksumException>(() =>
            ZucchiniPatchApplier.Apply(oldData, patch, newData.Length));
        Assert.Contains("CRC-32", ex.Message);
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

        // First 8 bytes are copied from the base by an equivalence; the last 8 bytes are inserted via
        // the extra-data stream. The patch is the same RAW-subset format as the unit tests above.
        // 前 8 字节由等价从基座拷贝；后 8 字节经附加数据流插入。补丁与上方单元测试为同一
        // RAW 子集格式。
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

        var payload = BuildPayload(manifest, new Dictionary<InstallOperation, byte[]> { [op] = patch });
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
            baseData.CopyTo(expected, 0);  // blocks 0-3 retain the base image (in-place delta semantics)
            newData.CopyTo(expected, 16);  // ZUCCHINI writes the patched data into blocks 4-7

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
            DstLength = (ulong)(4 * BlockSize)
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

    /// <summary>
    /// Minimal RAW patch writer sharing the applier's encoding conventions:
    /// <list type="bullet">
    /// <item>header: magic/version/old_size/old_crc/new_size/new_crc/num_elems as little-endian uint32;</item>
    /// <item>one element covering the whole image: old offset 0, new offset 0, both exe types as given;</item>
    /// <item>equivalence stream: (skip, copy) LEB128 pairs; skip advances source and destination by the
    /// same amount, so consecutive equivalences must satisfy dst gap == src gap;</item>
    /// <item>extra-data stream: the destination bytes in the gaps not covered by equivalences;</item>
    /// <item>raw-delta stream: (offset-difference LEB128, correction byte) pairs, offsets absolute in the
    /// new image, difference measured from one past the previously corrected byte.</item>
    /// </list>
    /// <para>与应用器共用编码约定的最小 RAW 补丁写入器：
    /// 头部为 magic/version/old_size/old_crc/new_size/new_crc/num_elems（little-endian uint32）；
    /// 单个元素覆盖整个镜像（旧偏移 0、新偏移 0，新旧可执行类型均为给定值）；等价流为
    /// (skip, copy) LEB128 对，skip 同时推进源与目标位置，因此相邻等价须满足目标空隙等于源空隙；
    /// 附加数据流为等价未覆盖目标空隙处的目标字节；原始差分流为 (偏移差 LEB128, 修正字节) 对，
    /// 偏移为新镜像中的绝对偏移，差值相对上一个被修正字节之后测量。</para>
    /// </summary>
    private static byte[] BuildPatch(
        byte[] oldData,
        byte[] newData,
        IReadOnlyList<(uint Src, uint Dst, uint Len)> equivalences,
        ZucchiniExecutableType exeType = ZucchiniExecutableType.Raw)
    {
        if (oldData.Length == 0)
        {
            throw new ArgumentException("Old data must not be empty.", nameof(oldData));
        }
        if (newData.Length == 0)
        {
            throw new ArgumentException("New data must not be empty.", nameof(newData));
        }

        // Validate alignment: the shared encoding advances both positions by skip.
        uint lastSrc = 0;
        uint lastDst = 0;
        foreach (var (src, dst, len) in equivalences)
        {
            if (src < lastSrc || dst < lastDst)
            {
                throw new ArgumentException("Equivalences must be sorted by source offset.", nameof(equivalences));
            }
            if ((src - lastSrc) != (dst - lastDst))
            {
                throw new ArgumentException("Equivalence destination gaps must equal source gaps for the shared encoding.", nameof(equivalences));
            }
            lastSrc = src + len;
            lastDst = dst + len;
        }

        // Equivalence stream.
        var equivalenceStream = new List<byte>();
        lastSrc = 0;
        foreach (var (src, _, len) in equivalences)
        {
            ZucchiniLeb128.Write(src - lastSrc, equivalenceStream);
            ZucchiniLeb128.Write(len, equivalenceStream);
            lastSrc = src + len;
        }

        // Extra-data stream: destination bytes in the gaps, and the intermediate image produced by
        // phase one (equivalences + extra, mirroring the applier) so raw deltas can be derived.
        var extra = new List<byte>();
        var intermediate = new byte[newData.Length];
        uint nextDst = 0;
        foreach (var (src, dst, len) in equivalences)
        {
            for (uint i = nextDst; i < dst; i++)
            {
                extra.Add(newData[i]);
                intermediate[i] = newData[i];
            }
            Array.Copy(oldData, (int)src, intermediate, (int)dst, (int)len);
            nextDst = dst + len;
        }
        for (uint i = nextDst; i < newData.Length; i++)
        {
            extra.Add(newData[i]);
            intermediate[i] = newData[i];
        }

        // Raw-delta stream: corrections turning the intermediate image into the target.
        var rawDelta = new List<byte>();
        uint lastCorrection = 0;
        for (int i = 0; i < newData.Length; i++)
        {
            if (intermediate[i] != newData[i])
            {
                ZucchiniLeb128.Write((uint)i - lastCorrection, rawDelta);
                rawDelta.Add((byte)(newData[i] ^ intermediate[i]));
                lastCorrection = (uint)i + 1;
            }
        }

        var patch = new List<byte>();
        WriteUInt32(ZucchiniConstants.Magic, patch);
        WriteUInt32(ZucchiniConstants.Version, patch);
        WriteUInt32((uint)oldData.Length, patch);
        WriteUInt32(Crc32(oldData), patch);
        WriteUInt32((uint)newData.Length, patch);
        WriteUInt32(Crc32(newData), patch);
        WriteUInt32(1, patch); // num elements

        // One element covering the whole image (old offset 0, new offset 0).
        WriteUInt32(0, patch);                  // old offset
        WriteUInt32((uint)oldData.Length, patch); // old size
        WriteUInt32((uint)exeType, patch);      // old exe type
        WriteUInt32(0, patch);                  // new offset
        WriteUInt32((uint)newData.Length, patch); // new size
        WriteUInt32((uint)exeType, patch);      // new exe type
        WriteUInt32((uint)equivalenceStream.Count, patch);
        WriteUInt32((uint)extra.Count, patch);
        WriteUInt32((uint)rawDelta.Count, patch);
        WriteUInt32(0, patch);                  // reference delta length

        // Reference bytes region is empty for RAW; then the four data streams follow.
        patch.AddRange(equivalenceStream);
        patch.AddRange(extra);
        patch.AddRange(rawDelta);
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
