using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Storage families supported by the XFlash storage query.
/// <para>XFlash 存储查询所支持的存储类型。</para>
/// </summary>
public enum MtkXFlashStorageKind
{
    /// <summary>Embedded MultiMediaCard storage. <para>嵌入式多媒体卡（eMMC）存储。</para></summary>
    Emmc,
    /// <summary>
    /// Universal Flash Storage, using XFlash partition identifiers rather than XML LUN names.
    /// <para>通用闪存存储（UFS），使用 XFlash 分区标识符而不是 XML 的 LUN 名称。</para>
    /// </summary>
    Ufs,
    /// <summary>NOR flash addressed through the agent's whole-device region.
    /// <para>通过代理整片区域寻址的 NOR 闪存。</para></summary>
    Nor,
    /// <summary>NAND flash requiring explicit page, spare-area and addressing policies.
    /// <para>需要显式页、备用区和寻址策略的 NAND 闪存。</para></summary>
    Nand
}

/// <summary>
/// One device-reported XFlash physical partition, not a GPT partition.
/// <para>设备上报的一个 XFlash 物理分区，不是 GPT 分区。</para>
/// </summary>
public sealed class MtkXFlashStorageRegion
{
    internal MtkXFlashStorageRegion(uint partitionId, long capacityBytes, bool authenticated)
    {
        PartitionId = partitionId;
        CapacityBytes = capacityBytes;
        RequiresAuthenticatedAccess = authenticated;
    }

    /// <summary>
    /// Gets the partition identifier used by XFlash storage commands.
    /// <para>获取 XFlash 存储命令使用的分区标识符。</para>
    /// </summary>
    /// <value>
    /// eMMC/UFS regions start at <c>1</c>; NOR uses the whole-device identifier <c>8</c>.
    /// <para>eMMC/UFS 区域从 <c>1</c> 开始编号；NOR 使用整片标识符 <c>8</c>。</para>
    /// </value>
    public uint PartitionId { get; }
    /// <summary>
    /// Gets the reported capacity in bytes.
    /// <para>获取上报的容量（以字节为单位）。</para>
    /// </summary>
    /// <value>
    /// A multiple of the storage block size that may be <c>0</c> when the device reports no capacity for the region.
    /// <para>存储块大小的整数倍；设备未上报该分区容量时可能为 <c>0</c>。</para>
    /// </value>
    public long CapacityBytes { get; }
    /// <summary>
    /// Gets whether the region is RPMB and requires a separate authenticated protocol.
    /// <para>获取该分区是否为需要经过独立认证协议的 RPMB 分区。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> for the replay-protected partition, which
    /// <see cref="MtkXFlashStorageInfo.ValidateRange"/> refuses as an ordinary block-storage target.
    /// <para>为重放保护分区返回 <see langword="true"/>；<see cref="MtkXFlashStorageInfo.ValidateRange"/> 会拒绝把它当作普通块存储目标。</para>
    /// </value>
    public bool RequiresAuthenticatedAccess { get; }
}

/// <summary>
/// Validated device-reported geometry and bounded packet sizes for one storage family.
/// <para>某一种存储类型经过校验的设备上报几何信息与有界的包长度。</para>
/// </summary>
public sealed class MtkXFlashStorageInfo
{
    private MtkXFlashStorageInfo(MtkXFlashStorageKind kind, uint deviceType, int blockSize,
        MtkXFlashStorageRegion[] regions, string cidHex, int writePacketLength, int readPacketLength,
        MtkXFlashNandGeometry? nand = null)
    {
        Kind = kind;
        DeviceType = deviceType;
        BlockSize = blockSize;
        Regions = Array.AsReadOnly(regions);
        CidHex = cidHex;
        WritePacketLength = writePacketLength;
        ReadPacketLength = readPacketLength;
        Nand = nand;
    }

