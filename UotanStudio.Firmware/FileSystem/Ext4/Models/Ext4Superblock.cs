using System.Buffers.Binary;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.FileSystem.Ext4.Models;

internal sealed class Ext4Superblock
{
    public const int SuperblockOffset = 1024;

    public const ushort MagicValue = 0xEF53;

    public uint InodesCount { get; private init; }

    public uint BlocksCountLo { get; private init; }

    public uint BlocksCountHi { get; private init; }

    public uint FreeBlocksCountLo { get; private init; }

    public uint FreeBlocksCountHi { get; private init; }

    public uint FreeInodesCount { get; private init; }

    public uint FirstDataBlock { get; private init; }

    public uint LogBlockSize { get; private init; }

    public uint BlocksPerGroup { get; private init; }

    public uint InodesPerGroup { get; private init; }

    public ushort Magic { get; private init; }

    public uint RevLevel { get; private init; }

    public ushort InodeSize { get; private init; }

    public uint FeatureCompat { get; private init; }

    public uint FeatureIncompat { get; private init; }

    public uint FeatureRoCompat { get; private init; }

    public ushort Encoding { get; private init; }

    public ushort EncodingFlags { get; private init; }

    public ushort DescSize { get; private init; }

    public byte DefHashVersion { get; private init; }

    public uint JournalInum { get; private init; }

    public uint LastOrphan { get; private init; }

    public ushort State { get; private init; }

    public byte[] Uuid { get; private init; } = [];

    public string VolumeName { get; private init; } = string.Empty;

    public uint[] HashSeed { get; private init; } = [0, 0, 0, 0];

    public uint Checksum { get; private init; }

    public uint BackupGroup1 { get; private init; }

    public uint BackupGroup2 { get; private init; }

    /// <summary>RO_COMPAT_SPARSE_SUPER selects the classic backup-group sequence for superblock and descriptor copies. <para>RO_COMPAT_SPARSE_SUPER 决定超级块与描述符备份的经典备份组序列。</para></summary>
    /// <value><see langword="true"/> when ro-compat bit 0x1 is set. <para>ro-compat 位 0x1 置位时为 <see langword="true"/>。</para></value>
    public bool HasSparseSuper => (FeatureRoCompat & 0x0001) != 0;

    /// <summary>COMPAT_SPARSE_SUPER2 restricts superblock and descriptor-table backups to the two groups named by the superblock. <para>COMPAT_SPARSE_SUPER2 把超级块与描述符表备份限定为超级块中记录的两个组。</para></summary>
    /// <value><see langword="true"/> when compat bit 0x200 is set; the kernel then also mandates <c>s_first_data_block == 0</c>.
    /// <para>compat 位 0x200 置位时为 <see langword="true"/>；此时内核还强制要求 <c>s_first_data_block == 0</c>。</para></value>
    public bool HasSparseSuper2 => (FeatureCompat & 0x0200) != 0;

    public long BlockSize => 1024L << (int)LogBlockSize;

    public ulong BlocksCount => ((ulong)BlocksCountHi << 32) | BlocksCountLo;

    public ulong FreeBlocksCount => ((ulong)FreeBlocksCountHi << 32) | FreeBlocksCountLo;

    public int EffectiveDescSize => DescSize == 0 ? 32 : DescSize;

    public bool IsValid => Magic == MagicValue;

    /// <summary>
    /// Parses the superblock from the beginning of an image buffer (little-endian).
    /// <para>从镜像缓冲区起始处解析超级块（小端）。</para>
    /// </summary>
    /// <param name="data">Image bytes from offset 0, including the reserved boot region; the record itself starts at <see cref="SuperblockOffset"/> and needs 360 bytes there, while the backup-group, encoding and checksum fields are read only when the buffer reaches their offsets.
    /// <para>从偏移 0 起始的镜像字节，含保留的引导区；记录本身从 <see cref="SuperblockOffset"/> 开始且该处至少要有 360 字节，备份组、文件名编码与校验和字段只在缓冲区覆盖到相应偏移时才读取。</para></param>
    /// <returns>Superblock with the magic verified and both per-group counts non-zero, combining the 64-bit block counters, the NUL-trimmed volume name, the UUID, the four hash-seed words and zero for any field beyond the buffer.
    /// <para>已校验魔数且两个每组计数均非零的超级块，合并 64 位块计数、去除尾部 NUL 的卷标、UUID、四个哈希种子字，缓冲区未覆盖的字段取 0。</para></returns>
    public static Ext4Superblock FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < SuperblockOffset + 360)
        {
            throw new FileSystemCorruptedException($"Image too small to hold an ext4 superblock (need {SuperblockOffset + 360} bytes, got {data.Length}).");
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
            State = BinaryPrimitives.ReadUInt16LittleEndian(span[58..60]),
            RevLevel = BinaryPrimitives.ReadUInt32LittleEndian(span[76..80]),
            InodeSize = BinaryPrimitives.ReadUInt16LittleEndian(span[88..90]),
            FeatureCompat = BinaryPrimitives.ReadUInt32LittleEndian(span[92..96]),
            FeatureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(span[96..100]),
            FeatureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(span[100..104]),
            Encoding = span.Length >= 0x280 ? BinaryPrimitives.ReadUInt16LittleEndian(span[0x27C..]) : (ushort)0,
            EncodingFlags = span.Length >= 0x280 ? BinaryPrimitives.ReadUInt16LittleEndian(span[0x27E..]) : (ushort)0,
            Uuid = span[104..120].ToArray(),
            VolumeName = System.Text.Encoding.UTF8.GetStringTrimNul(span[120..136]),
            DefHashVersion = span[252],
            JournalInum = BinaryPrimitives.ReadUInt32LittleEndian(span[224..228]),
            LastOrphan = BinaryPrimitives.ReadUInt32LittleEndian(span[232..236]),
            HashSeed =
            [
                BinaryPrimitives.ReadUInt32LittleEndian(span[236..240]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[240..244]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[244..248]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[248..252]),
            ],
            DescSize = BinaryPrimitives.ReadUInt16LittleEndian(span[254..256]),
            BlocksCountHi = BinaryPrimitives.ReadUInt32LittleEndian(span[336..340]),
            FreeBlocksCountHi = BinaryPrimitives.ReadUInt32LittleEndian(span[344..348]),
            BackupGroup1 = span.Length >= 0x250 ? BinaryPrimitives.ReadUInt32LittleEndian(span[0x24C..]) : 0u,
            BackupGroup2 = span.Length >= 0x254 ? BinaryPrimitives.ReadUInt32LittleEndian(span[0x250..]) : 0u,
            Checksum = span.Length >= 1024
                ? BinaryPrimitives.ReadUInt32LittleEndian(span[1020..1024])
                : 0u,
        };
        if (!result.IsValid)
        {
            throw new FileSystemCorruptedException($"Invalid ext4 superblock magic 0x{result.Magic:X4} (expected 0xEF53).");
        }
        if (result.BlocksPerGroup == 0 || result.InodesPerGroup == 0)
        {
            throw new FileSystemCorruptedException("ext4 superblock has a zero blocks/inodes-per-group count.");
        }
        return result;
    }
}