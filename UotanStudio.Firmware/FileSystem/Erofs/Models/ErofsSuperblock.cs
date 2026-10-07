using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

internal sealed class ErofsSuperblock
{
    public const int SuperblockOffset = 1024;

    public const int SuperblockSize = 128;

    public const int ExtSlotSize = 16;

    public const uint Magic = 0xE0F5E1E2u;

    public const int CompactInodeSize = 32;

    public const int DeviceSlotSize = 128;

    public const uint CompatSbChecksum = 0x00000001;
    public const uint CompatMtime = 0x00000002;
    public const uint CompatXattrFilter = 0x00000004;
    public const uint CompatSharedEaInMetabox = 0x00000008;
    public const uint CompatPlainXattrPfx = 0x00000010;
    public const uint CompatIshareXattrs = 0x00000020;

    public const uint IncompatLz4_0Padding = 0x00000001;
    public const uint IncompatCompressCfgs = 0x00000002;
    public const uint IncompatBigPcluster = 0x00000002;
    public const uint IncompatChunkedFile = 0x00000004;
    public const uint IncompatDeviceTable = 0x00000008;
    public const uint IncompatCompressHead2 = 0x00000008;
    public const uint IncompatZtailpacking = 0x00000010;
    public const uint IncompatFragments = 0x00000020;
    public const uint IncompatDedupe = 0x00000020;
    public const uint IncompatXattrPrefixes = 0x00000040;
    public const uint Incompat48Bit = 0x00000080;
    public const uint IncompatMetabox = 0x00000100;

    public const uint AllIncompat = (IncompatMetabox << 1) - 1;

    private const int OffsetMagic = 0;
    private const int OffsetChecksum = 4;
    private const int OffsetFeatureCompat = 8;
    private const int OffsetBlockSizeBits = 12;
    private const int OffsetSbExtSlots = 13;
    private const int OffsetRootNidOrBlocksHi = 14;
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
    private const int OffsetIshareXattrPrefixId = 105;
    private const int OffsetBuildTime32 = 108;
    private const int OffsetRootNid8b = 112;
    private const int OffsetReserved2 = 120;
    private const int OffsetMetaBoxNid = 128;
    private const int OffsetReserved3 = 136;

    public uint MagicValue { get; init; }

    public uint Checksum { get; init; }

    public uint FeatureCompat { get; init; }

    public byte BlockSizeBits { get; init; } = 12;

    public byte SbExtSlots { get; init; }

    public ushort RootNid { get; init; }

    public ulong Inos { get; init; }

    public ulong BuildTime { get; init; }

    public uint BuildTimeNsec { get; init; }

    public uint Blocks { get; init; }

    public uint MetaBlkAddr { get; init; }

    public uint XattrBlkAddr { get; init; }

    public byte[] Uuid { get; init; } = [];

    public string VolumeName { get; init; } = string.Empty;

    internal byte[] VolumeNameBytes { get; init; } = [];

    public uint FeatureIncompat { get; init; }

    public ushort AvailableComprAlgs { get; init; }

    public ushort ExtraDevices { get; init; }

    public ushort DevtSlotoff { get; init; }

    public byte DirBlkBits { get; init; }

    public byte XattrPrefixCount { get; init; }

    public uint XattrPrefixStart { get; init; }

    public ulong PackedNid { get; init; }

    public byte XattrFilterReserved { get; init; }

    public byte IshareXattrPrefixId { get; init; }

    public uint BuildTime32 { get; init; }

    public ulong RootNid8b { get; init; }

    public ulong MetaBoxNid { get; init; }

    public bool HasCompatChecksum => (FeatureCompat & CompatSbChecksum) != 0;
    public bool HasCompatMtime => (FeatureCompat & CompatMtime) != 0;
    public bool HasCompatXattrFilter => (FeatureCompat & CompatXattrFilter) != 0;
    public bool Has48Bit => (FeatureIncompat & Incompat48Bit) != 0;
    public bool HasBigPcluster => (FeatureIncompat & IncompatBigPcluster) != 0;
    public bool HasChunkedFile => (FeatureIncompat & IncompatChunkedFile) != 0;
    public bool HasDeviceTable => (FeatureIncompat & IncompatDeviceTable) != 0;
    public bool HasZtailpacking => (FeatureIncompat & IncompatZtailpacking) != 0;
    public bool HasFragments => (FeatureIncompat & IncompatFragments) != 0;
    public bool HasXattrPrefixes => (FeatureIncompat & IncompatXattrPrefixes) != 0;
    public bool HasMetabox => (FeatureIncompat & IncompatMetabox) != 0;

