using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects NAND address interpretation by the agent. <para>选择代理对 NAND 地址的解释方式。</para></summary>
public enum MtkXFlashNandAddressType : uint
{
    /// <summary>Logical addresses within reported available capacity. <para>上报可用容量内的逻辑地址。</para></summary>
    Logical,
    /// <summary>Physical addresses within reported total capacity. <para>上报总容量内的物理地址。</para></summary>
    Physical,
    /// <summary>The agent's physical-PMT address mode. <para>代理的 physical-PMT 地址模式。</para></summary>
    PhysicalPmt
}

/// <summary>Selects the NAND cell-use field, independently of storage type. <para>选择独立于存储类型的 NAND 单元使用字段。</para></summary>
public enum MtkXFlashNandCellUsage : uint
{
    /// <summary>Single-level cell use. <para>单层单元使用方式。</para></summary>
    SingleLevel,
    /// <summary>Multi-level cell use. <para>多层单元使用方式。</para></summary>
    MultiLevel
}

/// <summary>Selects an explicit XFlash NAND storage selector. <para>选择显式的 XFlash NAND 存储标识符。</para></summary>
public enum MtkXFlashNandStorageType : uint
{
    /// <summary>Generic NAND. <para>通用 NAND。</para></summary>
    Generic = 0x10,
    /// <summary>SLC NAND. <para>SLC NAND。</para></summary>
    Slc = 0x11,
    /// <summary>MLC NAND. <para>MLC NAND。</para></summary>
    Mlc = 0x12,
    /// <summary>TLC NAND. <para>TLC NAND。</para></summary>
    Tlc = 0x13,
    /// <summary>AMLC NAND. <para>AMLC NAND。</para></summary>
    Amlc = 0x14,
    /// <summary>SPI NAND. <para>SPI NAND。</para></summary>
    Spi = 0x15
}

/// <summary>Selects a NAND transfer mode with a defined page/spare stream layout.
/// <para>选择具有明确页与备用区流布局的 NAND 传输模式。</para></summary>
public enum MtkXFlashNandTransferMode : uint
{
    /// <summary>Interleaved main page and spare bytes. <para>交错排列的主页与备用字节。</para></summary>
    PageAndSpare = 0,
    /// <summary>Main-page bytes only. <para>仅主页字节。</para></summary>
    PageOnly = 1,
    /// <summary>Spare bytes only, one record per addressed page. <para>仅备用字节，每个寻址页一个记录。</para></summary>
    SpareOnly = 2,
    /// <summary>Main-page bytes with agent-side ECC handling. <para>由代理处理 ECC 的主页字节。</para></summary>
    PageWithEcc = 3,
    /// <summary>Main page and spare bytes with agent-side ECC handling. <para>由代理处理 ECC 的主页与备用字节。</para></summary>
    PageAndSpareWithEcc = 4,
    /// <summary>Main page and spare bytes with agent randomization disabled. <para>禁用代理随机化的主页与备用字节。</para></summary>
    PageAndSpareNoRandom = 6
}

/// <summary>Selects how the caller's DA profile encodes the command length field.
/// <para>选择调用方 DA 配置对命令长度字段的编码方式。</para></summary>
public enum MtkXFlashNandLengthUnit
{
    /// <summary>Addressed main-area bytes, excluding spare bytes. <para>寻址的主区字节数，不含备用字节。</para></summary>
    MainAreaBytes,
    /// <summary>Transferred stream bytes, including or excluding main/spare according to the selected mode.
    /// <para>传输的流字节数，按选定模式包含或排除主页与备用区。</para></summary>
    StreamBytes
}

/// <summary>Selects the explicit device-side NAND erase action. <para>选择显式的设备端 NAND 擦除动作。</para></summary>
public enum MtkXFlashNandEraseMode : uint
{
    /// <summary>Normal erase with the agent's bad-block policy. <para>按代理坏块策略进行普通擦除。</para></summary>
    Normal,
    /// <summary>Explicit force erase; no automatic retry chooses this action. <para>显式强制擦除；自动重试不会选择此动作。</para></summary>
    Force,
    /// <summary>Explicitly mark the selected erase blocks bad. <para>显式将选定擦除块标记为坏块。</para></summary>
    MarkBadBlock
}