    /// <summary>
    /// Gets the queried storage family.
    /// <para>获取被查询的存储类型。</para>
    /// </summary>
    /// <value>
    /// The family whose geometry reply produced this snapshot; it fixes the region count and the field offsets.
    /// <para>生成该快照所依据的几何信息应答对应的类型；它决定了分区数量与字段偏移。</para>
    /// </value>
    public MtkXFlashStorageKind Kind { get; }
    /// <summary>Gets NAND-specific geometry when the explicitly queried family is NAND.
    /// <para>获取显式查询家族为 NAND 时的专用几何信息。</para></summary>
    /// <value>The page, spare-area and erase geometry, or null for another storage family.
    /// <para>页、备用区和擦除几何信息；其他存储家族为 null。</para></value>
    public MtkXFlashNandGeometry? Nand { get; }
    /// <summary>
    /// Gets the unmodified nonzero device type reported by the agent.
    /// <para>获取代理上报的未经修改且非零的设备类型值。</para>
    /// </summary>
    /// <value>
    /// The first four bytes of the geometry reply read as little-endian; a zero value denotes absent storage and is
    /// rejected before an instance is built.
    /// <para>以小端方式读取的几何信息应答前四个字节；值为零表示存储不存在，会在构建实例前被拒绝。</para>
    /// </value>
    public uint DeviceType { get; }
    /// <summary>
    /// Gets the storage block size in bytes.
    /// <para>获取存储块大小（以字节为单位）。</para>
    /// </summary>
    /// <value>
    /// A power of two up to 65536, at least 512 for eMMC/UFS and 1 for NOR.
    /// For NOR/NAND this is the reported page size, not an erase-block size.
    /// <para>不超过 65536 的 2 的幂；eMMC/UFS 至少为 512，NOR 至少为 1。
    /// NOR/NAND 的此值为上报页大小，不是擦除块大小。</para>
    /// </value>
    public int BlockSize { get; }
    /// <summary>
    /// Gets physical regions, including zero-capacity regions and any reported RPMB region.
    /// <para>获取物理分区，包含容量为零的分区以及设备上报的 RPMB 分区。</para>
    /// </summary>
    /// <value>
    /// Eight regions for eMMC, three for UFS, one whole-device region for NOR, or NAND's logical available area.
    /// NAND physical capacity is available through <see cref="Nand"/> and requires dedicated access methods.
    /// <para>eMMC 为八个区域，UFS 为三个，NOR 为一个整片区域，NAND 为逻辑可用区域。
    /// NAND 物理容量通过 <see cref="Nand"/> 提供，必须使用专用访问方法。</para>
    /// </value>
    public IReadOnlyList<MtkXFlashStorageRegion> Regions { get; }
    /// <summary>
    /// Gets the reported 16-byte card identity encoded as hexadecimal.
    /// <para>获取以十六进制编码的上报 16 字节卡标识信息。</para>
    /// </summary>
    /// <value>
    /// Thirty-two hexadecimal characters for eMMC/UFS, or an empty string for NOR/NAND, whose queries have no CID.
    /// <para>eMMC/UFS 为三十二个十六进制字符；NOR/NAND 查询不含 CID，返回空字符串。</para>
    /// </value>
    public string CidHex { get; }
    /// <summary>
    /// Gets the agent's bounded write packet length in bytes.
    /// <para>获取代理有界写入包长度（以字节为单位）。</para>
    /// </summary>
    /// <value>
    /// At most 1 MiB, block-aligned for eMMC/UFS/NOR; NAND reports a byte limit and its selected records must fit.
    /// <para>不超过 1 MiB；eMMC/UFS/NOR 按块对齐，NAND 上报字节上限且选定记录必须能装入包中。</para>
    /// </value>
    public int WritePacketLength { get; }
    /// <summary>
    /// Gets the agent's bounded read packet length in bytes.
    /// <para>获取代理有界读取包长度（以字节为单位）。</para>
    /// </summary>
    /// <value>
    /// At most 1 MiB, block-aligned for eMMC/UFS/NOR; NAND reports a byte limit greater than four status bytes.
    /// <para>不超过 1 MiB；eMMC/UFS/NOR 按块对齐，NAND 上报大于四字节状态长度的字节上限。</para>
    /// </value>
    public int ReadPacketLength { get; }

