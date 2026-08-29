using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs 超级块，位于镜像偏移 1024 处，按 Linux 内核 include/linux/f2fs_fs.h 的
/// struct f2fs_super_block（__packed，小端）布局解析。
/// <para>F2FS superblock located at image offset 1024, parsed with the little-endian layout of
/// struct f2fs_super_block (packed) defined in Linux kernel include/linux/f2fs_fs.h.</para>
/// </summary>
internal sealed class F2fsSuperblock
{
    /// <summary>
    /// 超级块在镜像内的字节偏移。
    /// <para>Byte offset of the superblock within the image.</para>
    /// </summary>
    public const int SuperblockOffset = 1024;

    /// <summary>
    /// 超级块实际占用的字节数（sizeof(struct f2fs_super_block) = 3072）。
    /// <para>Size in bytes occupied by the superblock (sizeof(struct f2fs_super_block) = 3072).</para>
    /// </summary>
    public const int SuperblockSize = 3072;

    /// <summary>
    /// f2fs 魔数（0xF2F52010；磁盘上为小端字节 10 20 F5 F2）。
    /// <para>F2FS magic value (0xF2F52010; little-endian bytes on disk: 10 20 F5 F2).</para>
    /// </summary>
    public const uint Magic = 0xF2F52010u;

    /// <summary>
    /// 卷名字符数上限（MAX_VOLUME_NAME = 512，存储为 __le16 数组）。
    /// <para>Maximum volume name length in characters (MAX_VOLUME_NAME = 512, stored as a __le16 array).</para>
    /// </summary>
    public const int VolumeNameChars = 512;

    // 字段偏移（相对超级块起始处）。Field offsets relative to the superblock start.
    private const int OffsetMagic = 0;
    private const int OffsetMajorVer = 4;
    private const int OffsetMinorVer = 6;
    private const int OffsetLogSectorsize = 8;
    private const int OffsetLogSectorsPerBlock = 12;
    private const int OffsetLogBlocksize = 16;
    private const int OffsetLogBlocksPerSeg = 20;
    private const int OffsetSegsPerSec = 24;
    private const int OffsetSecsPerZone = 28;
    private const int OffsetChecksumOffset = 32;
    private const int OffsetBlockCount = 36;
    private const int OffsetSectionCount = 44;
    private const int OffsetSegmentCount = 48;
    private const int OffsetSegmentCountCkpt = 52;
    private const int OffsetSegmentCountSit = 56;
    private const int OffsetSegmentCountNat = 60;
    private const int OffsetSegmentCountSsa = 64;
    private const int OffsetSegmentCountMain = 68;
    private const int OffsetSegment0Blkaddr = 72;
    private const int OffsetCpBlkaddr = 76;
    private const int OffsetSitBlkaddr = 80;
    private const int OffsetNatBlkaddr = 84;
    private const int OffsetSsaBlkaddr = 88;
    private const int OffsetMainBlkaddr = 92;
    private const int OffsetRootIno = 96;
    private const int OffsetNodeIno = 100;
    private const int OffsetMetaIno = 104;
    private const int OffsetUuid = 108;
    private const int OffsetVolumeName = 124;
    private const int OffsetExtensionCount = 1148;
    private const int OffsetExtensionList = 1152;
    private const int OffsetCpPayload = 1664;
    private const int OffsetVersion = 1668;
    private const int OffsetInitVersion = 1924;
    private const int OffsetFeature = 2180;
    private const int OffsetEncryptionLevel = 2184;
    private const int OffsetCrc = 3068;

    /// <summary>魔数。Magic value.</summary>
    public uint MagicValue { get; init; }

    /// <summary>主版本号。Major version.</summary>
    public ushort MajorVersion { get; init; }

    /// <summary>次版本号。Minor version.</summary>
    public ushort MinorVersion { get; init; }

    /// <summary>log2(扇区大小)。log2 sector size in bytes.</summary>
    public uint LogSectorsize { get; init; }

    /// <summary>log2(每块扇区数)。log2 sectors per block.</summary>
    public uint LogSectorsPerBlock { get; init; }

    /// <summary>log2(块大小)。log2 block size in bytes.</summary>
    public uint LogBlocksize { get; init; }

    /// <summary>log2(每段块数)。log2 blocks per segment.</summary>
    public uint LogBlocksPerSeg { get; init; }

    /// <summary>每 section 的段数。Segments per section.</summary>
    public uint SegsPerSec { get; init; }

    /// <summary>每 zone 的 section 数。Sections per zone.</summary>
    public uint SecsPerZone { get; init; }

    /// <summary>超级块内校验和偏移。Checksum offset inside the super block.</summary>
    public uint ChecksumOffset { get; init; }

    /// <summary>文件系统总用户块数。Total user block count.</summary>
    public ulong BlockCount { get; init; }

    /// <summary>总 section 数。Total section count.</summary>
    public uint SectionCount { get; init; }

    /// <summary>总段数。Total segment count.</summary>
    public uint SegmentCount { get; init; }

