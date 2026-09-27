using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Describes one Legacy eMMC physical region, not a GPT partition.
/// <para>描述一个 Legacy eMMC 物理区域，而非 GPT 分区。</para></summary>
/// <param name="PartitionId">The Legacy partition byte from 1 through 8.
/// <para>1 至 8 的 Legacy 分区字节。</para></param>
/// <param name="CapacityBytes">The reported capacity in bytes.
/// <para>上报的字节容量。</para></param>
/// <param name="RequiresAuthenticatedAccess">Whether this is the RPMB region.
/// <para>是否为 RPMB 区域。</para></param>
public sealed record MtkLegacyStorageRegion(byte PartitionId, long CapacityBytes, bool RequiresAuthenticatedAccess);

/// <summary>Contains validated geometry for the explicitly selected Legacy storage.
/// <para>包含显式选定的 Legacy 存储的已校验几何信息。</para></summary>
public sealed class MtkLegacyStorageInfo
{
    private MtkLegacyStorageInfo(MtkLegacyStorageRegion[] regions, string cidHex, string firmwareRevisionHex,
        MtkLegacyNorGeometry? nor = null, MtkLegacyNandGeometry? nand = null)
    {
        Regions = Array.AsReadOnly(regions);
        CidHex = cidHex;
        FirmwareRevisionHex = firmwareRevisionHex;
        Nor = nor;
        Nand = nand;
        Kind = nand is not null ? MtkXFlashStorageKind.Nand
            : nor is not null ? MtkXFlashStorageKind.Nor : MtkXFlashStorageKind.Emmc;
    }

    /// <summary>Gets the explicitly initialized storage family.
    /// <para>获取显式初始化的存储类型。</para></summary>
    /// <value>eMMC, NOR or NAND; this value is not inferred from another family's status.
    /// <para>eMMC、NOR 或 NAND，不根据其他类型的状态推断。</para></value>
    public MtkXFlashStorageKind Kind { get; }

    /// <summary>Gets the NOR geometry when NOR was explicitly selected.
    /// <para>获取显式选择 NOR 时的 NOR 几何信息。</para></summary>
    /// <value>The validated NOR inventory, or null for other storage.
    /// <para>已校验的 NOR 清单；其他存储时为 null。</para></value>
    public MtkLegacyNorGeometry? Nor { get; }

    /// <summary>Gets the NAND geometry when NAND was explicitly selected.
    /// <para>获取显式选择 NAND 时的 NAND 几何信息。</para></summary>
    /// <value>The validated NAND inventory, or null for other storage.
    /// <para>已校验的 NAND 清单；其他存储时为 null。</para></value>
    public MtkLegacyNandGeometry? Nand { get; }

    /// <summary>Gets all eight eMMC regions, or an empty collection for NOR and NAND.
    /// <para>获取全部八个 eMMC 区域；NOR 和 NAND 时为空集合。</para></summary>
    /// <value>The immutable region inventory.
    /// <para>不可变的区域清单。</para></value>
    public IReadOnlyList<MtkLegacyStorageRegion> Regions { get; }

    /// <summary>Gets the unmodified 16-byte CID in wire byte order.
    /// <para>获取按线协议字节顺序保留的原始 16 字节 CID。</para></summary>
    /// <value>Thirty-two hexadecimal characters for eMMC; empty for NOR and NAND.
    /// <para>eMMC 时为三十二个十六进制字符；NOR 和 NAND 时为空。</para></value>
    public string CidHex { get; }

    /// <summary>Gets the unmodified firmware revision bytes reported by the card.
    /// <para>获取存储卡上报的原始固件版本字节。</para></summary>
    /// <value>Sixteen hexadecimal characters for eMMC; empty for NOR and NAND.
    /// <para>eMMC 时为十六个十六进制字符；NOR 和 NAND 时为空。</para></value>
    public string FirmwareRevisionHex { get; }

    /// <summary>Validates an aligned ordinary eMMC range, excluding authenticated RPMB access.
    /// <para>校验对齐的普通 eMMC 范围，不包括认证 RPMB 访问。</para></summary>
    /// <param name="partitionId">The physical partition byte.
    /// <para>物理分区字节。</para></param>
    /// <param name="offset">The nonnegative byte offset aligned to 512.
    /// <para>按 512 对齐的非负字节偏移。</para></param>
    /// <param name="length">The positive byte length aligned to 512.
    /// <para>按 512 对齐的正数字节长度。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The region or range is invalid or exceeds capacity.
    /// <para>区域或范围无效，或超过容量。</para></exception>
    /// <exception cref="NotSupportedException">The selected storage is NOR or NAND, or the region is RPMB.
    /// <para>所选存储为 NOR 或 NAND，或所选区域为 RPMB。</para></exception>
    public void ValidateRange(byte partitionId, long offset, long length)
    {
        if (Kind != MtkXFlashStorageKind.Emmc)
        {
            throw new NotSupportedException("Use the dedicated Legacy NOR or NAND access methods.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (partitionId is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionId));
        }
        var region = Regions[partitionId - 1];
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException("RPMB requires a separate authenticated protocol.");
        }
        if (offset % 512 != 0 || length % 512 != 0 || offset > region.CapacityBytes
            || length > region.CapacityBytes - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The range is unaligned or outside the discovered region.");
        }
    }

    internal static MtkLegacyStorageInfo Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 92)
        {
            throw new MtkProtocolException("The Legacy eMMC inventory has an invalid length.");
        }
        var status = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (status != 0)
        {
            throw new MtkProtocolException("Legacy eMMC initialization failed.", status);
        }
        var regions = new MtkLegacyStorageRegion[8];
        for (var index = 0; index < regions.Length; index++)
        {
            var capacity = BinaryPrimitives.ReadUInt64BigEndian(bytes[(4 + index * 8)..]);
            if (capacity > long.MaxValue || capacity % 512 != 0)
            {
                throw new MtkProtocolException("The Legacy eMMC capacity is oversized or unaligned.");
            }
            regions[index] = new((byte)(index + 1), (long)capacity, index == 2);
        }
        if (regions[7].CapacityBytes == 0)
        {
            throw new MtkProtocolException("The selected Legacy eMMC user region is absent.");
        }
        return new(regions, Convert.ToHexString(bytes.Slice(68, 16)), Convert.ToHexString(bytes.Slice(84, 8)));
    }

    internal static MtkLegacyStorageInfo ParseNor(ReadOnlySpan<byte> bytes) =>
        new([], string.Empty, string.Empty, MtkLegacyNorGeometry.Parse(bytes));

    internal static MtkLegacyStorageInfo ParseNand(ReadOnlySpan<byte> inventory, ReadOnlySpan<byte> ids,
        ReadOnlySpan<byte> details, bool size64Bit) =>
        new([], string.Empty, string.Empty, nand: MtkLegacyNandGeometry.Parse(inventory, ids, details, size64Bit));
}