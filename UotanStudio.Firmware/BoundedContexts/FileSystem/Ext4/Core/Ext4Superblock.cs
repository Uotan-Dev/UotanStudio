using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Core;

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

    public ushort DescSize { get; private init; }

    public byte DefHashVersion { get; private init; }

    public uint JournalInum { get; private init; }

    public uint LastOrphan { get; private init; }

    public ushort State { get; private init; }

    public byte[] Uuid { get; private init; } = [];

    public uint[] HashSeed { get; private init; } = [0, 0, 0, 0];

    public uint Checksum { get; private init; }

    public long BlockSize => 1024L << (int)LogBlockSize;

    public ulong BlocksCount => ((ulong)BlocksCountHi << 32) | BlocksCountLo;

    public ulong FreeBlocksCount => ((ulong)FreeBlocksCountHi << 32) | FreeBlocksCountLo;

    public int EffectiveDescSize => DescSize == 0 ? 32 : DescSize;

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
            State = BinaryPrimitives.ReadUInt16LittleEndian(span[58..60]),
            RevLevel = BinaryPrimitives.ReadUInt32LittleEndian(span[76..80]),
            InodeSize = BinaryPrimitives.ReadUInt16LittleEndian(span[88..90]),
            FeatureCompat = BinaryPrimitives.ReadUInt32LittleEndian(span[92..96]),
            FeatureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(span[96..100]),
            FeatureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(span[100..104]),
            Uuid = span[104..120].ToArray(),
            DefHashVersion = span[234],
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
            Checksum = span.Length >= 360 + 60
                ? BinaryPrimitives.ReadUInt32LittleEndian(span[356..360])
                : 0u,
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