    public ulong MetadataBlockCount => BlockCount64 - MetaBlkAddr;

    public int BlockSize => 1 << BlockSizeBits;

    public long MetadataByteOffset => MetaBlkAddr * BlockSize;

    public ulong BlockCount64 => Has48Bit
        ? ((ulong)BlocksHi << 32) | Blocks
        : Blocks;

    public ulong RootNid64 => Has48Bit ? RootNid8b : RootNid;

    public ushort BlocksHi { get; init; }

    public ushort EffectiveComprAlgs => HasBigPcluster ? AvailableComprAlgs : (ushort)1;

    /// <summary>
    /// Computes the absolute byte offset of an inode from its nid: (meta_blkaddr &lt;&lt; s_blkszbits) + nid.
    /// <para>根据 nid 计算 inode 的绝对字节偏移：(meta_blkaddr &lt;&lt; s_blkszbits) + nid。</para>
    /// </summary>
    /// <param name="nid">Inode number counted in <see cref="CompactInodeSize"/>-byte slots from the start of the
    /// metadata area. <para>自元数据区起点、按 <see cref="CompactInodeSize"/> 字节槽计位的 inode 编号。</para></param>
    /// <returns>The absolute image offset of that inode: <see cref="MetadataByteOffset"/> plus
    /// <paramref name="nid"/> scaled by <see cref="CompactInodeSize"/>.
    /// <para>该 inode 的镜像绝对偏移：<see cref="MetadataByteOffset"/> 加上 <paramref name="nid"/> 与
    /// <see cref="CompactInodeSize"/> 的乘积。</para></returns>
    public long GetInodeOffset(ulong nid) => MetadataByteOffset + (long)nid * CompactInodeSize;

