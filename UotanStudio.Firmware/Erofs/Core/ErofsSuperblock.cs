using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 超级块，位于镜像偏移 1024 处，按 Linux 内核 fs/erofs/erofs_fs.h 的小端字节序布局解析。
/// <para>EROFS superblock located at image offset 1024, parsed with the little-endian layout
/// defined in Linux kernel fs/erofs/erofs_fs.h.</para>
/// </summary>
internal sealed class ErofsSuperblock
{
    /// <summary>
    /// 超级块在镜像内的偏移。
    /// <para>Offset of the superblock within the image.</para>
    /// </summary>
    public const int SuperblockOffset = 1024;

    /// <summary>
    /// 超级块实际占用的字节数。
    /// <para>Size in bytes actually used by the superblock.</para>
    /// </summary>
    public const int SuperblockSize = 112;

    /// <summary>
    /// erofs 魔数（0xE0F5E1E2；磁盘上为小端字节 E2 E1 F5 E0）。
    /// <para>EROFS magic value (0xE0F5E1E2; little-endian bytes on disk: E2 E1 F5 E0).</para>
    /// </summary>
    public const uint Magic = 0xE0F5E1E2u;

    // 字段偏移（相对超级块起始处）。Field offsets relative to the superblock start.
    private const int OffsetMagic = 0;
    private const int OffsetChecksum = 4;
    private const int OffsetFeatureCompat = 8;
    private const int OffsetBlockSizeBits = 12;
    private const int OffsetSbExtSlots = 13;
    private const int OffsetRootNid = 14;
    private const int OffsetInos = 16;
    private const int OffsetBuildTime = 24;
    private const int OffsetBuildTimeNsec = 32;
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

    /// <summary>魔数。Magic value.</summary>
    public uint MagicValue { get; init; }

    /// <summary>校验和（本实现不计算）。Checksum (not computed by this implementation).</summary>
    public uint Checksum { get; init; }

    /// <summary>兼容特性位。Compatible feature flags.</summary>
    public uint FeatureCompat { get; init; }

    /// <summary>块大小移位（s_blkszbits，默认 12 → 4096 字节）。Block size in bit shift (s_blkszbits, default 12 → 4096 bytes).</summary>
    public byte BlockSizeBits { get; init; } = 12;

    /// <summary>扩展槽位数量。Number of extended superblock slots.</summary>
    public byte SbExtSlots { get; init; }

    /// <summary>根目录 inode 号（s_root_nid）。Nid of the root directory inode (s_root_nid).</summary>
    public ushort RootNid { get; init; }

    /// <summary>有效 inode 总数（s_inos）。Total number of valid inodes (s_inos).</summary>
    public ulong Inos { get; init; }

    /// <summary>构建时间（Unix 秒，s_build_time）。Build time in Unix seconds (s_build_time).</summary>
    public ulong BuildTime { get; init; }

    /// <summary>构建时间纳秒部分。Build time nanoseconds.</summary>
    public uint BuildTimeNsec { get; init; }

    /// <summary>文件系统总块数（s_blocks）。Total number of filesystem blocks (s_blocks).</summary>
    public uint Blocks { get; init; }

    /// <summary>元数据区起始块号（s_meta_blkaddr）。Start block number of the metadata area (s_meta_blkaddr).</summary>
    public uint MetaBlkAddr { get; init; }

    /// <summary>共享 xattr 区起始块号。Start block number of the shared xattr area.</summary>
    public uint XattrBlkAddr { get; init; }

    /// <summary>128 位 UUID（16 字节，s_uuid）。128-bit UUID (16 bytes, s_uuid).</summary>
    public byte[] Uuid { get; init; } = [];

    /// <summary>卷名（16 字节，NUL 填充，s_volume_name）。Volume name (16 bytes, NUL padded, s_volume_name).</summary>
    public string VolumeName { get; init; } = string.Empty;

    /// <summary>不兼容特性位。Incompatible feature flags.</summary>
    public uint FeatureIncompat { get; init; }

    /// <summary>可用压缩算法（bit0 = LZ4）。Available compression algorithms (bit 0 = LZ4).</summary>
    public ushort AvailableComprAlgs { get; init; }

    /// <summary>额外设备数量。Number of extra devices.</summary>
    public ushort ExtraDevices { get; init; }

    /// <summary>设备槽位偏移。Device slot offset.</summary>
    public ushort DevtSlotoff { get; init; }

    /// <summary>目录块大小移位。Directory block size in bit shift.</summary>
    public byte DirBlkBits { get; init; }

    /// <summary>
    /// 元数据块数（s_nid，近似值 Blocks - MetaBlkAddr）。标准磁盘格式并未直接存储纯元数据块数，
    /// 该近似值表示自元数据起始块到镜像末尾的块数（元数据 + 数据）。
    /// <para>Approximate metadata block count (s_nid, computed as Blocks - MetaBlkAddr). The standard
    /// on-disk format does not store the pure metadata block count; this approximates the blocks from
    /// the metadata start block to the end of the image (metadata + data).</para>
    /// </summary>
    public uint MetadataBlockCount => Blocks - MetaBlkAddr;

    /// <summary>块大小（字节）。Block size in bytes.</summary>
    public int BlockSize => 1 << BlockSizeBits;

    /// <summary>元数据区起始字节偏移。Byte offset of the metadata area start.</summary>
    public long MetadataByteOffset => (long)MetaBlkAddr * BlockSize;

    /// <summary>
    /// 根据 nid 计算 inode 的绝对字节偏移：(meta_blkaddr &lt;&lt; s_blkszbits) + nid。
    /// <para>Computes the absolute byte offset of an inode from its nid:
    /// (meta_blkaddr &lt;&lt; s_blkszbits) + nid.</para>
    /// </summary>
    public long GetInodeOffset(ulong nid) => MetadataByteOffset + (long)nid;

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
            throw new FormatException($"EROFS superblock region too short: {data.Length} bytes.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetMagic..]) != Magic)
        {
            throw new FormatException("Invalid EROFS superblock magic.");
        }

        return new ErofsSuperblock
        {
            MagicValue = Magic,
            Checksum = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetChecksum..]),
            FeatureCompat = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetFeatureCompat..]),
            BlockSizeBits = data[OffsetBlockSizeBits],
            SbExtSlots = data[OffsetSbExtSlots],
            RootNid = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetRootNid..]),
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
        };
    }

    /// <summary>
    /// 从完整镜像解析超级块（自动定位到偏移 1024）。
    /// <para>Parses the superblock from a full image (locating offset 1024 automatically).</para>
    /// </summary>
    public static ErofsSuperblock FromImage(ReadOnlySpan<byte> image)
    {
        if (image.Length < SuperblockOffset + SuperblockSize)
        {
            throw new FormatException($"Image too small to hold an EROFS superblock: {image.Length} bytes.");
        }

        return FromBytes(image.Slice(SuperblockOffset, SuperblockSize));
    }

    /// <summary>
    /// 将超级块序列化到缓冲区（长度至少 112 字节）。
    /// <para>Serializes the superblock into a buffer of at least 112 bytes.</para>
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
        BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetRootNid..], RootNid);
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
        destination[OffsetXattrPrefixCount] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetXattrPrefixStart..], 0);
        destination[OffsetXattrFilterReserved] = 0;
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