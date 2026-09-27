using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects an explicit Legacy NAND readback wire profile.
/// <para>选择显式的 Legacy NAND 读回线协议配置。</para></summary>
public enum MtkLegacyNandReadProfile
{
    /// <summary>SDK 64-bit address and total-length fields, no host byte, and zero container length.
    /// <para>SDK 64 位地址和总长度字段，无主机字节，容器长度为零。</para></summary>
    Sdk64Raw
}

/// <summary>Selects the Legacy NAND address interpretation.
/// <para>选择 Legacy NAND 地址解释方式。</para></summary>
public enum MtkLegacyNandAddressType : byte
{
    /// <summary>Agent-managed logical addresses with a caller-supplied capacity bound.
    /// <para>代理管理的逻辑地址，使用调用方提供的容量边界。</para></summary>
    Logical,
    /// <summary>Physical main-area addresses within the reported capacity.
    /// <para>上报容量内的物理主区地址。</para></summary>
    Physical
}

/// <summary>Selects the Legacy NAND readback page layout.
/// <para>选择 Legacy NAND 读回页布局。</para></summary>
public enum MtkLegacyNandReadMode : byte
{
    /// <summary>Main and spare bytes interleaved per page. <para>逐页交错的主页与备用字节。</para></summary>
    PageAndSpare = 0,
    /// <summary>Main-page bytes only. <para>仅主页字节。</para></summary>
    PageOnly = 1,
    /// <summary>Spare bytes only. <para>仅备用字节。</para></summary>
    SpareOnly = 2,
    /// <summary>Main-page bytes with device-side ECC. <para>带设备端 ECC 的主页字节。</para></summary>
    PageWithEcc = 3,
    /// <summary>Main and spare bytes with device-side ECC decoding. <para>带设备端 ECC 解码的主页与备用字节。</para></summary>
    PageAndSpareWithEcc = 4,
    /// <summary>Main and spare bytes without device randomization. <para>不使用设备随机化的主页与备用字节。</para></summary>
    PageAndSpareNoRandom = 6
}

/// <summary>Selects the length field convention required by the selected Legacy DA.
/// <para>选择所选 Legacy DA 要求的长度字段约定。</para></summary>
public enum MtkLegacyNandLengthUnit
{
    /// <summary>Main-area bytes excluding spare bytes. <para>不含备用字节的主区字节数。</para></summary>
    MainAreaBytes,
    /// <summary>Exact output bytes according to the selected page mode. <para>按选定页模式计算的精确输出字节数。</para></summary>
    StreamBytes
}

/// <summary>Defines an explicit raw-page SDK readback profile without container processing or protocol fallback.
/// <para>定义显式的 SDK 原始页读回配置，不处理容器，也不回退协议。</para></summary>
public sealed record MtkLegacyNandReadOptions
{
    /// <summary>Gets the caller-selected wire profile.
    /// <para>获取调用方选择的线协议配置。</para></summary>
    /// <value>The explicit SDK 64-bit raw-page profile, not the host-byte 32-bit protocol.
    /// <para>显式 SDK 64 位原始页配置，而非含主机字节的 32 位协议。</para></value>
    public required MtkLegacyNandReadProfile Profile { get; init; }

    /// <summary>Gets the selected address interpretation.
    /// <para>获取选定的地址解释方式。</para></summary>
    /// <value>Logical or physical; never inferred from BMT presence.
    /// <para>逻辑或物理，不根据 BMT 是否存在推测。</para></value>
    public required MtkLegacyNandAddressType AddressType { get; init; }

    /// <summary>Gets the requested page layout.
    /// <para>获取请求的页布局。</para></summary>
    /// <value>The explicit main, spare or combined read mode.
    /// <para>显式的主页、备用区或组合读取模式。</para></value>
    public required MtkLegacyNandReadMode Mode { get; init; }

