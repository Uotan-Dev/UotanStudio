using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 超级块，位于镜像偏移 1024 处，按 Linux 内核 fs/erofs/erofs_fs.h 的小端字节序布局解析。
/// 经典磁盘布局为 112 字节结构（占用 128 字节槽位），内核主线已将其扩展为 144 字节
/// （含 48BIT / METABOX / xattr 前缀等新字段）。本类型解析完整的 128 字节主槽位，
/// 并额外暴露内核主线新增字段（<see cref="PackedNid"/>、<see cref="RootNid8b"/>、
/// <see cref="MetaBoxNid"/>、<see cref="BuildTime32"/> 等），供按 feature 位选择读取。
/// <para>EROFS superblock located at image offset 1024, parsed with the little-endian layout
/// defined in Linux kernel fs/erofs/erofs_fs.h. The classic on-disk struct is 112 bytes (kept in a
/// 128-byte slot); kernel mainline has extended it to 144 bytes (48BIT / METABOX / xattr prefix
/// fields). This type parses the full 128-byte main slot and additionally exposes the newer
/// mainline fields (<see cref="PackedNid"/>, <see cref="RootNid8b"/>, <see cref="MetaBoxNid"/>,
/// <see cref="BuildTime32"/>, etc.) for feature-gated reads.</para>
/// </summary>
internal sealed class ErofsSuperblock
{
    /// <summary>
    /// 超级块在镜像内的偏移。
    /// <para>Offset of the superblock within the image.</para>
    /// </summary>
    public const int SuperblockOffset = 1024;

    /// <summary>
    /// 超级块磁盘槽位字节数。经典结构体为 112 字节，但按上游约定整个槽位固定 128 字节
    /// （<c>128 + sb_extslots * 16</c>）；本实现解析该主槽位。
    /// <para>Size in bytes of the superblock slot on disk. The classic struct is 112 bytes but the
    /// upstream convention keeps a fixed 128-byte slot (<c>128 + sb_extslots * 16</c>); this
    /// implementation parses that main slot.</para>
    /// </summary>
    public const int SuperblockSize = 128;

    /// <summary>
    /// 扩展槽位每块字节数（<c>sb_extslots * ErofsSuperblock.ExtSlotSize</c> 追加在 128 字节之后）。
    /// <para>Bytes per extended superblock slot (appended after the 128-byte main slot:
    /// <c>sb_extslots * ErofsSuperblock.ExtSlotSize</c>).</para>
    /// </summary>
    public const int ExtSlotSize = 16;

    /// <summary>
    /// erofs 魔数（0xE0F5E1E2；磁盘上为小端字节 E2 E1 F5 E0）。
    /// <para>EROFS magic value (0xE0F5E1E2; little-endian bytes on disk: E2 E1 F5 E0).</para>
    /// </summary>
    public const uint Magic = 0xE0F5E1E2u;

    /// <summary>
    /// 每个 compact inode 槽位的固定字节数（Linux 内核
    /// <c>sizeof(struct erofs_inode_compact)</c> = 32）。nid 到字节偏移的映射为
    /// <c>meta_blkaddr * block_size + nid * CompactInodeSize</c>。extended inode 占用 64 字节，
    /// 但 EROFS 镜像中同一 inode 表只使用其中一种尺寸。
    /// <para>Fixed byte size of each compact inode slot (Linux kernel
    /// <c>sizeof(struct erofs_inode_compact)</c> = 32). The nid-to-byte-offset mapping is
    /// <c>meta_blkaddr * block_size + nid * CompactInodeSize</c>. An extended inode occupies 64
    /// bytes, but a single EROFS inode table uses only one of the two sizes.</para>
    /// </summary>
    public const int CompactInodeSize = 32;

    /// <summary>设备槽位大小（Linux 内核 <c>sizeof(struct erofs_deviceslot)</c> = 128）。
    /// Device slot size (Linux kernel <c>sizeof(struct erofs_deviceslot)</c> = 128).</summary>
    public const int DeviceSlotSize = 128;

