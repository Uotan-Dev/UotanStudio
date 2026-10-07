using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.FileSystem.Ext4.Journal;

internal sealed class JournalSuperblock
{
    public const uint Magic = 0xC03B3998;

    public const uint BlockTypeSuperblockV2 = 4;

    public const byte ChecksumTypeCrc32c = 1;

    public const uint FeatureIncompatRevoke = 0x1;

    public const uint FeatureIncompat64Bit = 0x2;

    public const uint FeatureIncompatAsyncCommit = 0x4;

    public const uint FeatureIncompatCsumV2 = 0x8;

    public const uint FeatureIncompatCsumV3 = 0x10;

    public const uint FeatureIncompatFastCommit = 0x20;

    public const int HeaderSize = 12;

    public const int MinimumSize = 0x204 + 4;

    public const int SerializedSize = 0x208;

    public uint HeaderMagic { get; init; }

    public uint HeaderBlockType { get; init; }

    public uint HeaderSequence { get; init; }

    public uint BlockSize { get; init; }

    public uint MaxLen { get; init; }

    public uint First { get; init; }

    public uint Sequence { get; init; }

    public uint Start { get; init; }

    public uint Errno { get; init; }

    public uint FeatureCompat { get; init; }

    public uint FeatureIncompat { get; init; }

    public uint FeatureRoCompat { get; init; }

    public byte[] Uuid { get; init; } = [];

    public byte ChecksumType { get; init; }

    public uint Checksum { get; init; }

    public bool Has64Bit => (FeatureIncompat & FeatureIncompat64Bit) != 0;

    public bool HasCsumV3 => (FeatureIncompat & FeatureIncompatCsumV3) != 0;

    public bool HasCsumV2 => (FeatureIncompat & FeatureIncompatCsumV2) != 0;

    public bool HasAsyncCommit => (FeatureIncompat & FeatureIncompatAsyncCommit) != 0;

    public bool HasRevoke => (FeatureIncompat & FeatureIncompatRevoke) != 0;

    public bool IsEmpty => Start == 0;

