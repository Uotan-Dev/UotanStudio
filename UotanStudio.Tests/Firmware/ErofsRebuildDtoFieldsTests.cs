using System.Text.Json;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the metadata DTO contract the parser and builder layers encode against: the device-slot tag bytes,
/// the chunk index table, the per-inode format shape and the superblock incidental dimensions survive a
/// document round trip, and a document written before those fields existed still deserializes.
/// <para>固定解析层与构建层据以编码的元数据 DTO 契约：设备槽 tag 字节、chunk 索引表、逐 inode 格式形态与
/// 超级块 incidental 维度可经文档往返，且在这些字段存在之前写出的文档仍可反序列化。</para>
/// </summary>
public sealed class ErofsRebuildDtoFieldsTests
{
    [Fact]
    public void RoundTrip_RecordedDimensions_ReachesEveryNewField()
    {
        var tag = new byte[64];
        for (var index = 0; index < tag.Length; index++)
        {
            tag[index] = (byte)(0xA0 + index);
        }

        byte[] indexes = [0x01, 0x02, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00];
        var record = new ErofsRebuildParameters
        {
            SbExtSlots = 5,
            DirBlkBits = 12,
            SourceDevices =
            [
                new ErofsRebuildDevice
                {
                    TagBase64 = Convert.ToBase64String(tag),
                    Blocks = 7,
                    UniAddr = 0x30000,
                },
            ],
            Inodes =
            [
                new ErofsRebuildInode
                {
                    Nid = 11,
                    ChunkBits = 2,
                    ChunkFormat = 0x0060,
                    ChunkIndexBase64 = Convert.ToBase64String(indexes),
                    SourceIFormat = 0x4010,
                    Compacted2B = true,
                    InterlacedPcluster = false,
                },
            ],
        };

        var json = ToJson(record);

        Assert.Contains("\"tagBase64\"", json, StringComparison.Ordinal);
        Assert.Contains("\"chunkFormat\"", json, StringComparison.Ordinal);
        Assert.Contains("\"chunkIndexBase64\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceIFormat\"", json, StringComparison.Ordinal);
        Assert.Contains("\"compacted2B\"", json, StringComparison.Ordinal);
        Assert.Contains("\"interlacedPcluster\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sbExtSlots\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dirBlkBits\"", json, StringComparison.Ordinal);

        var restored = FromJson(json);

        Assert.Equal((byte?)5, restored.SbExtSlots);
        Assert.Equal((byte?)12, restored.DirBlkBits);
        var device = Assert.Single(restored.SourceDevices);
        Assert.Equal(7UL, device.Blocks);
        Assert.Equal(0x30000UL, device.UniAddr);
        Assert.Equal(tag, Convert.FromBase64String(device.TagBase64!));
        var inode = Assert.Single(restored.Inodes);
        Assert.Equal(2, inode.ChunkBits);
        Assert.Equal((ushort?)0x0060, inode.ChunkFormat);
        Assert.Equal(indexes, Convert.FromBase64String(inode.ChunkIndexBase64!));
        Assert.Equal((ushort?)0x4010, inode.SourceIFormat);
        Assert.True(inode.Compacted2B);
        Assert.False(inode.InterlacedPcluster);
    }

    [Fact]
    public void Serialize_UnsetDimensions_OmitsTheNullEntries()
    {
        var record = new ErofsRebuildParameters
        {
            SourceDevices = [new ErofsRebuildDevice()],
            Inodes = [new ErofsRebuildInode { Nid = 3 }],
        };

        var json = ToJson(record);

        Assert.DoesNotContain("\"tagBase64\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"chunkFormat\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"chunkIndexBase64\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sourceIFormat\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"compacted2B\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"interlacedPcluster\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sbExtSlots\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"dirBlkBits\"", json, StringComparison.Ordinal);

        var restored = FromJson(json);

        Assert.Null(restored.SbExtSlots);
        Assert.Null(restored.DirBlkBits);
        Assert.Null(Assert.Single(restored.SourceDevices).TagBase64);
        var inode = Assert.Single(restored.Inodes);
        Assert.Null(inode.ChunkFormat);
        Assert.Null(inode.ChunkIndexBase64);
        Assert.Null(inode.SourceIFormat);
        Assert.Null(inode.Compacted2B);
        Assert.Null(inode.InterlacedPcluster);
    }