    // ---- 兼容特性位（s_feature_compat）。Compatible feature flags (s_feature_compat). ----
    /// <summary>超级块 crc32c 校验和（EROFS_FEATURE_COMPAT_SB_CHKSUM）。Superblock crc32c checksum.</summary>
    public const uint CompatSbChecksum = 0x00000001;
    /// <summary>inode mtime 语义（EROFS_FEATURE_COMPAT_MTIME）。Inode mtime semantics.</summary>
    public const uint CompatMtime = 0x00000002;
    /// <summary>xattr 名称过滤（EROFS_FEATURE_COMPAT_XATTR_FILTER）。Xattr name filter.</summary>
    public const uint CompatXattrFilter = 0x00000004;
    /// <summary>共享 EA 元数据盒（EROFS_FEATURE_COMPAT_SHARED_EA_IN_METABOX）。Shared EA in metabox.</summary>
    public const uint CompatSharedEaInMetabox = 0x00000008;
    /// <summary>普通 xattr 前缀（EROFS_FEATURE_COMPAT_PLAIN_XATTR_PFX）。Plain xattr prefixes.</summary>
    public const uint CompatPlainXattrPfx = 0x00000010;
    /// <summary>共享 xattr（EROFS_FEATURE_COMPAT_ISHARE_XATTRS）。I-shared xattrs.</summary>
    public const uint CompatIshareXattrs = 0x00000020;

    // ---- 不兼容特性位（s_feature_incompat）。Incompatible feature flags (s_feature_incompat). ----
    /// <summary>LZ4 零填充（EROFS_FEATURE_INCOMPAT_LZ4_0PADDING）。LZ4 zero padding.</summary>
    public const uint IncompatLz4_0Padding = 0x00000001;
    /// <summary>压缩配置（EROFS_FEATURE_INCOMPAT_COMPR_CFGS，与 BIG_PCLUSTER 同一位的历史别名）。
    /// Compression configs (historical alias of BIG_PCLUSTER).</summary>
    public const uint IncompatCompressCfgs = 0x00000002;
    /// <summary>大 pcluster（EROFS_FEATURE_INCOMPAT_BIG_PCLUSTER）。Big pcluster.</summary>
    public const uint IncompatBigPcluster = 0x00000002;
    /// <summary>chunk 文件（EROFS_FEATURE_INCOMPAT_CHUNKED_FILE）。Chunk-based files.</summary>
    public const uint IncompatChunkedFile = 0x00000004;
    /// <summary>设备表（EROFS_FEATURE_INCOMPAT_DEVICE_TABLE）。Device table.</summary>
    public const uint IncompatDeviceTable = 0x00000008;
    /// <summary>HEAD2 压缩簇（EROFS_FEATURE_INCOMPAT_COMPR_HEAD2，与 DEVICE_TABLE 同一位的历史别名）。
    /// HEAD2 compressed clusters (historical alias of DEVICE_TABLE).</summary>
    public const uint IncompatCompressHead2 = 0x00000008;
    /// <summary>尾部打包（EROFS_FEATURE_INCOMPAT_ZTAILPACKING）。Tail packing.</summary>
    public const uint IncompatZtailpacking = 0x00000010;
    /// <summary>fragment 文件（EROFS_FEATURE_INCOMPAT_FRAGMENTS）。Fragment files.</summary>
    public const uint IncompatFragments = 0x00000020;
    /// <summary>去重（EROFS_FEATURE_INCOMPAT_DEDUPE，与 FRAGMENTS 同一位的历史别名）。Dedupe (alias of FRAGMENTS).</summary>
    public const uint IncompatDedupe = 0x00000020;
    /// <summary>长 xattr 名称前缀表（EROFS_FEATURE_INCOMPAT_XATTR_PREFIXES）。Long xattr name prefixes.</summary>
    public const uint IncompatXattrPrefixes = 0x00000040;
    /// <summary>48 位块号 / 根 nid（EROFS_FEATURE_INCOMPAT_48BIT）。48-bit block numbers / root nid.</summary>
    public const uint Incompat48Bit = 0x00000080;
    /// <summary>元数据盒（EROFS_FEATURE_INCOMPAT_METABOX）。Metabox.</summary>
    public const uint IncompatMetabox = 0x00000100;

