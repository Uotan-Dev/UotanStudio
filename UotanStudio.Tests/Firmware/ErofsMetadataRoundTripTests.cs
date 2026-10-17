using System.Text;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the serialization layer of the EROFS rebuild profile: the raw dimensions the format stores as bytes
/// (device-slot tag, chunk format word and index table, per-inode <c>i_format</c> with the compacted-2B and
/// interlaced shapes, superblock extended-slot and directory-block geometry) travel through the document, are
/// fed back into the build inputs, and a document written before those fields existed still loads with the
/// previous behavior.
/// <para>钉住 EROFS 重建档案的序列化层：格式以字节保存的原始维度（设备槽 tag、chunk 格式字与索引表、含紧凑
/// 2 字节与交错形态的逐 inode <c>i_format</c>、超级块扩展槽与目录块几何）在文档中往返并回填到构建输入，而在
/// 这些字段出现之前写出的文档仍按既有行为加载。</para>
/// </summary>
public sealed class ErofsMetadataRoundTripTests
{
    private const int BlockSize = 4096;

    private const ushort WideChunkFormat = ErofsInode.ChunkFormatIndexes | ErofsInode.ChunkFormat48Bit | 2;

    [Fact]
    public void Capture_Restore_RecordedRawDimensions_SurviveTheDocument()
    {
        var digest = DigestTag();
        var options = CapturedOptions(digest);

        var document = ErofsRebuildMetadata.Capture(options);
        var json = ToJson(document);

        Assert.Contains("\"tagBase64\"", json, StringComparison.Ordinal);
        Assert.Contains("\"chunkFormat\"", json, StringComparison.Ordinal);
        Assert.Contains("\"chunkIndexBase64\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceIFormat\"", json, StringComparison.Ordinal);
        Assert.Contains("\"compacted2B\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"interlacedPcluster\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"sbExtSlots\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"dirBlkBits\": 12", json, StringComparison.Ordinal);

        var restored = ErofsRebuildMetadata.Restore(FromJson(json));
        restored.Validate();

        Assert.Equal((byte?)3, restored.SourceSbExtSlots);
        Assert.Equal((byte?)12, restored.SourceDirBlkBits);
        Assert.Equal(digest, Assert.Single(restored.SourceDeviceTable!).Tag);

        var descriptor = Assert.Single(restored.ExtraDeviceImages!);
        Assert.Equal(digest, descriptor.TagBytes!);
        Assert.Equal("dev1", descriptor.Tag);
        Assert.Equal(8L * BlockSize, descriptor.SizeBytes);

        var chunk = restored.SourceInodes![21];
        Assert.Equal(ErofsDataLayout.ChunkBased, chunk.DataLayout);
        Assert.Equal(WideChunkFormat, chunk.ChunkFormat);
        Assert.Equal(ErofsInode.FlagExtended | ErofsInode.AdviseBitNlinkOneOrDotOmitted, chunk.IFormat);
        Assert.True(chunk.SourceIFormatRecorded);
        var chunkIdata = Assert.IsType<ErofsChunkIdata>(chunk.Idata);
        Assert.Equal(1u, chunkIdata.DeviceId);
        Assert.NotNull(chunkIdata.ChunkIndex);
        Assert.Equal(new ulong[] { 0x0000_0123UL, (2UL << 32) | 0x0000_4567UL }, chunkIdata.ChunkIndex!);

        var compressed = restored.SourceInodes[33];
        Assert.Equal(ErofsDataLayout.CompactIndexes, compressed.DataLayout);
        Assert.Equal(
            ZErofsMapHeader.AdviseCompacted2B | ZErofsMapHeader.AdviseInterlacedPcluster,
            compressed.ZAdvise);
        Assert.Equal((ushort)0, compressed.IFormat);
        Assert.True(compressed.SourceIFormatRecorded);
        var compressedIdata = Assert.IsType<ErofsCompressedIdata>(compressed.Idata);
        Assert.True(compressedIdata.Compiled2B);
        Assert.True(compressedIdata.InterlacedPlainPclusters);
    }