    /// <summary>
    /// Checks whether the image contains the EROFS magic (4 bytes at offset 1024).
    /// <para>判断镜像头部是否包含 erofs 魔数（偏移 1024 处 4 字节）。</para>
    /// </summary>
    /// <param name="image">Leading image bytes; a span shorter than <see cref="SuperblockOffset"/> + 4 cannot
    /// reach the magic field. <para>镜像起始字节；跨度短于 <see cref="SuperblockOffset"/> + 4 时无法触及魔数字段。</para></param>
    /// <returns><see langword="true"/> when the four little-endian bytes at <see cref="SuperblockOffset"/> equal
    /// <see cref="Magic"/>; otherwise <see langword="false"/>.
    /// <para>偏移 <see cref="SuperblockOffset"/> 处的 4 字节小端值等于 <see cref="Magic"/> 时为
    /// <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public static bool HasMagic(ReadOnlySpan<byte> image)
        => image.Length >= SuperblockOffset + 4
           && BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(SuperblockOffset, 4)) == Magic;

    /// <summary>
    /// Checks whether a 4-byte slice holds the magic value (little-endian 0xE0F5E1E2).
    /// <para>判断 4 字节片段是否为魔数（小端 0xE0F5E1E2）。</para>
    /// </summary>
    /// <param name="fourBytes">Candidate slice read little-endian; fewer than four bytes are never
    /// accepted. <para>按小端读取的候选片段；不足 4 字节时一律不接受。</para></param>
    /// <returns><see langword="true"/> when <paramref name="fourBytes"/> holds at least four bytes equal to
    /// <see cref="Magic"/> in little-endian order; otherwise <see langword="false"/>.
    /// <para><paramref name="fourBytes"/> 至少含 4 字节且按小端等于 <see cref="Magic"/> 时为
    /// <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public static bool IsMagicBytes(ReadOnlySpan<byte> fourBytes)
        => fourBytes.Length >= 4
           && BinaryPrimitives.ReadUInt32LittleEndian(fourBytes) == Magic;

    /// <summary>
    /// Validates the superblock's structural feature fields: the block-size shift must be within 9..16 and every incompatible feature bit must be one the reader understands. Both the parser and the builder route their superblock checks here so the rules live in one place.
    /// <para>校验超级块的结构特性字段：块大小位移须处于 9..16，且每个不兼容特性位都必须是读取器可识别的位。解析器与构建器都将超级块校验汇聚到此处，使规则只有一处定义。</para>
    /// </summary>
    /// <exception cref="FormatException">
    /// <see cref="BlockSizeBits"/> is below 9 or above 16, or <see cref="FeatureIncompat"/> sets a bit outside <see cref="AllIncompat"/>.
    /// <para><see cref="BlockSizeBits"/> 小于 9 或大于 16，或 <see cref="FeatureIncompat"/> 置位了 <see cref="AllIncompat"/> 之外的位。</para>
    /// </exception>
    internal void Validate()
    {
        if (BlockSizeBits is < 9 or > 16)
        {
            throw new FormatException(
                $"Unsupported EROFS block size shift {BlockSizeBits} (expected 9..16).");
        }

        var unknownIncompat = FeatureIncompat & ~AllIncompat;
        if (unknownIncompat != 0)
        {
            throw new FormatException(
                $"EROFS image uses unknown incompatible feature bits 0x{unknownIncompat:X8}.");
        }
    }

    /// <summary>
    /// Guards that a computed total block count still fits the 32-bit <see cref="Blocks"/> superblock field before the builder truncating-casts it.
    /// <para>在构建器以截断方式转换之前，确保计算得到的总块数仍适合 32 位的 <see cref="Blocks"/> 超级块字段。</para>
    /// </summary>
    /// <param name="blockCount">The computed total block count to store in the superblock. <para>要写入超级块的计算总块数。</para></param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="blockCount"/> exceeds <see cref="uint.MaxValue"/>. <para><paramref name="blockCount"/> 超过 <see cref="uint.MaxValue"/>。</para>
    /// </exception>
    internal static void ValidateBlockCountField(long blockCount)
    {
        if (blockCount > uint.MaxValue)
        {
            throw new InvalidOperationException($"Image block count {blockCount} exceeds the 32-bit superblock field.");
        }
    }

    /// <summary>
    /// Guards that a resolved root inode number still fits the 16-bit <see cref="RootNid"/> superblock field before the builder truncating-casts it.
    /// <para>在构建器以截断方式转换之前，确保解析出的根 inode 编号仍适合 16 位的 <see cref="RootNid"/> 超级块字段。</para>
    /// </summary>
    /// <param name="rootNid">The resolved root inode number to store in the superblock. <para>要写入超级块的已解析根 inode 编号。</para></param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="rootNid"/> exceeds <see cref="ushort.MaxValue"/>. <para><paramref name="rootNid"/> 超过 <see cref="ushort.MaxValue"/>。</para>
    /// </exception>
    internal static void ValidateRootNidField(ulong rootNid)
    {
        if (rootNid > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Root nid {rootNid} exceeds the 16-bit superblock field.");
        }
    }

    /// <summary>
    /// Parses the superblock from the superblock region (at least 112 bytes starting at offset 1024); throws FormatException when the magic does not match.
    /// <para>从超级块区域（偏移 1024 起、长度至少 112 字节）解析超级块；魔数不符时抛出 FormatException。</para>
    /// </summary>
    /// <param name="data">Superblock region starting at the magic field rather than at
    /// <see cref="SuperblockOffset"/>; fewer than 112 bytes are rejected, and the trailing 48-bit fields decode
    /// only when <paramref name="data"/> reaches their offsets.
    /// <para>起始于魔数字段而非 <see cref="SuperblockOffset"/> 的超级块区域；不足 112 字节会被拒绝，
    /// 尾部 48 位字段仅当 <paramref name="data"/> 覆盖其偏移时才解析。</para></param>
    /// <returns>The decoded superblock, whose <see cref="MagicValue"/> carries the checked magic.
    /// <para>解码得到的超级块，其 <see cref="MagicValue"/> 为已校验的魔数。</para></returns>
    public static ErofsSuperblock FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < SuperblockSize)
        {

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
            VolumeName = ReadFixedUtf8(data.Slice(OffsetVolumeName, 16)),
            VolumeNameBytes = data.Slice(OffsetVolumeName, 16).ToArray(),
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

            RootNid8b = data.Length >= OffsetRootNid8b + 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetRootNid8b..])
                : 0,
            MetaBoxNid = data.Length >= OffsetMetaBoxNid + 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(data[OffsetMetaBoxNid..])
                : 0,
        };
    }

    /// <summary>
    /// Parses the superblock from a full image (locating offset 1024 automatically).
    /// <para>从完整镜像解析超级块（自动定位到偏移 1024）。</para>
    /// </summary>
    /// <param name="image">Whole image bytes; the region from <see cref="SuperblockOffset"/> must hold at least
    /// 112 bytes, and a wider slot of <see cref="SuperblockSize"/> plus <see cref="SbExtSlots"/> multiplied by
    /// <see cref="ExtSlotSize"/> is re-read whenever the image reaches it.
    /// <para>完整镜像字节；自 <see cref="SuperblockOffset"/> 起的区域至少要有 112 字节；镜像覆盖更宽槽位时，
    /// 按 <see cref="SuperblockSize"/> 加上 <see cref="SbExtSlots"/> 与 <see cref="ExtSlotSize"/> 的乘积重新读取。</para></param>
    /// <returns>The superblock parsed from the widest slot the image actually contains.
    /// <para>按镜像实际包含的最宽槽位解析得到的超级块。</para></returns>
    public static ErofsSuperblock FromImage(ReadOnlySpan<byte> image)
    {
        if (image.Length < SuperblockOffset + SuperblockSize - 16)
        {
            throw new FormatException($"Image too small to hold an EROFS superblock: {image.Length} bytes.");
        }

        var core = image.Slice(SuperblockOffset, Math.Min(SuperblockSize, image.Length - SuperblockOffset));
        var preliminary = FromBytes(core);
        var slotSize = SuperblockSize + preliminary.SbExtSlots * ExtSlotSize;
        if (slotSize <= SuperblockSize)
        {
            return preliminary;
        }

        var available = checked((int)Math.Min(slotSize, image.Length - SuperblockOffset));
        return FromBytes(image.Slice(SuperblockOffset, available));
    }

    /// <summary>
    /// Serializes the superblock into a buffer of at least 128 bytes (the classic layout fills the whole 128-byte slot).
    /// <para>将超级块序列化到缓冲区（长度至少 128 字节；经典布局写满 128 字节槽位）。</para>
    /// </summary>
    /// <param name="destination">Destination span of at least <see cref="SuperblockSize"/> bytes; the span is
    /// cleared before each field is written at its fixed little-endian offset.
    /// <para>至少 <see cref="SuperblockSize"/> 字节的目标区间；先整体清零，再按固定小端偏移逐个写入字段。</para></param>
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
        if (VolumeNameBytes.Length == 16) VolumeNameBytes.CopyTo(destination.Slice(OffsetVolumeName, 16));
        else WriteFixedUtf8(destination.Slice(OffsetVolumeName, 16), VolumeName);
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

        if (destination.Length >= OffsetMetaBoxNid + 8 && HasMetabox)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetMetaBoxNid..], MetaBoxNid);
            if (destination.Length >= OffsetReserved3 + 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(destination[OffsetReserved3..], 0);
            }
        }
    }

    private static string ReadFixedUtf8(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0)
        {
            end = bytes.Length;
        }

        return Encoding.UTF8.GetString(bytes[..end]);
    }

    private static void WriteFixedUtf8(Span<byte> destination, string value)
    {
        destination.Clear();
        if (Encoding.UTF8.GetByteCount(value) > destination.Length)
            throw new ArgumentException("The EROFS volume label exceeds its 16-byte field.", nameof(value));
        Encoding.UTF8.GetBytes(value, destination);
    }
}