    /// <summary>全部已知不兼容特性位的并集（用于校验未知位）。Union of all known incompatible feature bits.</summary>
    public const uint AllIncompat = (IncompatMetabox << 1) - 1;

    // 字段偏移（相对超级块起始处）。Field offsets relative to the superblock start.
    private const int OffsetMagic = 0;
    private const int OffsetChecksum = 4;
    private const int OffsetFeatureCompat = 8;
    private const int OffsetBlockSizeBits = 12;
    private const int OffsetSbExtSlots = 13;
    private const int OffsetRootNidOrBlocksHi = 14;
    private const int OffsetInos = 16;
    private const int OffsetBuildTime = 24;       // 经典 build_time / 主线 epoch
    private const int OffsetBuildTimeNsec = 32;   // 经典 build_time_nsec / 主线 fixed_nsec
    private const int OffsetBlocks = 36;
    private const int OffsetMetaBlkAddr = 40;
    private const int OffsetXattrBlkAddr = 44;
    private const int OffsetUuid = 48;
    private const int OffsetVolumeName = 64;
    private const int OffsetFeatureIncompat = 80;
    private const int OffsetAvailableComprAlgs = 84;
    private const int OffsetExtraDevices = 86;
    private const int OffsetDevtSlotoff = 88;
    private const int OffsetDirBlkBits = 90;
    private const int OffsetXattrPrefixCount = 91;
    private const int OffsetXattrPrefixStart = 92;
    private const int OffsetPackedNid = 96;
    private const int OffsetXattrFilterReserved = 104;
    private const int OffsetIshareXattrPrefixId = 105;
    private const int OffsetBuildTime32 = 108;    // 主线新增：epoch 之上叠加的秒数
    private const int OffsetRootNid8b = 112;      // 主线 48BIT：64 位根 nid
    private const int OffsetReserved2 = 120;
    private const int OffsetMetaBoxNid = 128;     // 主线 METABOX：metabox inode nid
    private const int OffsetReserved3 = 136;

    /// <summary>魔数。Magic value.</summary>
    public uint MagicValue { get; init; }

    /// <summary>校验和（s_checksum；当 <see cref="HasCompatChecksum"/> 时为 superblock 的 crc32c）。
    /// Checksum (s_checksum; crc32c of the superblock when <see cref="HasCompatChecksum"/>).</summary>
    public uint Checksum { get; init; }

    /// <summary>兼容特性位。Compatible feature flags.</summary>
    public uint FeatureCompat { get; init; }

    /// <summary>块大小移位（s_blkszbits，默认 12 → 4096 字节）。Block size in bit shift (s_blkszbits, default 12 → 4096 bytes).</summary>
    public byte BlockSizeBits { get; init; } = 12;

    /// <summary>扩展槽位数量（s_sb_extslots；超级块总大小 = 128 + sb_extslots * 16）。
    /// Number of extended superblock slots (s_sb_extslots; total superblock size = 128 + sb_extslots * 16).</summary>
    public byte SbExtSlots { get; init; }

    /// <summary>根目录 inode 号（经典 s_root_nid，16 位）。在 <see cref="Has48Bit"/> 下请使用
    /// <see cref="RootNid64"/>。Nid of the root directory inode (classic 16-bit s_root_nid); use
    /// <see cref="RootNid64"/> when <see cref="Has48Bit"/>.</summary>
    public ushort RootNid { get; init; }

    /// <summary>有效 inode 总数（s_inos）。Total number of valid inodes (s_inos).</summary>
    public ulong Inos { get; init; }

    /// <summary>经典构建时间（Unix 秒，s_build_time / 主线 s_epoch）。Classic build time in Unix
    /// seconds (s_build_time / mainline s_epoch).</summary>
    public ulong BuildTime { get; init; }

    /// <summary>构建时间纳秒部分（经典 s_build_time_nsec / 主线 s_fixed_nsec）。
    /// Build time nanoseconds (classic s_build_time_nsec / mainline s_fixed_nsec).</summary>
    public uint BuildTimeNsec { get; init; }