    /// <summary>
    /// Creates a clean initial empty-journal superblock (mke2fs empty-log semantics: sequence=1, start=0, first=1, errno=0).
    /// <para>构造一个干净的初始空日志超级块（mke2fs 空日志语义：sequence=1、start=0、first=1、errno=0）。</para>
    /// </summary>
    /// <param name="blockSize">Journal block size in bytes, at least 1024 and a power of two.
    /// <para>日志块大小（字节），至少 1024 且必须是 2 的整数次幂。</para></param>
    /// <param name="maxLen">Journal length in blocks, at least 2.
    /// <para>日志的块数，至少 2。</para></param>
    /// <param name="uuid">16-byte journal UUID copied into the record; <see langword="null"/> fills the field with 16 random bytes, and any other length is rejected.
    /// <para>写入记录的 16 字节日志 UUID；为 <see langword="null"/> 时用 16 个随机字节填充，其他长度会被拒绝。</para></param>
    /// <param name="checksumV3">When <see langword="true"/> the incompatibility features and checksum type are set to metadata checksums v3, leaving both at zero when <see langword="false"/>.
    /// <para>为 <see langword="true"/> 时把 incompat 特性与校验和类型设为 v3 元数据校验和，为 <see langword="false"/> 时两者保持 0。</para></param>
    /// <returns>A v2 journal superblock with sequence 1, start 0, first 1, errno 0 and a header sequence of 0.
    /// <para>v2 日志超级块，其 sequence 为 1、start 为 0、first 为 1、errno 为 0，头部序列号为 0。</para></returns>
    public static JournalSuperblock CreateEmpty(uint blockSize, uint maxLen, byte[]? uuid = null, bool checksumV3 = true)
    {
        if (blockSize < 1024 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Journal block size must be at least 1024 and a power of two (got {blockSize}).");
        }

        if (maxLen < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLen), $"Journal length must be at least 2 blocks (got {maxLen}).");
        }

        if (uuid is { Length: not 16 })
        {
            throw new ArgumentException("Journal UUID must be 16 bytes.", nameof(uuid));
        }

        return new JournalSuperblock
        {
            HeaderMagic = Magic,
            HeaderBlockType = BlockTypeSuperblockV2,
            HeaderSequence = 0,
            BlockSize = blockSize,
            MaxLen = maxLen,
            First = 1,
            Sequence = 1,
            Start = 0,
            Errno = 0,
            FeatureCompat = 0,
            FeatureIncompat = checksumV3 ? FeatureIncompatCsumV3 : 0u,
            FeatureRoCompat = 0,
            Uuid = uuid is { Length: 16 } ? (byte[])uuid.Clone() : RandomNumberGenerator.GetBytes(16),
            ChecksumType = checksumV3 ? ChecksumTypeCrc32c : (byte)0,
            Checksum = 0,
        };
    }

    /// <summary>
    /// Serializes the journal superblock to a 0x208-byte big-endian buffer (v2/v3 layout: field offsets 0x00..0x50 match the journal_superblock, with s_checksum at 0x204, aligned with the read-side model and the earlier JBD2 layout).
    /// <para>将日志超级块序列化为 0x208 字节的大端序字节缓冲（v2/v3 布局：字段偏移与 journal_superblock 0x00..0x50 一致，s_checksum 位于 0x204，与读侧模型及早期 JBD2 布局对齐）。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[SerializedSize];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), HeaderMagic);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), HeaderBlockType);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), HeaderSequence);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C, 4), BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x10, 4), MaxLen);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x14, 4), First);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x18, 4), Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x1C, 4), Start);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x20, 4), Errno);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x24, 4), FeatureCompat);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x28, 4), FeatureIncompat);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x2C, 4), FeatureRoCompat);
        var uuid = Uuid;
        if (uuid.Length > 0)
        {
            uuid.AsSpan(0, Math.Min(uuid.Length, 16)).CopyTo(data.AsSpan(0x30, Math.Min(uuid.Length, 16)));
        }

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x40, 4), 1);
        data[0x50] = ChecksumType;

        if (HasCsumV3)
        {


            var crc = Crc32c(data.AsSpan(0, 0x204), UotanStudio.Firmware.Core.Checksums.Crc32c.InitialSeed);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x204, 4), crc);
        }

        return data;
    }

    /// <summary>
    /// Computes CRC32c from the given register seed (crc32c_le convention: no final XOR).
    /// <para>以指定寄存器 seed 计算 CRC32c（crc32c_le 约定：无最终异或）。</para>
    /// </summary>
    private static uint Crc32c(ReadOnlySpan<byte> data, uint seed) => UotanStudio.Firmware.Core.Checksums.Crc32c.Compute(data, seed);

    /// <summary>
    /// Parses a JBD2 journal superblock from raw bytes (big-endian).
    /// <para>从原始字节解析 JBD2 日志超级块（大端序）。</para>
    /// </summary>
    /// <param name="data">Raw journal superblock bytes, at least <see cref="MinimumSize"/> long, holding the JBD2 magic, a superblock block type of 3 or 4, the big-endian descriptor fields, the 16-byte UUID at 0x30, the checksum type at 0x50 and the checksum at 0x204.
    /// <para>原始日志超级块字节，长度至少为 <see cref="MinimumSize"/>，依次包含 JBD2 魔数、块类型 3 或 4、大端序描述符字段、0x30 处的 16 字节 UUID、0x50 处的校验和类型与 0x204 处的校验和。</para></param>
    /// <returns>Parsed superblock carrying the descriptor fields, the UUID read from 0x30, the checksum type from 0x50 and the stored checksum; <see cref="IsEmpty"/> reports whether the journal points at any data.
    /// <para>解析出的超级块，含描述符字段、0x30 读出的 UUID、0x50 的校验和类型与存储的校验和；<see cref="IsEmpty"/> 反映日志是否指向数据。</para></returns>
    public static JournalSuperblock FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumSize)
        {
            throw new FileSystemCorruptedException(
                $"JBD2 journal superblock is truncated: need at least {MinimumSize} bytes, got {data.Length}.");
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(data[0..4]);
        if (magic != Magic)
        {
            throw new FileSystemCorruptedException($"Invalid JBD2 magic: 0x{magic:X8} (expected 0x{Magic:X8}).");
        }

        var blockType = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
        if (blockType is not (3 or 4))
        {
            throw new FileSystemCorruptedException($"Invalid JBD2 superblock block type: {blockType} (expected 3 or 4).");
        }

        var checksumType = data.Length > 0x50 ? data[0x50] : (byte)0;
        var uuid = data.Length >= 0x40 ? data[0x30..0x40].ToArray() : [];

        return new JournalSuperblock
        {
            HeaderMagic = magic,
            HeaderBlockType = blockType,
            HeaderSequence = BinaryPrimitives.ReadUInt32BigEndian(data[8..12]),
            BlockSize = BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..0x10]),
            MaxLen = BinaryPrimitives.ReadUInt32BigEndian(data[0x10..0x14]),
            First = BinaryPrimitives.ReadUInt32BigEndian(data[0x14..0x18]),
            Sequence = BinaryPrimitives.ReadUInt32BigEndian(data[0x18..0x1C]),
            Start = BinaryPrimitives.ReadUInt32BigEndian(data[0x1C..0x20]),
            Errno = BinaryPrimitives.ReadUInt32BigEndian(data[0x20..0x24]),
            FeatureCompat = BinaryPrimitives.ReadUInt32BigEndian(data[0x24..0x28]),
            FeatureIncompat = BinaryPrimitives.ReadUInt32BigEndian(data[0x28..0x2C]),
            FeatureRoCompat = BinaryPrimitives.ReadUInt32BigEndian(data[0x2C..0x30]),
            Uuid = uuid,
            ChecksumType = checksumType,
            Checksum = data.Length >= 0x208
                ? BinaryPrimitives.ReadUInt32BigEndian(data[0x204..0x208])
                : 0u,
        };
    }
}