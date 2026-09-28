using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.F2fs.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks Linux inode compression fields independently. <para>独立检查 Linux inode 压缩字段。</para></summary>
public class F2fsCompressionReadTests
{
    [Fact]
    public void F2fsInode_ParsesLinuxCompressionExtraAttributes()
    {
        var node = new byte[4096];
        node[2] = 0x12;
        node[3] = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(80), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(360), 36);
        BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(384), 30);
        node[392] = 2;
        node[393] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(394), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(396), 0xFFFF_FFFE);

        var inode = F2fsInode.FromBytes(node);
        Assert.True(inode.IsCompressed);
        Assert.Equal(2, inode.CompressAlgorithm);
        Assert.Equal(4, inode.LogClusterSize);
        Assert.Equal(30UL, inode.CompressedBlocks);
        Assert.Equal(912, inode.AddressCount);
        Assert.Equal(1008, inode.DataAddressesPerNode);
        var rewritten = new byte[4096];
        inode.WriteTo(rewritten);
        Assert.Equal(node, rewritten);
    }

    [Fact]
    public void F2fsInode_AdviseDoesNotSetCompressionParameters()
    {
        var node = new byte[4096];
        node[2] = 0x42;
        var inode = F2fsInode.FromBytes(node);
        Assert.False(inode.IsCompressed);
        Assert.Equal(0, inode.CompressAlgorithm);
        Assert.Equal(0, inode.LogClusterSize);
    }

    [Fact]
    public void F2fsInode_CompressedWithoutExtraAttributesIsInvalid()
    {
        var node = new byte[4096];
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(80), 4);
        Assert.Throws<InvalidDataException>(() => F2fsInode.FromBytes(node));
    }

    [Fact]
    public void Parser_LzoCompressedFile_GarbageData_ThrowsInvalidData()
    {
        Assert.ThrowsAny<InvalidDataException>(() => F2fsCompression.DecompressCluster(new byte[64], CompressionAlgorithm.Lzo, 1024, 65536));
    }
}