    /// <summary>checkpoint 区段数。Segment count of the checkpoint area.</summary>
    public uint SegmentCountCkpt { get; init; }

    /// <summary>SIT 区段数。Segment count of the SIT area.</summary>
    public uint SegmentCountSit { get; init; }

    /// <summary>NAT 区段数。Segment count of the NAT area.</summary>
    public uint SegmentCountNat { get; init; }

    /// <summary>SSA 区段数。Segment count of the SSA area.</summary>
    public uint SegmentCountSsa { get; init; }

    /// <summary>主区段数。Segment count of the main area.</summary>
    public uint SegmentCountMain { get; init; }

    /// <summary>0 号段的起始块号。Start block address of segment 0.</summary>
    public uint Segment0Blkaddr { get; init; }

    /// <summary>checkpoint 区起始块号。Start block address of the checkpoint area.</summary>
    public uint CpBlkaddr { get; init; }

    /// <summary>SIT 区起始块号。Start block address of the SIT area.</summary>
    public uint SitBlkaddr { get; init; }

    /// <summary>NAT 区起始块号。Start block address of the NAT area.</summary>
    public uint NatBlkaddr { get; init; }

    /// <summary>节点区（NAT）起始块号，与 <see cref="NatBlkaddr"/> 同义。
    /// Node-area (NAT) start block, an alias of <see cref="NatBlkaddr"/>.</summary>
    public uint NodeBlkaddr => NatBlkaddr;

    /// <summary>SSA 区起始块号。Start block address of the SSA area.</summary>
    public uint SsaBlkaddr { get; init; }

    /// <summary>主区起始块号。Start block address of the main area.</summary>
    public uint MainBlkaddr { get; init; }

    /// <summary>根 inode 号。Root inode number.</summary>
    public uint RootIno { get; init; }

    /// <summary>node inode 号（通常为 1）。Node inode number (usually 1).</summary>
    public uint NodeIno { get; init; }

    /// <summary>meta inode 号（通常为 2）。Meta inode number (usually 2).</summary>
    public uint MetaIno { get; init; }

    /// <summary>128 位 UUID（16 字节）。128-bit UUID (16 bytes).</summary>
    public byte[] Uuid { get; init; } = [];

    /// <summary>卷名（512 个 __le16 字符，NUL 填充）。Volume name (512 __le16 chars, NUL padded).</summary>
    public string VolumeName { get; init; } = string.Empty;

    /// <summary>cp_payload（checkpoint 载荷块数）。Checkpoint payload block count.</summary>
    public uint CpPayload { get; init; }

    /// <summary>内核版本串（VERSION_LEN=256 字节）。Kernel version string (VERSION_LEN=256 bytes).</summary>
    public byte[] Version { get; init; } = [];

    /// <summary>特性位。Feature flags.</summary>
    public uint Feature { get; init; }

    /// <summary>
    /// 超级块是否通过魔数校验。
    /// <para>Whether the superblock passed the magic check.</para>
    /// </summary>
    public bool IsValid => MagicValue == Magic;

    /// <summary>块大小（字节）。Block size in bytes.</summary>
    public int BlockSize => 1 << (int)LogBlocksize;

    /// <summary>扇区大小（字节）。Sector size in bytes.</summary>
    public int SectorSize => 1 << (int)LogSectorsize;

    /// <summary>每块扇区数。Sectors per block.</summary>
    public int SectorsPerBlock => 1 << (int)LogSectorsPerBlock;

    /// <summary>每段块数。Blocks per segment.</summary>
    public int BlocksPerSeg => 1 << (int)LogBlocksPerSeg;

    /// <summary>段大小（字节）。Segment size in bytes.</summary>
    public long SegmentSize => (long)BlockSize * BlocksPerSeg;

