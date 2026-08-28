using System.Buffers.Binary;

namespace UotanStudio.Firmware.Ext4.Core;

/// <summary>
/// ext4 superblock structure located at byte offset 1024, parsed in little-endian order.
/// <para>位于字节偏移 1024 处的 ext4 超级块结构，按小端解析。</para>
/// </summary>
internal sealed class Ext4Superblock
{
    /// <summary>
    /// Byte offset of the superblock within the volume.
    /// <para>超级块在卷内的字节偏移。</para>
    /// </summary>
    public const int SuperblockOffset = 1024;

    /// <summary>
    /// ext4 magic value (0xEF53).
    /// <para>ext4 魔数（0xEF53）。</para>
    /// </summary>
    public const ushort MagicValue = 0xEF53;

    /// <summary>
    /// Number of inodes in the filesystem (s_inodes_count).
    /// <para>文件系统中的 inode 总数（s_inodes_count）。</para>
    /// </summary>
    public uint InodesCount { get; private init; }

    /// <summary>
    /// Low 32 bits of the block count (s_blocks_count_lo).
    /// <para>块总数低 32 位（s_blocks_count_lo）。</para>
    /// </summary>
    public uint BlocksCountLo { get; private init; }

    /// <summary>
    /// High 32 bits of the block count (s_blocks_count_hi).
    /// <para>块总数高 32 位（s_blocks_count_hi）。</para>
    /// </summary>
    public uint BlocksCountHi { get; private init; }

    /// <summary>
    /// Number of free blocks, low 32 bits (s_free_blocks_count_lo).
    /// <para>空闲块数低 32 位（s_free_blocks_count_lo）。</para>
    /// </summary>
    public uint FreeBlocksCountLo { get; private init; }

    /// <summary>
    /// Number of free blocks, high 32 bits (s_free_blocks_count_hi).
    /// <para>空闲块数高 32 位（s_free_blocks_count_hi）。</para>
    /// </summary>
    public uint FreeBlocksCountHi { get; private init; }

    /// <summary>
    /// Number of free inodes (s_free_inodes_count).
    /// <para>空闲 inode 数（s_free_inodes_count）。</para>
    /// </summary>
    public uint FreeInodesCount { get; private init; }

    /// <summary>
    /// First data block (s_first_data_block); 0 for block sizes above 1 KiB.
    /// <para>首数据块（s_first_data_block）；块大小大于 1 KiB 时为 0。</para>
    /// </summary>
    public uint FirstDataBlock { get; private init; }

    /// <summary>
    /// Log2(block_size) - 10 (s_log_block_size); 2 means 4096-byte blocks.
    /// <para>log2(块大小) - 10（s_log_block_size）；2 表示 4096 字节块。</para>
    /// </summary>
    public uint LogBlockSize { get; private init; }

    /// <summary>
    /// Number of blocks per block group (s_blocks_per_group).
    /// <para>每个块组的块数（s_blocks_per_group）。</para>
    /// </summary>
    public uint BlocksPerGroup { get; private init; }

    /// <summary>
    /// Number of inodes per block group (s_inodes_per_group).
    /// <para>每个块组的 inode 数（s_inodes_per_group）。</para>
    /// </summary>
    public uint InodesPerGroup { get; private init; }

    /// <summary>
    /// ext4 magic value (s_magic); must be 0xEF53.
    /// <para>ext4 魔数（s_magic）；必须为 0xEF53。</para>
    /// </summary>
    public ushort Magic { get; private init; }

    /// <summary>
    /// Filesystem revision level (s_rev_level); 1 = dynamic (ext4 features in use).
    /// <para>文件系统修订级别（s_rev_level）；1 表示动态（使用 ext4 特性）。</para>
    /// </summary>
    public uint RevLevel { get; private init; }

    /// <summary>
    /// Inode size in bytes (s_inode_size); 0 falls back to 128.
    /// <para>inode 大小（字节，s_inode_size）；0 时回退为 128。</para>
    /// </summary>
    public ushort InodeSize { get; private init; }

    /// <summary>
    /// Compatible feature flags (s_feature_compat).
    /// <para>兼容特性标志（s_feature_compat）。</para>
    /// </summary>
    public uint FeatureCompat { get; private init; }