    [Fact]
    public void Restore_LegacyDocument_WithoutTheRawDimensions_KeepsThePreviousBehavior()
    {
        var textTag = new byte[ErofsDeviceSlot.TagSize];
        Encoding.UTF8.GetBytes("blob.img", textTag);

        var document = FromJson($$"""
            {
              "blockSize": 4096,
              "dictionarySize": 8388608,
              "extraDeviceCount": 1,
              "sourceDevices": [
                { "tag": "{{Convert.ToBase64String(textTag)}}", "blocks": 4, "uniAddr": 8192 }
              ],
              "inodes": [
                { "nid": 7, "path": "blob.bin", "chunkBits": 2 },
                { "nid": 9, "path": "packed.bin", "extended": true, "compressed": false, "algorithm": "Lz4" }
              ]
            }
            """);

        var restored = ErofsRebuildMetadata.Restore(document);
        restored.Validate();

        Assert.Null(restored.SourceSbExtSlots);
        Assert.Null(restored.SourceDirBlkBits);
        var descriptor = Assert.Single(restored.ExtraDeviceImages!);
        Assert.Equal("blob.img", descriptor.Tag);
        Assert.Equal(textTag, descriptor.TagBytes!);

        var chunk = restored.SourceInodes![7];
        Assert.Equal(ErofsDataLayout.ChunkBased, chunk.DataLayout);
        Assert.Equal((ushort)2, chunk.ChunkFormat);
        Assert.Null(Assert.IsType<ErofsChunkIdata>(chunk.Idata).ChunkIndex);
        Assert.Equal((ushort)0, chunk.ZAdvise);
        Assert.False(chunk.SourceIFormatRecorded);

        var flat = restored.SourceInodes[9];
        Assert.Equal(ErofsDataLayout.Flat, flat.DataLayout);
        Assert.Equal(ErofsInode.FlagExtended, flat.IFormat);
        Assert.Equal((ushort)0, flat.ZAdvise);
    }

    [Fact]
    public void Restore_DigestDeviceTag_KeepsTheRawBytesUnderAPlaceholderLabel()
    {
        var digest = DigestTag();
        var document = FromJson($$"""
            {
              "blockSize": 4096,
              "dictionarySize": 8388608,
              "extraDeviceCount": 1,
              "sourceDevices": [
                { "tagBase64": "{{Convert.ToBase64String(digest)}}", "blocks": 2, "uniAddr": 4096 }
              ]
            }
            """);

        var restored = ErofsRebuildMetadata.Restore(document);
        restored.Validate();

        var descriptor = Assert.Single(restored.ExtraDeviceImages!);
        Assert.Equal(digest, descriptor.TagBytes!);
        Assert.Equal("dev1", descriptor.Tag);
        Assert.Equal(2L * BlockSize, descriptor.SizeBytes);
        Assert.Equal(digest, Assert.Single(restored.SourceDeviceTable!).Tag);
    }

    [Fact]
    public void Capture_Restore_TextDeviceTag_KeepsTheLabelAndTheRawBytes()
    {
        var textTag = new byte[ErofsDeviceSlot.TagSize];
        Encoding.UTF8.GetBytes("other.img", textTag);
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            ExtraDeviceCount = 1,
            SourceDeviceTable = [new ErofsSourceDevice(textTag, 4, 0x2000)],
        };

        var restored = ErofsRebuildMetadata.Restore(FromJson(ToJson(ErofsRebuildMetadata.Capture(options))));
        restored.Validate();

