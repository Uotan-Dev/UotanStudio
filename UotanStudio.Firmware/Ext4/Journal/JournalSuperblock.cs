using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.Ext4.Journal;

/// <summary>
/// JBD2 日志超级块结构（大端序）。
/// <para>JBD2 journal superblock structure (big-endian).</para>
/// </summary>
internal sealed class JournalSuperblock
{
    /// <summary>JBD2 魔数。<para>JBD2 magic number.</para></summary>
    public const uint Magic = 0xC03B3998;

    /// <summary>块类型：日志超级块 v2。<para>Block type: journal superblock v2.</para></summary>
    public const uint BlockTypeSuperblockV2 = 4;

    /// <summary>校验和类型：CRC32C。<para>Checksum type: CRC32C.</para></summary>
    public const byte ChecksumTypeCrc32c = 1;

    /// <summary>不兼容特性：revoke 块。<para>Incompat feature: revoke blocks.</para></summary>
    public const uint FeatureIncompatRevoke = 0x1;

    /// <summary>不兼容特性：64 位块号。<para>Incompat feature: 64-bit block numbers.</para></summary>
    public const uint FeatureIncompat64Bit = 0x2;

    /// <summary>不兼容特性：异步提交。<para>Incompat feature: async commit.</para></summary>
    public const uint FeatureIncompatAsyncCommit = 0x4;

    /// <summary>不兼容特性：v2 校验和。<para>Incompat feature: v2 checksums.</para></summary>
    public const uint FeatureIncompatCsumV2 = 0x8;

    /// <summary>不兼容特性：v3 校验和。<para>Incompat feature: v3 checksums.</para></summary>
    public const uint FeatureIncompatCsumV3 = 0x10;

    /// <summary>不兼容特性：fast commit。<para>Incompat feature: fast commit.</para></summary>
    public const uint FeatureIncompatFastCommit = 0x20;

    /// <summary>JBD2 块头大小（字节）。<para>JBD2 block header size in bytes.</para></summary>
    public const int HeaderSize = 12;

    /// <summary>日志超级块最小解析大小（字节）。<para>Minimum parse size for journal superblock.</para></summary>
    public const int MinimumSize = 0x204 + 4;

    /// <summary>日志超级块完整序列化大小（字节，含校验和字段）。<para>Fully serialized journal superblock size in bytes (checksum field included).</para></summary>
    public const int SerializedSize = 0x208;

    /// <summary>魔数。<para>Magic number.</para></summary>
    public uint HeaderMagic { get; init; }

    /// <summary>块类型。<para>Block type.</para></summary>
    public uint HeaderBlockType { get; init; }

    /// <summary>事务序列号。<para>Transaction sequence number.</para></summary>
    public uint HeaderSequence { get; init; }

    /// <summary>日志块大小（字节）。<para>Journal block size in bytes.</para></summary>
    public uint BlockSize { get; init; }

    /// <summary>日志总块数（不含超级块）。<para>Total journal blocks (excluding superblock).</para></summary>
    public uint MaxLen { get; init; }

    /// <summary>日志首个块号。<para>First block number of the journal log.</para></summary>
    public uint First { get; init; }

    /// <summary>首个待处理事务的序列号。<para>Sequence number of the first pending transaction.</para></summary>
    public uint Sequence { get; init; }

    /// <summary>首个有效事务的块号（0 = 空日志）。<para>Block number of the first valid transaction (0 = empty journal).</para></summary>
    public uint Start { get; init; }

    /// <summary>错误号（0 = 正常）。<para>Error number (0 = clean).</para></summary>
    public uint Errno { get; init; }

    /// <summary>兼容特性标志。<para>Compatible feature flags.</para></summary>
    public uint FeatureCompat { get; init; }

    /// <summary>不兼容特性标志。<para>Incompatible feature flags.</para></summary>
    public uint FeatureIncompat { get; init; }

    /// <summary>只读兼容特性标志。<para>Read-only compatible feature flags.</para></summary>
    public uint FeatureRoCompat { get; init; }

    /// <summary>日志 UUID（16 字节）。<para>Journal UUID (16 bytes).</para></summary>
    public byte[] Uuid { get; init; } = [];

    /// <summary>校验和类型（1 = CRC32C）。<para>Checksum type (1 = CRC32C).</para></summary>
    public byte ChecksumType { get; init; }

    /// <summary>超级块校验和。<para>Superblock checksum.</para></summary>
    public uint Checksum { get; init; }

    /// <summary>是否有 64 位块号支持。<para>Whether 64-bit block numbers are supported.</para></summary>
    public bool Has64Bit => (FeatureIncompat & FeatureIncompat64Bit) != 0;

