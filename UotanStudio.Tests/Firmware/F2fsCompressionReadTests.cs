using System.Buffers.Binary;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// F2FS 解析侧压缩簇读路径接入测试。
/// <para>F2FS parser-side compressed-cluster read path integration tests.</para>
/// </summary>
public class F2fsCompressionReadTests
{
    // i_advise 在 inode 内的字节偏移（struct f2fs_inode）。
    // i_advise byte offset inside the inode.
    private const int OffsetAdvise = 2;

    // i_flags 在 inode 内的字节偏移。
    // i_flags byte offset inside the inode.
    private const int OffsetFlags = 80;

    /// <summary>
    /// 手工拼一个 4096 字节 inode raw bytes，在 i_advise 偏移放 0x12
    /// (algorithm=2/ZSTD, log_cluster_size=1)，在 i_flags 偏移放 F2FS_COMPR_FL (bit 27)。
    /// 验证 CompressAlgorithm == 2、LogClusterSize == 1、IsCompressed == true。
    /// </summary>
    [Fact]
    public void F2fsInode_ParsesCompressFlags()
    {
        var node = new byte[F2fsNode.NodeSize];

        // i_advise = 0x12 → 低 4 位 algorithm=2(ZSTD)，高 4 位 log_cluster_size=1。
        node[OffsetAdvise] = 0x12;

        // i_flags = F2FS_COMPR_FL (bit 27 = 0x0800_0000)。
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(OffsetFlags, 4), F2fsInode.ComprFlag);

        var inode = F2fsInode.FromBytes(node);

        Assert.Equal(2, inode.CompressAlgorithm);
        Assert.Equal(1, inode.LogClusterSize);
        Assert.True(inode.IsCompressed);
    }

    /// <summary>
    /// 手工拼 inode raw bytes，i_flags 不含 F2FS_COMPR_FL。
    /// 验证 IsCompressed == false。
    /// </summary>
    [Fact]
    public void F2fsInode_NonCompressed_IsCompressedFalse()
    {
        var node = new byte[F2fsNode.NodeSize];

        // i_advise 仍标记压缩算法（algorithm=1/LZ4，log_cluster_size=2），但 i_flags 不含压缩标志。
        node[OffsetAdvise] = (1 & 0x0F) | ((2 & 0x0F) << 4);

        // i_flags 清零（不含 F2FS_COMPR_FL）。
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(OffsetFlags, 4), 0u);

        var inode = F2fsInode.FromBytes(node);

        Assert.False(inode.IsCompressed);
        Assert.Equal(1, inode.CompressAlgorithm);
        Assert.Equal(2, inode.LogClusterSize);
    }

    /// <summary>
    /// 验证 i_advise 的双向一致：FromBytes 读出的 CompressAlgorithm/LogClusterSize 经
    /// WriteTo 写回后，再 FromBytes 仍得相同值。
    /// </summary>
    [Fact]
    public void F2fsInode_CompressFlags_RoundTripThroughWriteTo()
    {
        var node = new byte[F2fsNode.NodeSize];
        node[OffsetAdvise] = 0x21; // algorithm=1/LZ4, log_cluster_size=2.
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(OffsetFlags, 4), F2fsInode.ComprFlag);

        var inode = F2fsInode.FromBytes(node);
        var rewritten = new byte[F2fsNode.NodeSize];
        inode.WriteTo(rewritten);

        Assert.Equal(0x21, rewritten[OffsetAdvise]);
        Assert.Equal(F2fsInode.ComprFlag, BinaryPrimitives.ReadUInt32LittleEndian(rewritten.AsSpan(OffsetFlags, 4)));

        var reparsed = F2fsInode.FromBytes(rewritten);
        Assert.Equal(inode.CompressAlgorithm, reparsed.CompressAlgorithm);
        Assert.Equal(inode.LogClusterSize, reparsed.LogClusterSize);
        Assert.Equal(inode.IsCompressed, reparsed.IsCompressed);
    }

    /// <summary>
    /// LZO 压缩簇读路径最终委托给 F2fsCompression.DecompressCluster（托管 LZO1X）。
    /// 手工构造完整 dnode 镜像过于复杂，此处直接验证底层契约：垃圾数据抛
    /// InvalidDataException 而非 NotSupportedException。
    /// <para>The LZO compressed-cluster read path delegates to F2fsCompression.DecompressCluster
    /// (managed LZO1X). Building a full dnode image by hand is complex, so this verifies the
    /// underlying contract directly: garbage data throws InvalidDataException rather than
    /// NotSupportedException.</para>
    /// </summary>
    [Fact]
    public void Parser_LzoCompressedFile_GarbageData_ThrowsInvalidData()
    {
        // 解析侧对 LZO 算法（内核 id 0/3）映射到 CompressionAlgorithm.Lzo。
        Assert.ThrowsAny<InvalidDataException>(() =>
            F2fsCompression.DecompressCluster(
                new byte[64],
                CompressionAlgorithm.Lzo,
                1024,
                F2fsCompression.ClusterBytes));
    }
}