        var descriptor = Assert.Single(restored.ExtraDeviceImages!);
        Assert.Equal("other.img", descriptor.Tag);
        Assert.Equal(textTag, descriptor.TagBytes!);
        Assert.Equal(4L * BlockSize, descriptor.SizeBytes);
    }

    [Fact]
    public void Restore_DeviceTagBeyondTheSlot_ThrowsCorruption()
    {
        var oversized = new byte[ErofsDeviceSlot.TagSize + 1];
        var document = FromJson($$"""
            {
              "blockSize": 4096,
              "dictionarySize": 8388608,
              "extraDeviceCount": 1,
              "sourceDevices": [
                { "tagBase64": "{{Convert.ToBase64String(oversized)}}", "blocks": 2, "uniAddr": 4096 }
              ]
            }
            """);

        var error = Assert.Throws<FileSystemCorruptedException>(() => ErofsRebuildMetadata.Restore(document));

        Assert.Contains("65 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_ChunkIndexTableWithPartialEntry_ThrowsCorruption()
    {
        var document = FromJson($$"""
            {
              "blockSize": 4096,
              "dictionarySize": 8388608,
              "inodes": [
                {
                  "nid": 11,
                  "path": "chunk.bin",
                  "chunkBits": 2,
                  "chunkFormat": 96,
                  "chunkIndexBase64": "{{Convert.ToBase64String(new byte[6])}}"
                }
              ]
            }
            """);

        var error = Assert.Throws<FileSystemCorruptedException>(() => ErofsRebuildMetadata.Restore(document));

        Assert.Contains("chunk index table", error.Message, StringComparison.Ordinal);
        Assert.Contains("chunk.bin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_UndefinedIFormatBits_ThrowsCorruption()
    {
        var document = FromJson("""
            {
              "blockSize": 4096,
              "dictionarySize": 8388608,
              "inodes": [
                { "nid": 13, "path": "bad.bin", "sourceIFormat": 8192 }
              ]
            }
            """);

        var error = Assert.Throws<FileSystemCorruptedException>(() => ErofsRebuildMetadata.Restore(document));

        Assert.Contains("i_format", error.Message, StringComparison.Ordinal);
        Assert.Contains("bad.bin", error.Message, StringComparison.Ordinal);
    }

    private static ErofsOptions CapturedOptions(byte[] digest) => new()
    {
        BlockSize = BlockSize,
        ExtraDeviceCount = 1,
        SourceSbExtSlots = 3,
        SourceDirBlkBits = 12,
        SourceDeviceTable = [new ErofsSourceDevice(digest, 8, 0x30000)],
        SourceInodes = new Dictionary<ulong, ErofsInode>
        {
            [21] = ChunkSourceInode(),
            [33] = CompressedSourceInode(),
        },
    };

    private static ErofsInode ChunkSourceInode()
    {
        var inode = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            Nid = 21,
            IFormat = ErofsInode.FlagExtended | ErofsInode.AdviseBitNlinkOneOrDotOmitted,
            DataLayout = ErofsDataLayout.ChunkBased,
            SourcePath = "blob.bin",
            SourceCompressed = false,
            Idata = new ErofsChunkIdata { ChunkFormat = WideChunkFormat, DeviceId = 1 },
        };
        inode.ChunkIndex = [0x0000_0123UL, (2UL << 32) | 0x0000_4567UL];
        return inode;
    }

    private static ErofsInode CompressedSourceInode() => new()
    {
        Layer = ErofsInodeLayer.Source,
        Nid = 33,
        IFormat = 0,
        DataLayout = ErofsDataLayout.CompactIndexes,
        SourcePath = "packed.bin",
        SourceCompressed = true,
        SourceAlgorithm = ErofsCompressionAlgorithm.Lz4,
        ZAdvise = ZErofsMapHeader.AdviseCompacted2B | ZErofsMapHeader.AdviseInterlacedPcluster,
        Idata = new ErofsCompressedIdata { CompactedIndexes = true, Compiled2B = true, InterlacedPlainPclusters = true },
        LogicalClusterSize = BlockSize,
    };

    private static byte[] DigestTag()
    {
        var digest = new byte[ErofsDeviceSlot.TagSize];
        for (var index = 0; index < digest.Length; index++)
        {
            digest[index] = (byte)(0x80 + (index * 7 % 0x60));
        }

        digest[5] = 0;
        digest[9] = 0xFF;
        return digest;
    }

    private static string ToJson(ErofsRebuildParameters parameters)
        => System.Text.Json.JsonSerializer.Serialize(parameters, FileSystemPreservationJsonContext.Default.ErofsRebuildParameters);

    private static ErofsRebuildParameters FromJson(string json)
        => System.Text.Json.JsonSerializer.Deserialize(
            json, FileSystemPreservationJsonContext.Default.ErofsRebuildParameters)!;
}