    /// <summary>
    /// 判断镜像头部是否包含 f2fs 魔数（偏移 1024 处 4 字节）。
    /// <para>Checks whether the image head contains the F2FS magic (4 bytes at offset 1024).</para>
    /// </summary>
    public static bool HasMagic(ReadOnlySpan<byte> image)
        => image.Length >= SuperblockOffset + 4
           && BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(SuperblockOffset, 4)) == Magic;

    /// <summary>
    /// 判断 4 字节片段是否为魔数（小端 0xF2F52010）。
    /// <para>Checks whether a 4-byte slice holds the magic value (little-endian 0xF2F52010).</para>
    /// </summary>
    public static bool IsMagicBytes(ReadOnlySpan<byte> fourBytes)
        => fourBytes.Length >= 4
           && BinaryPrimitives.ReadUInt32LittleEndian(fourBytes) == Magic;

    /// <summary>
    /// 从超级块区域（偏移 1024 起、长度至少 3072 字节）解析超级块；魔数不符时抛出 FormatException。
    /// <para>Parses the superblock from the superblock region (at least 3072 bytes starting at offset
    /// 1024); throws FormatException when the magic does not match.</para>
    /// </summary>
    public static F2fsSuperblock FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < SuperblockSize)
        {
            throw new FormatException($"F2FS superblock region too short: {data.Length} bytes.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetMagic..]) != Magic)
        {
            throw new FormatException("Invalid F2FS superblock magic.");
        }

        return new F2fsSuperblock
        {
            MagicValue = Magic,
            MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetMajorVer..]),
            MinorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetMinorVer..]),
            LogSectorsize = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetLogSectorsize..]),
            LogSectorsPerBlock = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetLogSectorsPerBlock..]),
            LogBlocksize = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetLogBlocksize..]),
            LogBlocksPerSeg = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetLogBlocksPerSeg..]),
            SegsPerSec = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSegsPerSec..]),
            SecsPerZone = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSecsPerZone..]),
            ChecksumOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetChecksumOffset..]),
            BlockCount = BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetBlockCount..]),
            SectionCount = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSectionCount..]),
            SegmentCount = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSegmentCount..]),
            SegmentCountCkpt = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSegmentCountCkpt..]),
            SegmentCountSit = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSegmentCountSit..]),
            SegmentCountNat = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSegmentCountNat..]),
            SegmentCountSsa = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSegmentCountSsa..]),
            SegmentCountMain = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSegmentCountMain..]),
            Segment0Blkaddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSegment0Blkaddr..]),
            CpBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetCpBlkaddr..]),
            SitBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSitBlkaddr..]),
            NatBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetNatBlkaddr..]),
            SsaBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetSsaBlkaddr..]),
            MainBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetMainBlkaddr..]),
            RootIno = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetRootIno..]),
            NodeIno = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetNodeIno..]),
            MetaIno = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetMetaIno..]),
            Uuid = data.Slice(OffsetUuid, 16).ToArray(),
            VolumeName = ReadVolumeName(data.Slice(OffsetVolumeName, VolumeNameChars * 2)),
            CpPayload = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetCpPayload..]),
            Version = data.Slice(OffsetVersion, 256).ToArray(),
            Feature = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetFeature..]),
        };
    }

    /// <summary>
    /// 从完整镜像解析超级块（自动定位到偏移 1024）。
    /// <para>Parses the superblock from a full image (locating offset 1024 automatically).</para>
    /// </summary>
    public static F2fsSuperblock FromImage(ReadOnlySpan<byte> image)
    {
        if (image.Length < SuperblockOffset + SuperblockSize)
        {
            throw new FormatException($"Image too small to hold an F2FS superblock: {image.Length} bytes.");
        }

        return FromBytes(image.Slice(SuperblockOffset, SuperblockSize));
    }

    /// <summary>
    /// 将超级块序列化到缓冲区（长度至少 3072 字节；未实现的尾部字段保持为零）。
    /// <para>Serializes the superblock into a buffer of at least 3072 bytes (unimplemented tail
    /// fields stay zero).</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SuperblockSize)
        {
            throw new ArgumentException("Superblock destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetMagic..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetMajorVer..], MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetMinorVer..], MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetLogSectorsize..], LogSectorsize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetLogSectorsPerBlock..], LogSectorsPerBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetLogBlocksize..], LogBlocksize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetLogBlocksPerSeg..], LogBlocksPerSeg);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSegsPerSec..], SegsPerSec);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSecsPerZone..], SecsPerZone);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetChecksumOffset..], ChecksumOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetBlockCount..], BlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSectionCount..], SectionCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSegmentCount..], SegmentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSegmentCountCkpt..], SegmentCountCkpt);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSegmentCountSit..], SegmentCountSit);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSegmentCountNat..], SegmentCountNat);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSegmentCountSsa..], SegmentCountSsa);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSegmentCountMain..], SegmentCountMain);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSegment0Blkaddr..], Segment0Blkaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetCpBlkaddr..], CpBlkaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSitBlkaddr..], SitBlkaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetNatBlkaddr..], NatBlkaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetSsaBlkaddr..], SsaBlkaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetMainBlkaddr..], MainBlkaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetRootIno..], RootIno);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetNodeIno..], NodeIno);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetMetaIno..], MetaIno);
        Uuid.AsSpan(0, Math.Min(16, Uuid.Length)).CopyTo(destination[OffsetUuid..]);
        WriteVolumeName(destination.Slice(OffsetVolumeName, VolumeNameChars * 2), VolumeName);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetCpPayload..], CpPayload);
        Version.AsSpan(0, Math.Min(256, Version.Length)).CopyTo(destination[OffsetVersion..]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetFeature..], Feature);
    }

    private static string ReadVolumeName(ReadOnlySpan<byte> data)
    {
        var buffer = new char[VolumeNameChars];
        var count = 0;
        for (var i = 0; i < VolumeNameChars; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
            if (value == 0)
            {
                break;
            }

            buffer[count++] = (char)(value & 0xFF);
        }

        return count == 0 ? string.Empty : new string(buffer, 0, count);
    }

    private static void WriteVolumeName(Span<byte> destination, string value)
    {
        destination.Clear();
        var count = Math.Min(VolumeNameChars, value.Length);
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(i * 2, 2), (ushort)(value[i] & 0xFF));
        }
    }
}