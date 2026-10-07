using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.F2fs.Models;

internal sealed class F2fsSuperblock
{
    public const int SuperblockOffset = 1024;

    public const int SuperblockSize = 3072;

    public const uint Magic = 0xF2F52010u;

    public const int VolumeNameChars = 512;


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

    public uint MagicValue { get; init; }

    public ushort MajorVersion { get; init; }

    public ushort MinorVersion { get; init; }

    public uint LogSectorsize { get; init; }

    public uint LogSectorsPerBlock { get; init; }

    public uint LogBlocksize { get; init; }

    public uint LogBlocksPerSeg { get; init; }

    public uint SegsPerSec { get; init; }

    public uint SecsPerZone { get; init; }

    public uint ChecksumOffset { get; init; }

    public ulong BlockCount { get; init; }

    public uint SectionCount { get; init; }

    public uint SegmentCount { get; init; }

    public uint SegmentCountCkpt { get; init; }

    public uint SegmentCountSit { get; init; }

    public uint SegmentCountNat { get; init; }

    public uint SegmentCountSsa { get; init; }

    public uint SegmentCountMain { get; init; }

    public uint Segment0Blkaddr { get; init; }

    public uint CpBlkaddr { get; init; }

    public uint SitBlkaddr { get; init; }

    public uint NatBlkaddr { get; init; }

    public uint NodeBlkaddr => NatBlkaddr;

    public uint SsaBlkaddr { get; init; }

    public uint MainBlkaddr { get; init; }

    public uint RootIno { get; init; }

    public uint NodeIno { get; init; }

    public uint MetaIno { get; init; }

    public byte[] Uuid { get; init; } = [];

    public string VolumeName { get; init; } = string.Empty;

    public uint CpPayload { get; init; }

    public byte[] Version { get; init; } = [];

    public uint Feature { get; init; }

    public bool IsValid => MagicValue == Magic;

    public int BlockSize => 1 << (int)LogBlocksize;

    public int SectorSize => 1 << (int)LogSectorsize;

    public int SectorsPerBlock => 1 << (int)LogSectorsPerBlock;

    public int BlocksPerSeg => 1 << (int)LogBlocksPerSeg;

    public long SegmentSize => (long)BlockSize * BlocksPerSeg;

    /// <summary>
    /// Checks whether the image head contains the F2FS magic (4 bytes at offset 1024).
    /// <para>判断镜像头部是否包含 f2fs 魔数（偏移 1024 处 4 字节）。</para>
    /// </summary>
    /// <param name="image">Leading image bytes counted from offset 0; fewer than <see cref="SuperblockOffset"/> plus four bytes cannot hold the magic.
    /// <para>从偏移 0 计算的镜像前导字节；不足 <see cref="SuperblockOffset"/> 加 4 字节时不可能含魔数。</para></param>
    /// <returns><see langword="true"/> when the little-endian uint32 stored at <see cref="SuperblockOffset"/> equals <see cref="Magic"/>.
    /// <para><see cref="SuperblockOffset"/> 处的小端 uint32 等于 <see cref="Magic"/> 时返回 <see langword="true"/>。</para></returns>
    public static bool HasMagic(ReadOnlySpan<byte> image)
        => image.Length >= SuperblockOffset + 4
           && BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(SuperblockOffset, 4)) == Magic;

    /// <summary>
    /// Checks whether a 4-byte slice holds the magic value (little-endian 0xF2F52010).
    /// <para>判断 4 字节片段是否为魔数（小端 0xF2F52010）。</para>
    /// </summary>
    /// <param name="fourBytes">Slice taken from anywhere in the image; fewer than four bytes never match.
    /// <para>可取自镜像任意位置的片段；不足 4 字节时永不匹配。</para></param>
    /// <returns><see langword="true"/> when the slice's little-endian uint32 equals <see cref="Magic"/>.
    /// <para>片段的小端 uint32 等于 <see cref="Magic"/> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsMagicBytes(ReadOnlySpan<byte> fourBytes)
        => fourBytes.Length >= 4
           && BinaryPrimitives.ReadUInt32LittleEndian(fourBytes) == Magic;

    /// <summary>
    /// Parses the superblock from the superblock region (at least 3072 bytes starting at offset 1024); throws FormatException when the magic does not match.
    /// <para>从超级块区域（偏移 1024 起、长度至少 3072 字节）解析超级块；魔数不符时抛出 FormatException。</para>
    /// </summary>
    /// <param name="data">Superblock region counted from its own offset 0 and at least <see cref="SuperblockSize"/> bytes long, so the magic sits at the start of the span rather than at the image offset.
    /// <para>以其自身偏移 0 起始的超级块区域，长度至少为 <see cref="SuperblockSize"/> 字节，因此魔数位于该区间起始处而不是镜像偏移处。</para></param>
    /// <returns>Superblock holding the geometry exponents, section and segment counts, area start blocks, root, node and meta inode numbers, the 16-byte UUID, the volume name decoded up to the first zero UTF-16 unit, cp payload, 256 version bytes and the feature word.
    /// <para>返回的超级块含几何指数、section 与 segment 计数、各区域起始块、root/node/meta inode 号、16 字节 UUID、在首个零 UTF-16 单元前解码出的卷标、cp payload、256 字节版本信息以及 feature 字。</para></returns>
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
    /// Parses the superblock from a full image (locating offset 1024 automatically).
    /// <para>从完整镜像解析超级块（自动定位到偏移 1024）。</para>
    /// </summary>
    /// <param name="image">Image bytes from offset 0 including the reserved boot area; <see cref="SuperblockOffset"/> plus <see cref="SuperblockSize"/> bytes must be present.
    /// <para>从偏移 0 起始的镜像字节，含保留引导区；必须能覆盖 <see cref="SuperblockOffset"/> 加 <see cref="SuperblockSize"/> 字节。</para></param>
    /// <returns>Superblock read from the region beginning at <see cref="SuperblockOffset"/>, with the same magic requirement as <see cref="FromBytes"/>.
    /// <para>取自 <see cref="SuperblockOffset"/> 起始区域的超级块，魔数要求与 <see cref="FromBytes"/> 相同。</para></returns>
    public static F2fsSuperblock FromImage(ReadOnlySpan<byte> image)
    {
        if (image.Length < SuperblockOffset + SuperblockSize)
        {
            throw new FormatException($"Image too small to hold an F2FS superblock: {image.Length} bytes.");
        }

        return FromBytes(image.Slice(SuperblockOffset, SuperblockSize));
    }

    /// <summary>
    /// Serializes the superblock into a buffer of at least 3072 bytes (unimplemented tail fields stay zero).
    /// <para>将超级块序列化到缓冲区（长度至少 3072 字节；未实现的尾部字段保持为零）。</para>
    /// </summary>
    /// <param name="destination">Buffer offering at least <see cref="SuperblockSize"/> writable bytes; it is cleared entirely, the magic is written as the constant value, the UUID and version bytes are truncated to 16 and 256 bytes, the volume name is re-encoded as UTF-16 units and bytes of fields this model does not carry stay zero.
    /// <para>至少提供 <see cref="SuperblockSize"/> 个可写字节的缓冲区；整个缓冲区先清零，魔数按常量写入，UUID 与版本字节分别截断到 16 与 256 字节，卷标重新编码为 UTF-16 单元，本模型未承载字段对应的字节保持为 0。</para></param>
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