/// <summary>Provides the caller-selected eight-word NAND extension profile used by XFlash commands.
/// No seven-word fallback, bad-block remapping, UBI conversion or FTL conversion is performed by the host.
/// <para>提供调用方选定的 XFlash 命令八字 NAND 扩展配置。
/// 主机不执行七字布局回退、坏块重映射、UBI 转换或 FTL 转换。</para></summary>
public sealed record MtkXFlashNandPolicy
{
    /// <summary>Gets the address interpretation. <para>获取地址解释方式。</para></summary>
    /// <value>The explicitly selected logical, physical or physical-PMT mode. <para>显式选定的逻辑、物理或 physical-PMT 模式。</para></value>
    public required MtkXFlashNandAddressType AddressType { get; init; }
    /// <summary>Gets the cell-use field. <para>获取单元使用字段。</para></summary>
    /// <value>The caller's single-level or multi-level choice. <para>调用方选定的单层或多层使用方式。</para></value>
    public required MtkXFlashNandCellUsage CellUsage { get; init; }
    /// <summary>Gets the storage selector. <para>获取存储标识符。</para></summary>
    /// <value>The generic selector by default; a subtype is never inferred from device type.
    /// <para>默认为通用标识符；不会根据设备类型推断子类型。</para></value>
    public MtkXFlashNandStorageType StorageType { get; init; } = MtkXFlashNandStorageType.Generic;
    /// <summary>Gets the region word in the eight-word layout. <para>获取八字布局中的区域字段。</para></summary>
    /// <value>The caller-selected agent region, defaulting to zero. <para>调用方选定的代理区域，默认为零。</para></value>
    public uint Region { get; init; }
    /// <summary>Gets the system SLC percentage sent to the agent. <para>获取发送给代理的系统 SLC 百分比。</para></summary>
    /// <value>A value from zero through one hundred. <para>零至一百的数值。</para></value>
    public uint SystemSlcPercent { get; init; }
    /// <summary>Gets the user SLC percentage sent to the agent. <para>获取发送给代理的用户 SLC 百分比。</para></summary>
    /// <value>A value from zero through one hundred. <para>零至一百的数值。</para></value>
    public uint UserSlcPercent { get; init; }
    /// <summary>Gets the profile's physical partition size limit in KiB. <para>获取配置中的物理分区大小上限，单位为 KiB。</para></summary>
    /// <value>Zero to leave the agent field unset, otherwise a selected limit within total physical capacity.
    /// <para>零表示不设置代理字段，否则为总物理容量内选定的上限。</para></value>
    public uint PhysicalMaximumSizeKiB { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(AddressType) || !Enum.IsDefined(CellUsage) || !Enum.IsDefined(StorageType)
            || SystemSlcPercent > 100 || UserSlcPercent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MtkXFlashNandPolicy), "The NAND policy contains an unsupported selector or percentage.");
        }
    }

    internal byte[] CreateParameters(MtkXFlashNandGeometry geometry, long offset, long mainLength, long commandLength,
        uint operation, bool erase)
    {
        Validate();
        geometry.ValidateRange(AddressType, offset, mainLength, erase);
        var maximumBytes = (long)PhysicalMaximumSizeKiB * 1024;
        if (maximumBytes != 0 && (maximumBytes > geometry.TotalSize || maximumBytes < mainLength))
        {
            throw new ArgumentOutOfRangeException(nameof(PhysicalMaximumSizeKiB));
        }
        var bytes = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)StorageType);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ulong)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), (ulong)commandLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)CellUsage);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)AddressType);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), Region);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), operation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), SystemSlcPercent);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), UserSlcPercent);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), PhysicalMaximumSizeKiB);
        return bytes;
    }
}

/// <summary>Defines an explicit NAND page-stream profile without inferring mode or command-length units.
/// The selected profile must match the DA; command rejection is not retried with another profile.
/// <para>定义显式 NAND 页流配置，不推断模式或命令长度单位。
/// 选定配置必须匹配 DA；命令被拒绝后不会换配置重试。</para></summary>
public sealed record MtkXFlashNandTransferOptions
{
    /// <summary>Gets the addressing and cell-use policy. <para>获取寻址与单元使用策略。</para></summary>
    /// <value>The caller-selected immutable policy. <para>调用方选定的不可变策略。</para></value>
    public required MtkXFlashNandPolicy Policy { get; init; }
    /// <summary>Gets the page-stream mode. <para>获取页流模式。</para></summary>
    /// <value>The explicit page, spare or combined mode. <para>显式的主页、备用区或组合模式。</para></value>
    public required MtkXFlashNandTransferMode Mode { get; init; }
    /// <summary>Gets the command-length encoding. <para>获取命令长度编码方式。</para></summary>
    /// <value>The explicit profile choice, independent of host stream byte count.
    /// <para>显式的配置选择，与主机流字节计数分别处理。</para></value>
    public required MtkXFlashNandLengthUnit LengthUnit { get; init; }

    /// <summary>Computes the exact stream length for a main-area span with checked arithmetic.
    /// <para>使用受检查算术计算主区跨度对应的精确流长度。</para></summary>
    /// <param name="geometry">Validated device geometry. <para>经校验的设备几何信息。</para></param>
    /// <param name="mainLength">The positive page-aligned main-area byte count.
    /// <para>正数且按页对齐的主区字节数。</para></param>
    /// <returns>The main, spare or interleaved byte count required from or written to the stream.
    /// <para>必须从流读取或向流写入的主页、备用区或交错字节数。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The length or profile is invalid or the result exceeds Int64.
    /// <para>长度或配置无效，或结果超出 Int64。</para></exception>
    public long GetTransferLength(MtkXFlashNandGeometry geometry, long mainLength)
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
        ArgumentNullException.ThrowIfNull(Policy);
        Policy.Validate();
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(LengthUnit))
        {
            throw new ArgumentOutOfRangeException(nameof(MtkXFlashNandTransferOptions));
        }
    }

    internal int GetRecordSize(MtkXFlashNandGeometry geometry)
    {
        Validate();
        if (Mode is MtkXFlashNandTransferMode.PageOnly or MtkXFlashNandTransferMode.PageWithEcc)
        {
            return geometry.PageSize;
        }
        if (geometry.SpareSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Mode), "The selected NAND mode requires a reported spare area.");
        }
        return Mode == MtkXFlashNandTransferMode.SpareOnly ? geometry.SpareSize : geometry.PageSize + geometry.SpareSize;
    }
}