    /// <summary>文件系统总块数（经典 s_blocks，32 位）。在 <see cref="Has48Bit"/> 下请使用
    /// <see cref="BlockCount64"/>。Total filesystem block count (classic 32-bit s_blocks); use
    /// <see cref="BlockCount64"/> when <see cref="Has48Bit"/>.</summary>
    public uint Blocks { get; init; }

    /// <summary>元数据区起始块号（s_meta_blkaddr）。Start block number of the metadata area (s_meta_blkaddr).</summary>
    public uint MetaBlkAddr { get; init; }

    /// <summary>共享 xattr 区起始块号（s_xattr_blkaddr）。Start block number of the shared xattr area.</summary>
    public uint XattrBlkAddr { get; init; }

    /// <summary>128 位 UUID（16 字节，s_uuid）。128-bit UUID (16 bytes, s_uuid).</summary>
    public byte[] Uuid { get; init; } = [];

    /// <summary>卷名（16 字节，NUL 填充，s_volume_name）。Volume name (16 bytes, NUL padded, s_volume_name).</summary>
    public string VolumeName { get; init; } = string.Empty;

    /// <summary>不兼容特性位。Incompatible feature flags.</summary>
    public uint FeatureIncompat { get; init; }

    /// <summary>可用压缩算法位图（bit0 = LZ4，bit1 = LZMA，bit2 = DEFLATE，bit3 = ZSTD；
    /// 未设置该字段时按 <c>Z_EROFS_ALL_COMPR_ALGS</c> 处理）。Available compression algorithms
    /// (bit0 = LZ4, bit1 = LZMA, bit2 = DEFLATE, bit3 = ZSTD; when the field is unset it is treated
    /// as <c>Z_EROFS_ALL_COMPR_ALGS</c>).</summary>
    public ushort AvailableComprAlgs { get; init; }

    /// <summary>额外设备数量。Number of extra devices.</summary>
    public ushort ExtraDevices { get; init; }

    /// <summary>设备槽位偏移（s_devt_slotoff；设备表起始偏移 = devt_slotoff * 128）。
    /// Device slot offset (s_devt_slotoff; device table start offset = devt_slotoff * 128).</summary>
    public ushort DevtSlotoff { get; init; }

    /// <summary>目录块大小移位（s_dirblkbits；0 表示与块大小一致）。Directory block size in bit
    /// shift (s_dirblkbits; 0 means equal to the block size).</summary>
    public byte DirBlkBits { get; init; }

    /// <summary>长 xattr 名称前缀数量（s_xattr_prefix_count）。Count of long xattr name prefixes.</summary>
    public byte XattrPrefixCount { get; init; }

    /// <summary>长 xattr 名称前缀表起始偏移（s_xattr_prefix_start，字节偏移）。
    /// Start offset of the long xattr prefix table (s_xattr_prefix_start, byte offset).</summary>
    public uint XattrPrefixStart { get; init; }

    /// <summary>特殊 packed inode 的 nid（s_packed_nid，fragments 特性）。Nid of the special packed
    /// inode (s_packed_nid, fragments feature).</summary>
    public ulong PackedNid { get; init; }

    /// <summary>xattr 名称过滤保留位。Reserved for the xattr name filter.</summary>
    public byte XattrFilterReserved { get; init; }

    /// <summary>共享 xattr 前缀 id（主线 s_ishare_xattr_prefix_id）。I-shared xattr prefix id.</summary>
    public byte IshareXattrPrefixId { get; init; }

    /// <summary>主线新增构建时间秒数（s_build_time，叠加于 <see cref="BuildTime"/> 之上；仅 48BIT 布局）。
    /// Mainline build time seconds added on top of <see cref="BuildTime"/> (s_build_time; 48BIT only).</summary>
    public uint BuildTime32 { get; init; }

    /// <summary>主线 48BIT 根目录 nid（s_rootnid_8b）。Mainline 48-bit root nid (s_rootnid_8b).</summary>
    public ulong RootNid8b { get; init; }