    /// <summary>是否有 v3 校验和。<para>Whether v3 checksums are present.</para></summary>
    public bool HasCsumV3 => (FeatureIncompat & FeatureIncompatCsumV3) != 0;

    /// <summary>是否有 v2 校验和。<para>Whether v2 checksums are present.</para></summary>
    public bool HasCsumV2 => (FeatureIncompat & FeatureIncompatCsumV2) != 0;

    /// <summary>是否有异步提交。<para>Whether async commit is enabled.</para></summary>
    public bool HasAsyncCommit => (FeatureIncompat & FeatureIncompatAsyncCommit) != 0;

    /// <summary>是否有 revoke 块支持。<para>Whether revoke blocks are supported.</para></summary>
    public bool HasRevoke => (FeatureIncompat & FeatureIncompatRevoke) != 0;

    /// <summary>日志是否为空（Start == 0）。<para>Whether the journal is empty (Start == 0).</para></summary>
    public bool IsEmpty => Start == 0;

    /// <summary>
    /// 构造一个干净的初始空日志超级块（mke2fs 空日志语义：sequence=1、start=0、first=1、errno=0）。
    /// <paramref name="checksumV3"/> 为 true（默认）时启用 JBD2 csum v3：置 CsumV3 不兼容特性位、
    /// checksum_type=CRC32C，并由 <see cref="ToBytes"/> 写入 s_checksum。
    /// <para>Creates a clean initial empty-journal superblock (mke2fs empty-log semantics:
    /// sequence=1, start=0, first=1, errno=0). When <paramref name="checksumV3"/> is true (default),
    /// JBD2 checksum v3 is enabled: the CsumV3 incompat feature bit is set, checksum_type=CRC32C, and
    /// <see cref="ToBytes"/> writes s_checksum.</para>
    /// </summary>
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
    /// 将日志超级块序列化为 0x208 字节的大端序字节缓冲（v2/v3 布局：字段偏移与
    /// journal_superblock 0x00..0x50 一致，s_checksum 位于 0x204，与读侧模型及早期 JBD2 布局对齐）。
    /// 启用 csum v3 时按 JBD2 约定计算并写入 s_checksum：crc32c(~0) 覆盖 [0x00..0x204)，结果以
    /// <strong>大端</strong>存于 0x204。
    /// <para>Serializes the journal superblock to a 0x208-byte big-endian buffer (v2/v3 layout: field
    /// offsets 0x00..0x50 match the journal_superblock, with s_checksum at 0x204, aligned with the
    /// read-side model and the earlier JBD2 layout). With checksum v3 enabled, s_checksum is computed
    /// per the JBD2 convention: crc32c(~0) over [0x00..0x204), written <strong>big-endian</strong>
    /// at 0x204.</para>
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

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x40, 4), 1); // s_nr_users = 1
        data[0x50] = ChecksumType;

        if (HasCsumV3)
        {
            // 参考 jbd2_superblock_csum：crc32c(~0, sb[0..0x204))，无最终异或（crc32c_le 约定）。
            // Mirrors jbd2_superblock_csum: crc32c(~0, sb[0..0x204)) with no final XOR (crc32c_le).
            var crc = Crc32c(data.AsSpan(0, 0x204), 0xFFFF_FFFF);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x204, 4), crc);
        }

        return data;
    }

    private static readonly uint[] Crc32cTable = InitCrc32cTable();

    private static uint[] InitCrc32cTable()
    {
        // CRC-32C (Castagnoli) 反射表常量：多项式 0x1EDC6F41 的位反转 0x82F63B78。
        // Reflected table constant for CRC-32C: bit-reversed 0x1EDC6F41 = 0x82F63B78.
        const uint poly = 0x82F63B78;
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (uint)i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    /// <summary>
    /// 以指定寄存器 seed 计算 CRC32c（crc32c_le 约定：无最终异或）。
    /// <para>Computes CRC32c from the given register seed (crc32c_le convention: no final XOR).</para>
    /// </summary>
    private static uint Crc32c(ReadOnlySpan<byte> data, uint seed)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>
    /// 从原始字节解析 JBD2 日志超级块（大端序）。
    /// <para>Parses a JBD2 journal superblock from raw bytes (big-endian).</para>
    /// </summary>
    public static JournalSuperblock FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumSize)
        {
            throw new InvalidDataException(
                $"JBD2 journal superblock is truncated: need at least {MinimumSize} bytes, got {data.Length}.");
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(data[0..4]);
        if (magic != Magic)
        {
            throw new InvalidDataException($"Invalid JBD2 magic: 0x{magic:X8} (expected 0x{Magic:X8}).");
        }

        var blockType = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
        if (blockType is not (3 or 4))
        {
            throw new InvalidDataException($"Invalid JBD2 superblock block type: {blockType} (expected 3 or 4).");
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