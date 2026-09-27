using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects the Legacy NAND programming wire profile.
/// <para>选择 Legacy NAND 编程线协议配置。</para></summary>
public enum MtkLegacyNandWriteProfile
{
    /// <summary>SDK B3 with 32-bit block indices, zero container length and complete page packets.
    /// <para>SDK B3 协议，使用 32 位块编号、零容器长度和完整页数据包。</para></summary>
    Sdk32BlocksRaw
}

/// <summary>Selects the NAND program operation without host-side layout conversion.
/// <para>选择 NAND 编程操作，不在主机端转换布局。</para></summary>
public enum MtkLegacyNandWriteMode : byte
{
    /// <summary>Programs interleaved main and spare bytes.
    /// <para>编程交错的主区与备用字节。</para></summary>
    PageAndSpare = 0,
    /// <summary>Programs main-page bytes only.
    /// <para>仅编程主页字节。</para></summary>
    PageOnly = 1,
    /// <summary>Programs spare bytes only.
    /// <para>仅编程备用字节。</para></summary>
    SpareOnly = 2,
    /// <summary>Programs main and spare bytes with agent-side ECC encoding.
    /// <para>通过代理端 ECC 编码编程主页与备用字节。</para></summary>
    PageAndSpareWithEcc = 4
}

/// <summary>Defines an explicit whole-block Legacy NAND programming request.
/// <para>定义显式的整块 Legacy NAND 编程请求。</para></summary>
/// <remarks>FDM layout, container processing, erase, padding and automatic retransmission are not included.
/// The SDK profile uses one page record per packet and accepts at most UInt32.MaxValue source bytes.
/// <para>不包含 FDM 布局、容器处理、擦除、填充或自动重传。
/// 此 SDK 配置每包使用一个页记录，最多接受 UInt32.MaxValue 个源字节。</para></remarks>
public sealed record MtkLegacyNandWriteOptions
{
    /// <summary>Gets the explicitly selected wire profile.
    /// <para>获取显式选择的线协议配置。</para></summary>
    /// <value>The SDK B3 block-addressed profile.
    /// <para>按块寻址的 SDK B3 配置。</para></value>
    public required MtkLegacyNandWriteProfile Profile { get; init; }

    /// <summary>Gets the selected address interpretation.
    /// <para>获取选定的地址解释方式。</para></summary>
    /// <value>Logical or physical, never inferred from BMT presence.
    /// <para>逻辑或物理，不根据 BMT 是否存在推测。</para></value>
    public required MtkLegacyNandAddressType AddressType { get; init; }

    /// <summary>Gets the explicit page programming mode.
    /// <para>获取显式的页编程模式。</para></summary>
    /// <value>The main, spare or combined layout; FDM is not supported.
    /// <para>主区、备用区或组合布局；不支持 FDM。</para></value>
    public required MtkLegacyNandWriteMode Mode { get; init; }

    /// <summary>Gets the independently selected logical main-area capacity bound.
    /// <para>获取独立选择的逻辑主区容量边界。</para></summary>
    /// <value>A positive erase-block multiple for logical access, or null for physical access.
    /// This bound is not proof that every requested logical block is writable.
    /// <para>逻辑访问使用正数擦除块大小倍数，物理访问为 null。此边界不证明所有请求的逻辑块均可写。</para></value>
    public long? LogicalCapacityBytes { get; init; }

    /// <summary>Computes the exact source length for a complete-block main-area span.
    /// <para>计算完整块主区跨度对应的精确源长度。</para></summary>
    /// <param name="geometry">The validated NAND geometry.
    /// <para>已校验的 NAND 几何信息。</para></param>
    /// <param name="mainLength">The positive main-area span aligned to the erase-block size.
    /// <para>正数且按擦除块大小对齐的主区跨度。</para></param>
    /// <returns>The exact source byte count, excluding any container or implicit padding.
    /// <para>精确源字节数，不包含容器或隐式填充。</para></returns>
    public long GetTransferLength(MtkLegacyNandGeometry geometry, long mainLength)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var recordSize = GetRecordSize(geometry);
        if (mainLength <= 0 || mainLength % geometry.EraseBlockSize != 0
            || mainLength / geometry.PageSize > uint.MaxValue / recordSize)
        {
            throw new ArgumentOutOfRangeException(nameof(mainLength), "The SDK NAND source must contain complete blocks and fit its 32-bit input length.");
        }
        return mainLength / geometry.PageSize * recordSize;
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Profile) || !Enum.IsDefined(AddressType) || !Enum.IsDefined(Mode)
            || (AddressType == MtkLegacyNandAddressType.Logical ? LogicalCapacityBytes is null or <= 0 : LogicalCapacityBytes is not null))
        {
            throw new ArgumentOutOfRangeException(nameof(MtkLegacyNandWriteOptions));
        }
    }

    internal int GetRecordSize(MtkLegacyNandGeometry geometry)
    {
        Validate();
        if (geometry.SpareSize == 0 && Mode != MtkLegacyNandWriteMode.PageOnly)
        {
            throw new ArgumentOutOfRangeException(nameof(Mode), "The selected NAND program mode requires a spare area.");
        }
        return Mode switch
        {
            MtkLegacyNandWriteMode.PageOnly => geometry.PageSize,
            MtkLegacyNandWriteMode.SpareOnly => geometry.SpareSize,
            _ => geometry.PageSize + geometry.SpareSize
        };
    }

    internal byte[] CreateHeader(MtkLegacyNandGeometry geometry, long offset, long mainLength)
    {
        _ = GetTransferLength(geometry, mainLength);
        var capacity = LogicalCapacityBytes ?? geometry.CapacityBytes;
        geometry.ValidateRange(offset, mainLength, capacity);
        var blockSize = geometry.EraseBlockSize;
        var first = offset / blockSize;
        var count = mainLength / blockSize;
        if (offset % blockSize != 0 || capacity % blockSize != 0
            || first > uint.MaxValue || count > uint.MaxValue || count - 1 > uint.MaxValue - first)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The SDK NAND range must fit its 32-bit block indices.");
        }
        var bytes = new byte[19];
        bytes[0] = 0xB3;
        bytes[1] = (byte)AddressType;
        bytes[2] = (byte)Mode;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(3), (uint)first);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(7), (uint)count);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(11), (uint)GetRecordSize(geometry));
        return bytes;
    }
}