    /// <summary>主线 METABOX metabox inode nid（s_metabox_nid）。Mainline metabox inode nid (s_metabox_nid).</summary>
    public ulong MetaBoxNid { get; init; }

    // ---- 特性便捷判断。Feature convenience flags. ----
    /// <summary>是否带超级块校验和特性。Whether the superblock checksum feature is set.</summary>
    public bool HasCompatChecksum => (FeatureCompat & CompatSbChecksum) != 0;
    /// <summary>是否带 mtime 特性。Whether the mtime feature is set.</summary>
    public bool HasCompatMtime => (FeatureCompat & CompatMtime) != 0;
    /// <summary>是否带 48 位块号特性。Whether the 48-bit feature is set.</summary>
    public bool Has48Bit => (FeatureIncompat & Incompat48Bit) != 0;
    /// <summary>是否带大 pcluster 特性。Whether the big-pcluster feature is set.</summary>
    public bool HasBigPcluster => (FeatureIncompat & IncompatBigPcluster) != 0;
    /// <summary>是否带 chunk 文件特性。Whether the chunk-based-file feature is set.</summary>
    public bool HasChunkedFile => (FeatureIncompat & IncompatChunkedFile) != 0;
    /// <summary>是否带设备表特性。Whether the device-table feature is set.</summary>
    public bool HasDeviceTable => (FeatureIncompat & IncompatDeviceTable) != 0;
    /// <summary>是否带尾部打包特性。Whether the tail-packing feature is set.</summary>
    public bool HasZtailpacking => (FeatureIncompat & IncompatZtailpacking) != 0;
    /// <summary>是否带 fragment 特性。Whether the fragments feature is set.</summary>
    public bool HasFragments => (FeatureIncompat & IncompatFragments) != 0;
    /// <summary>是否带 xattr 前缀表特性。Whether the xattr-prefixes feature is set.</summary>
    public bool HasXattrPrefixes => (FeatureIncompat & IncompatXattrPrefixes) != 0;
    /// <summary>是否带 METABOX 特性。Whether the metabox feature is set.</summary>
    public bool HasMetabox => (FeatureIncompat & IncompatMetabox) != 0;

    /// <summary>
    /// 元数据块数（近似值 Blocks - MetaBlkAddr）。标准磁盘格式并未直接存储纯元数据块数，
    /// 该近似值表示自元数据起始块到镜像末尾的块数（元数据 + 数据）。
    /// <para>Approximate metadata block count (computed as Blocks - MetaBlkAddr). The standard
    /// on-disk format does not store the pure metadata block count; this approximates the blocks from
    /// the metadata start block to the end of the image (metadata + data).</para>
    /// </summary>
    public ulong MetadataBlockCount => BlockCount64 - MetaBlkAddr;

    /// <summary>块大小（字节）。Block size in bytes.</summary>
    public int BlockSize => 1 << BlockSizeBits;

    /// <summary>元数据区起始字节偏移。Byte offset of the metadata area start.</summary>
    public long MetadataByteOffset => MetaBlkAddr * BlockSize;

    /// <summary>
    /// 总块数（48 位布局下拼接 blocks_hi/lo）。Total block count (48-bit layout merges blocks_hi/lo).
    /// </summary>
    public ulong BlockCount64 => Has48Bit
        ? ((ulong)BlocksHi << 32) | Blocks
        : Blocks;

    /// <summary>
    /// 根目录 nid（48 位布局下优先 <see cref="RootNid8b"/>）。Root directory nid (48-bit layout
    /// prefers <see cref="RootNid8b"/>).</summary>
    public ulong RootNid64 => Has48Bit ? RootNid8b : RootNid;

    /// <summary>
    /// 48 位布局下与根 nid 共用偏移的块数高位（s_blocks_hi）。High 16 bits of the block count
    /// (s_blocks_hi, sharing the root-nid offset in the 48-bit layout).
    /// </summary>
    public ushort BlocksHi { get; init; }

    /// <summary>实际可用压缩算法位图（未设置时按全部已知算法处理）。
    /// Effective compression algorithm bitmap (treated as all known algorithms when unset).</summary>
    public ushort EffectiveComprAlgs => AvailableComprAlgs == 0 ? (ushort)0x0F : AvailableComprAlgs;