    /// <summary>
    /// Incompatible feature flags (s_feature_incompat); EXTENTS (0x1) is the most relevant here.
    /// <para>不兼容特性标志（s_feature_incompat）；EXTENTS（0x1）是此处最相关的。</para>
    /// </summary>
    public uint FeatureIncompat { get; private init; }

    /// <summary>
    /// Read-only compatible feature flags (s_feature_ro_compat).
    /// <para>只读兼容特性标志（s_feature_ro_compat）。</para>
    /// </summary>
    public uint FeatureRoCompat { get; private init; }

    /// <summary>
    /// Group descriptor size in bytes (s_desc_size); 0 means legacy 32-byte descriptors.
    /// <para>块组描述符大小（字节，s_desc_size）；0 表示传统 32 字节描述符。</para>
    /// </summary>
    public ushort DescSize { get; private init; }

    /// <summary>
    /// Computed block size in bytes (1024 &lt;&lt; s_log_block_size).
    /// <para>计算得到的块大小（字节，1024 &lt;&lt; s_log_block_size）。</para>
    /// </summary>
    public long BlockSize => 1024L << (int)LogBlockSize;

    /// <summary>
    /// Total block count combining the high and low words.
    /// <para>合并高低位后的总块数。</para>
    /// </summary>
    public ulong BlocksCount => ((ulong)BlocksCountHi << 32) | BlocksCountLo;

    /// <summary>
    /// Total free block count combining the high and low words.
    /// <para>合并高低位后的总空闲块数。</para>
    /// </summary>
    public ulong FreeBlocksCount => ((ulong)FreeBlocksCountHi << 32) | FreeBlocksCountLo;

    /// <summary>
    /// Effective group descriptor size (32 when s_desc_size is 0).
    /// <para>实际生效的组描述符大小（s_desc_size 为 0 时为 32）。</para>
    /// </summary>
    public int EffectiveDescSize => DescSize == 0 ? 32 : DescSize;

    /// <summary>
    /// Whether the superblock passed the magic check.
    /// <para>超级块是否通过魔数校验。</para>
    /// </summary>
    public bool IsValid => Magic == MagicValue;

    /// <summary>
    /// Parses the superblock from the beginning of an image buffer (little-endian).
    /// <para>从镜像缓冲区起始处解析超级块（小端）。</para>
    /// </summary>
    public static Ext4Superblock FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < SuperblockOffset + 360)
        {
            throw new InvalidDataException($"Image too small to hold an ext4 superblock (need {SuperblockOffset + 360} bytes, got {data.Length}).");
        }

        var span = data[SuperblockOffset..];
        var result = new Ext4Superblock
        {
            InodesCount = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]),
            BlocksCountLo = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]),
            FreeBlocksCountLo = BinaryPrimitives.ReadUInt32LittleEndian(span[12..16]),
            FreeInodesCount = BinaryPrimitives.ReadUInt32LittleEndian(span[16..20]),
            FirstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(span[20..24]),
            LogBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(span[24..28]),
            BlocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(span[32..36]),
            InodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(span[40..44]),
            Magic = BinaryPrimitives.ReadUInt16LittleEndian(span[56..58]),
            RevLevel = BinaryPrimitives.ReadUInt32LittleEndian(span[76..80]),
            InodeSize = BinaryPrimitives.ReadUInt16LittleEndian(span[88..90]),
            FeatureCompat = BinaryPrimitives.ReadUInt32LittleEndian(span[92..96]),
            FeatureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(span[96..100]),
            FeatureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(span[100..104]),
            DescSize = BinaryPrimitives.ReadUInt16LittleEndian(span[254..256]),
            BlocksCountHi = BinaryPrimitives.ReadUInt32LittleEndian(span[336..340]),
            FreeBlocksCountHi = BinaryPrimitives.ReadUInt32LittleEndian(span[344..348]),
        };
        if (!result.IsValid)
        {
            throw new InvalidDataException($"Invalid ext4 superblock magic 0x{result.Magic:X4} (expected 0xEF53).");
        }
        if (result.BlocksPerGroup == 0 || result.InodesPerGroup == 0)
        {
            throw new InvalidDataException("ext4 superblock has a zero blocks/inodes-per-group count.");
        }
        return result;
    }
}