    /// <summary>
    /// Checks a byte range for ordinary block access; RPMB is deliberately excluded.
    /// <para>检查用于普通块访问的字节范围；RPMB 分区被明确排除。</para>
    /// </summary>
    /// <param name="partitionId">
    /// The XFlash physical partition identifier.
    /// <para>XFlash 物理分区标识符。</para>
    /// </param>
    /// <param name="offset">
    /// The nonnegative block-aligned byte offset.
    /// <para>非负且按块对齐的字节偏移量。</para>
    /// </param>
    /// <param name="length">
    /// The positive block-aligned byte count.
    /// <para>为正数且按块对齐的字节数量。</para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative, <paramref name="length"/> is zero or negative, no region reports
    /// <paramref name="partitionId"/>, or the range is unaligned or extends past the reported capacity.
    /// <para><paramref name="offset"/> 为负数、<paramref name="length"/> 为零或负数、没有分区上报 <paramref name="partitionId"/>，
    /// 或该范围未按块对齐、超出上报容量。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The matched region requires authenticated RPMB access, or NAND requires an explicit page-stream policy.
    /// <para>匹配到的分区需要经过认证的 RPMB 访问，或 NAND 需要显式页流策略。</para>
    /// </exception>
    public void ValidateRange(uint partitionId, long offset, long length)
    {
        if (Kind == MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException("NAND requires explicit page, spare-area and addressing policies.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var region = Regions.FirstOrDefault(region => region.PartitionId == partitionId)
            ?? throw new ArgumentOutOfRangeException(nameof(partitionId), "The physical partition was not reported.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException("RPMB is not an ordinary block-storage target.");
        }
        if (offset % BlockSize != 0 || length % BlockSize != 0
            || offset > region.CapacityBytes || length > region.CapacityBytes - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The requested range is unaligned or exceeds reported capacity.");
        }
    }

    internal static bool IsAbsent(ReadOnlySpan<byte> geometry)
    {
        if (geometry.Length < 4 || geometry.Length > 4096)
        {
            throw new MtkProtocolException("The storage query omitted its device type.");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(geometry) == 0;
    }

    internal static MtkXFlashStorageInfo Parse(MtkXFlashStorageKind kind, ReadOnlySpan<byte> geometry,
        ReadOnlySpan<byte> packetLengths)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var minimumLength = kind switch
        {
            MtkXFlashStorageKind.Emmc => 96,
            MtkXFlashStorageKind.Ufs => 48,
            MtkXFlashStorageKind.Nor => 16,
            MtkXFlashStorageKind.Nand => 45,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (geometry.Length < minimumLength || geometry.Length > 4096 || IsAbsent(geometry))
        {
            throw new MtkProtocolException("The selected storage geometry is missing, truncated or oversized.");
        }
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(geometry[4..]);
        var minimumBlockSize = kind == MtkXFlashStorageKind.Nor ? 1u : 512u;
        if (blockSize < minimumBlockSize || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new MtkProtocolException("The storage block size is unsupported or invalid.");
        }
        if (packetLengths.Length != 8)
        {
            throw new MtkProtocolException("The packet-length reply must contain two 32-bit fields.");
        }
        var writeLength = BinaryPrimitives.ReadUInt32LittleEndian(packetLengths);
        var readLength = BinaryPrimitives.ReadUInt32LittleEndian(packetLengths[4..]);
        var packetAlignment = kind == MtkXFlashStorageKind.Nand ? 1u : Math.Max(blockSize, 8u);
        if (writeLength < packetAlignment || readLength < packetAlignment
            || readLength < 5
            || writeLength > 1024 * 1024 || readLength > 1024 * 1024
            || writeLength % packetAlignment != 0 || readLength % packetAlignment != 0)
        {
            throw new MtkProtocolException("The storage packet sizes are unsupported, unaligned or unbounded.");
        }
        if (kind == MtkXFlashStorageKind.Nor)
        {
            var capacity = BinaryPrimitives.ReadUInt64LittleEndian(geometry[8..]);
            if (capacity < packetAlignment || capacity > long.MaxValue || capacity % packetAlignment != 0)
            {
                throw new MtkProtocolException("The NOR capacity is empty, overflowing or not transfer-aligned.");
            }
            return new MtkXFlashStorageInfo(kind, BinaryPrimitives.ReadUInt32LittleEndian(geometry), (int)blockSize,
                [new MtkXFlashStorageRegion(8, (long)capacity, false)], string.Empty, (int)writeLength, (int)readLength);
        }
        if (kind == MtkXFlashStorageKind.Nand)
        {
            var nand = MtkXFlashNandGeometry.Parse(geometry);
            return new MtkXFlashStorageInfo(kind, BinaryPrimitives.ReadUInt32LittleEndian(geometry), (int)blockSize,
                [new MtkXFlashStorageRegion(8, nand.AvailableSize, false)], string.Empty,
                (int)writeLength, (int)readLength, nand);
        }
        var count = kind == MtkXFlashStorageKind.Emmc ? 8 : 3;
        var regions = new MtkXFlashStorageRegion[count];
        for (var index = 0; index < count; index++)
        {
            var wireIndex = kind == MtkXFlashStorageKind.Emmc ? index : index switch { 0 => 1, 1 => 0, _ => 2 };
            var size = BinaryPrimitives.ReadUInt64LittleEndian(geometry[(8 + wireIndex * 8)..]);
            if (size > long.MaxValue || size % blockSize != 0)
            {
                throw new MtkProtocolException("The reported physical capacity overflows or is not block-aligned.");
            }
            regions[index] = new MtkXFlashStorageRegion((uint)index + 1, (long)size,
                kind == MtkXFlashStorageKind.Emmc && index == 2);
        }
        if (!regions.Any(region => region.CapacityBytes > 0 && !region.RequiresAuthenticatedAccess))
        {
            throw new MtkProtocolException("The selected storage has no ordinary addressable region.");
        }
        var cidOffset = kind == MtkXFlashStorageKind.Emmc ? 72 : 32;
        return new MtkXFlashStorageInfo(kind, BinaryPrimitives.ReadUInt32LittleEndian(geometry), (int)blockSize,
            regions, Convert.ToHexString(geometry.Slice(cidOffset, 16)), (int)writeLength, (int)readLength);
    }
}