    /// <summary>
    /// 根据 nid 计算 inode 的绝对字节偏移：(meta_blkaddr &lt;&lt; s_blkszbits) + nid。
    /// <para>Computes the absolute byte offset of an inode from its nid:
    /// (meta_blkaddr &lt;&lt; s_blkszbits) + nid.</para>
    /// </summary>
    public long GetInodeOffset(ulong nid) => MetadataByteOffset + (long)nid * CompactInodeSize;

    /// <summary>
    /// 判断镜像头部是否包含 erofs 魔数（偏移 1024 处 4 字节）。
    /// <para>Checks whether the image contains the EROFS magic (4 bytes at offset 1024).</para>
    /// </summary>
    public static bool HasMagic(ReadOnlySpan<byte> image)
        => image.Length >= SuperblockOffset + 4
           && BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(SuperblockOffset, 4)) == Magic;

    /// <summary>
    /// 判断 4 字节片段是否为魔数（小端 0xE0F5E1E2）。
    /// <para>Checks whether a 4-byte slice holds the magic value (little-endian 0xE0F5E1E2).</para>
    /// </summary>
    public static bool IsMagicBytes(ReadOnlySpan<byte> fourBytes)
        => fourBytes.Length >= 4
           && BinaryPrimitives.ReadUInt32LittleEndian(fourBytes) == Magic;

    /// <summary>
    /// 从超级块区域（偏移 1024 起、长度至少 112 字节）解析超级块；魔数不符时抛出 FormatException。
    /// <para>Parses the superblock from the superblock region (at least 112 bytes starting at offset
    /// 1024); throws FormatException when the magic does not match.</para>
    /// </summary>
    public static ErofsSuperblock FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < SuperblockSize)
        {
            // 经典结构体仅 112 字节；允许读取大于等于 112 的输入，但小于 128 的经典镜像也须可解析。
            // The classic struct is only 112 bytes; inputs of 112 bytes or more must parse, even
            // though the on-disk slot is 128 bytes.
            if (data.Length < SuperblockSize - 16)
            {
                throw new FormatException($"EROFS superblock region too short: {data.Length} bytes.");
            }
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetMagic..]) != Magic)
        {
            throw new FormatException("Invalid EROFS superblock magic.");
        }

        var has48Bit = (BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetFeatureIncompat..]) & Incompat48Bit) != 0;
        return new ErofsSuperblock
        {
            MagicValue = Magic,
            Checksum = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetChecksum..]),
            FeatureCompat = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetFeatureCompat..]),
            BlockSizeBits = data[OffsetBlockSizeBits],
            SbExtSlots = data[OffsetSbExtSlots],
            // 48BIT 布局下偏移 14 为块数高位，否则为经典根 nid。
            // In the 48-bit layout offset 14 holds the block-count MSB, otherwise the classic root nid.
            RootNid = has48Bit
                ? (ushort)0
                : BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetRootNidOrBlocksHi..]),
            BlocksHi = has48Bit
                ? BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetRootNidOrBlocksHi..])
                : (ushort)0,
            Inos = BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetInos..]),
            BuildTime = BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetBuildTime..]),
            BuildTimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetBuildTimeNsec..]),
            Blocks = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetBlocks..]),
            MetaBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetMetaBlkAddr..]),
            XattrBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetXattrBlkAddr..]),
            Uuid = data.Slice(OffsetUuid, 16).ToArray(),
            VolumeName = ReadFixedAscii(data.Slice(OffsetVolumeName, 16)),
            FeatureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetFeatureIncompat..]),
            AvailableComprAlgs = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetAvailableComprAlgs..]),
            ExtraDevices = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetExtraDevices..]),
            DevtSlotoff = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetDevtSlotoff..]),
            DirBlkBits = data[OffsetDirBlkBits],
            XattrPrefixCount = data[OffsetXattrPrefixCount],
            XattrPrefixStart = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetXattrPrefixStart..]),
            PackedNid = BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetPackedNid..]),
            XattrFilterReserved = data[OffsetXattrFilterReserved],
            IshareXattrPrefixId = data[OffsetIshareXattrPrefixId],
            BuildTime32 = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetBuildTime32..]),
            // 48BIT 与 METABOX 字段位于 112 字节经典结构之外，输入不足时按 0 处理（经典镜像）。
            // The 48BIT / METABOX fields live beyond the 112-byte classic struct; treat them as 0
            // when the input is shorter (classic images).
            RootNid8b = data.Length >= OffsetRootNid8b + 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetRootNid8b..])
                : 0,
            MetaBoxNid = data.Length >= OffsetMetaBoxNid + 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetMetaBoxNid..])
                : 0,
        };
    }

    /// <summary>
    /// 从完整镜像解析超级块（自动定位到偏移 1024）。
    /// <para>Parses the superblock from a full image (locating offset 1024 automatically).</para>
    /// </summary>
    public static ErofsSuperblock FromImage(ReadOnlySpan<byte> image)
    {
        if (image.Length < SuperblockOffset + SuperblockSize - 16)
        {
            throw new FormatException($"Image too small to hold an EROFS superblock: {image.Length} bytes.");
        }

        return FromBytes(image.Slice(SuperblockOffset, Math.Min(SuperblockSize, image.Length - SuperblockOffset)));
    }

    /// <summary>
    /// 将超级块序列化到缓冲区（长度至少 128 字节；经典布局写满 128 字节槽位）。
    /// <para>Serializes the superblock into a buffer of at least 128 bytes (the classic layout fills
    /// the whole 128-byte slot).</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SuperblockSize)
        {
            throw new ArgumentException("Superblock destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetMagic..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetChecksum..], Checksum);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetFeatureCompat..], FeatureCompat);
        destination[OffsetBlockSizeBits] = BlockSizeBits;
        destination[OffsetSbExtSlots] = SbExtSlots;
        if (Has48Bit)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetRootNidOrBlocksHi..], BlocksHi);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetRootNidOrBlocksHi..], RootNid);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetInos..], Inos);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetBuildTime..], BuildTime);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetBuildTimeNsec..], BuildTimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetBlocks..], Blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetMetaBlkAddr..], MetaBlkAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetXattrBlkAddr..], XattrBlkAddr);
        Uuid.AsSpan(0, Math.Min(16, Uuid.Length)).CopyTo(destination[OffsetUuid..]);
        WriteFixedAscii(destination.Slice(OffsetVolumeName, 16), VolumeName);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetFeatureIncompat..], FeatureIncompat);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetAvailableComprAlgs..], AvailableComprAlgs);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetExtraDevices..], ExtraDevices);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetDevtSlotoff..], DevtSlotoff);
        destination[OffsetDirBlkBits] = DirBlkBits;
        destination[OffsetXattrPrefixCount] = XattrPrefixCount;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetXattrPrefixStart..], XattrPrefixStart);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetPackedNid..], PackedNid);
        destination[OffsetXattrFilterReserved] = XattrFilterReserved;
        destination[OffsetIshareXattrPrefixId] = IshareXattrPrefixId;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetBuildTime32..], BuildTime32);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetRootNid8b..], RootNid8b);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetReserved2..], 0);
        // METABOX 字段位于 128 字节主槽位之外（需 sb_extslots ≥ 1 的扩展槽），
        // 仅在目标缓冲足够且特性开启时写入。
        // The METABOX field lives beyond the 128-byte main slot (needs an extended slot with
        // sb_extslots >= 1); write it only when the buffer is large enough and the feature is set.
        if (destination.Length >= OffsetMetaBoxNid + 8 && HasMetabox)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetMetaBoxNid..], MetaBoxNid);
            if (destination.Length >= OffsetReserved3 + 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetReserved3..], 0);
            }
        }
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0)
        {
            end = bytes.Length;
        }

        return Encoding.ASCII.GetString(bytes[..end]);
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        var count = Math.Min(destination.Length, value.Length);
        Encoding.ASCII.GetBytes(value.AsSpan(0, count), destination);
    }
}