    /// <summary>Gets the DA command-length convention.
    /// <para>获取 DA 命令长度约定。</para></summary>
    /// <value>The caller's main-area or stream-byte convention; no automatic retry selects another convention.
    /// <para>调用方的主区或流字节约定；不会通过自动重试选择另一种约定。</para></value>
    public required MtkLegacyNandLengthUnit LengthUnit { get; init; }

    /// <summary>Gets the independently selected upper bound for logical main-area addressing.
    /// <para>获取独立选定的逻辑主区寻址上界。</para></summary>
    /// <value>A positive page multiple required for logical access; null for physical access.
    /// The bound does not prove that the device can read every logical page.
    /// <para>逻辑访问必须提供正数页大小倍数；物理访问为 null。此上界不证明设备可以读取所有逻辑页。</para></value>
    public long? LogicalCapacityBytes { get; init; }

    /// <summary>Gets the number of complete page records per packet.
    /// <para>获取每个数据包包含的完整页记录数。</para></summary>
    /// <value>From 1 through 4096, additionally limited to 1 MiB of page records per packet.
    /// <para>1 至 4096，且每包页记录总字节数不得超过 1 MiB。</para></value>
    public int PagesPerPacket { get; init; } = 1;

    /// <summary>Computes the exact output length without interpreting or removing spare bytes.
    /// <para>计算精确输出长度，不解释或移除备用字节。</para></summary>
    /// <param name="geometry">Validated Legacy NAND geometry.
    /// <para>已校验的 Legacy NAND 几何信息。</para></param>
    /// <param name="mainLength">The positive page-aligned main-area span.
    /// <para>正数且按页对齐的主区跨度。</para></param>
    /// <returns>The bounded Int64 stream length.
    /// <para>Int64 范围内的流长度。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The profile, span or computed length is invalid.
    /// <para>配置、跨度或计算长度无效。</para></exception>
    public long GetTransferLength(MtkLegacyNandGeometry geometry, long mainLength)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var recordSize = GetRecordSize(geometry);
        if (mainLength <= 0 || mainLength % geometry.PageSize != 0
            || mainLength / geometry.PageSize > long.MaxValue / recordSize)
        {
            throw new ArgumentOutOfRangeException(nameof(mainLength));
        }
        return mainLength / geometry.PageSize * recordSize;
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Profile) || !Enum.IsDefined(AddressType) || !Enum.IsDefined(Mode)
            || !Enum.IsDefined(LengthUnit) || PagesPerPacket is < 1 or > 4096
            || (AddressType == MtkLegacyNandAddressType.Logical ? LogicalCapacityBytes is null or <= 0 : LogicalCapacityBytes is not null))
        {
            throw new ArgumentOutOfRangeException(nameof(MtkLegacyNandReadOptions));
        }
    }

    internal int GetRecordSize(MtkLegacyNandGeometry geometry)
    {
        Validate();
        var size = Mode switch
        {
            MtkLegacyNandReadMode.PageOnly or MtkLegacyNandReadMode.PageWithEcc => geometry.PageSize,
            MtkLegacyNandReadMode.SpareOnly => geometry.SpareSize,
            _ => geometry.PageSize + geometry.SpareSize
        };
        if ((geometry.SpareSize == 0 && Mode is not (MtkLegacyNandReadMode.PageOnly or MtkLegacyNandReadMode.PageWithEcc))
            || (long)size * PagesPerPacket > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(Mode), "The Legacy NAND record is absent or its packet exceeds 1 MiB.");
        }
        return size;
    }

    internal byte[] CreateHeader(MtkLegacyNandGeometry geometry, long offset, long mainLength, long streamLength)
    {
        Validate();
        geometry.ValidateRange(offset, mainLength, LogicalCapacityBytes ?? geometry.CapacityBytes);
        var bytes = new byte[23];
        bytes[0] = 0xDF;
        bytes[1] = (byte)Mode;
        bytes[2] = (byte)AddressType;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(3), (ulong)offset);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(11),
            (ulong)(LengthUnit == MtkLegacyNandLengthUnit.MainAreaBytes ? mainLength : streamLength));
        return bytes;
    }
}