    [Fact]
    public void Deserialize_LegacyDocument_ReadsTheNewDimensionsAsNull()
    {
        const string legacy = """
            {
              "blockSize": 4096,
              "sourceDevices": [
                {
                  "tag": "AAAA",
                  "blocks": 3,
                  "uniAddr": 4096
                }
              ],
              "inodes": [
                {
                  "nid": 9,
                  "chunkBits": 4,
                  "compressed": true
                }
              ]
            }
            """;

        var restored = FromJson(legacy);

        Assert.Equal(4096, restored.BlockSize);
        Assert.Null(restored.SbExtSlots);
        Assert.Null(restored.DirBlkBits);
        var device = Assert.Single(restored.SourceDevices);
        Assert.Equal(3UL, device.Blocks);
        Assert.Null(device.TagBase64);
        var inode = Assert.Single(restored.Inodes);
        Assert.Equal(4, inode.ChunkBits);
        Assert.True(inode.Compressed);
        Assert.Null(inode.ChunkFormat);
        Assert.Null(inode.ChunkIndexBase64);
        Assert.Null(inode.SourceIFormat);
        Assert.Null(inode.Compacted2B);
        Assert.Null(inode.InterlacedPcluster);
    }

    [Fact]
    public void Deserialize_ChunkFormatWord_KeepsTheIndexHighBitsBesideClusterBits()
    {
        const string document = """
            {
              "inodes": [
                {
                  "nid": 21,
                  "chunkBits": 2,
                  "chunkFormat": 96
                }
              ]
            }
            """;

        var inode = Assert.Single(FromJson(document).Inodes);

        Assert.Equal(2, inode.ChunkBits);
        Assert.Equal((ushort?)(0x20 | 0x40), inode.ChunkFormat);
    }

    [Fact]
    public void Serialize_ExplicitlyDisabledAdvise_StaysDistinctFromAnAbsentDimension()
    {
        var record = new ErofsRebuildParameters
        {
            Inodes = [new ErofsRebuildInode { Nid = 33, Compacted2B = false, InterlacedPcluster = false }],
        };

        var json = ToJson(record);

        Assert.Contains("\"compacted2B\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"interlacedPcluster\": false", json, StringComparison.Ordinal);

        var inode = Assert.Single(FromJson(json).Inodes);

        Assert.False(inode.Compacted2B);
        Assert.False(inode.InterlacedPcluster);
        Assert.Null(inode.ChunkFormat);
    }

    [Fact]
    public void Reflection_NewFields_DeclareTheExactContractTypes()
    {
        Assert.Equal(typeof(string), typeof(ErofsRebuildDevice).GetProperty("TagBase64")!.PropertyType);
        Assert.Equal(typeof(ushort?), typeof(ErofsRebuildInode).GetProperty("ChunkFormat")!.PropertyType);
        Assert.Equal(typeof(string), typeof(ErofsRebuildInode).GetProperty("ChunkIndexBase64")!.PropertyType);
        Assert.Equal(typeof(ushort?), typeof(ErofsRebuildInode).GetProperty("SourceIFormat")!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ErofsRebuildInode).GetProperty("Compacted2B")!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ErofsRebuildInode).GetProperty("InterlacedPcluster")!.PropertyType);
        Assert.Equal(typeof(byte?), typeof(ErofsRebuildParameters).GetProperty("SbExtSlots")!.PropertyType);
        Assert.Equal(typeof(byte?), typeof(ErofsRebuildParameters).GetProperty("DirBlkBits")!.PropertyType);
    }

    [Fact]
    public void Reflection_SourceMetaboxDataLayout_IsNotDeclaredOnTheDto()
    {
        Assert.Null(typeof(ErofsRebuildParameters).GetProperty("SourceMetaboxDataLayout"));
    }

    private static string ToJson(ErofsRebuildParameters parameters)
        => JsonSerializer.Serialize(parameters, FileSystemPreservationJsonContext.Default.ErofsRebuildParameters);

    private static ErofsRebuildParameters FromJson(string json)
        => JsonSerializer.Deserialize(json, FileSystemPreservationJsonContext.Default.ErofsRebuildParameters)!;
}