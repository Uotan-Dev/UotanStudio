namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Chip identity an nv3p platform-info reply reports: the Tegra part number plus its silicon revision.
/// <para>nv3p 平台信息应答上报的芯片身份：Tegra 器件型号及其硅片版本。</para>
/// </summary>
/// <param name="Id">The 16-bit chip id word. <para>16 位芯片 ID 字。</para></param>
/// <param name="Major">Major revision. <para>主版本号。</para></param>
/// <param name="Minor">Minor revision. <para>次版本号。</para></param>
public readonly record struct TegraChipId(ushort Id, byte Major, byte Minor)
{
    /// <summary>
    /// Formats the identity the way tegrarcm prints it.
    /// <para>按 tegrarcm 的打印方式格式化该身份。</para>
    /// </summary>
    /// <returns>The chip id with its revision, such as <c>0x35 (1.2)</c>.
    /// <para>带版本的芯片 ID，例如 <c>0x35 (1.2)</c>。</para></returns>
    public override string ToString() => $"0x{Id:X2} ({Major}.{Minor})";
}

/// <summary>
/// Board identity an nv3p platform-info reply reports: board number, fabrication, memory type and the memory
/// frequency strap.
/// <para>nv3p 平台信息应答上报的板卡身份：板号、工艺、内存类型与内存频率跳线。</para>
/// </summary>
/// <param name="BoardNumber">The board number. <para>板号。</para></param>
/// <param name="Fabrication">The fabrication revision. <para>工艺版本。</para></param>
/// <param name="MemoryType">The memory type strap. <para>内存类型跳线。</para></param>
/// <param name="Frequency">The memory frequency strap. <para>内存频率跳线。</para></param>
public readonly record struct TegraBoardId(uint BoardNumber, uint Fabrication, uint MemoryType, uint Frequency);

/// <summary>
/// Everything the nv3p <c>GET_PLATFORM_INFO</c> command reports, mirroring <c>nv3p_platform_info_t</c> from
/// tegrarcm field for field.
/// <para>nv3p <c>GET_PLATFORM_INFO</c> 命令上报的全部内容，逐字段对应 tegrarcm 的 <c>nv3p_platform_info_t</c>。</para>
/// </summary>
/// <remarks>
/// The values decide what the host may do next: <see cref="OperatingMode"/> gates whether a BCT and bootloader may
/// be downloaded at all, and <see cref="BootDevice"/> selects which BCT the bundle lookup expects.
/// <para>这些取值决定主机接下来可以做什么：<see cref="OperatingMode"/> 决定能否下载 BCT 与 bootloader，
/// <see cref="BootDevice"/> 决定引导包查找时预期哪一种 BCT。</para>
/// </remarks>
public sealed class TegraPlatformInfo
{
    /// <summary>Gets the two-word unique chip id. <para>获取双字唯一芯片 ID。</para></summary>
    /// <value>The device unique id exactly as reported. <para>设备上报的唯一 ID 原样保留。</para></value>
    public required ulong UniqueIdLow { get; init; }

    /// <summary>Gets the high word of the unique chip id. <para>获取唯一芯片 ID 的高位字。</para></summary>
    /// <value>The high word as reported. <para>上报的高位字。</para></value>
    public required ulong UniqueIdHigh { get; init; }

    /// <summary>Gets the chip identity. <para>获取芯片身份。</para></summary>
    /// <value>The part number and revision. <para>器件型号与版本。</para></value>
    public required TegraChipId Chip { get; init; }

    /// <summary>Gets the chip SKU code. <para>获取芯片 SKU 编码。</para></summary>
    /// <value>The SKU the boot ROM reports. <para>boot ROM 上报的 SKU。</para></value>
    public required uint Sku { get; init; }

    /// <summary>Gets the boot ROM version word. <para>获取 boot ROM 版本字。</para></summary>
    /// <value>The version as reported. <para>上报的版本。</para></value>
    public required uint Version { get; init; }

    /// <summary>Gets the boot device type, for example eMMC. <para>获取引导设备类型，例如 eMMC。</para></summary>
    /// <value>One of the <c>NV3P_DEV_TYPE_*</c> values. <para><c>NV3P_DEV_TYPE_*</c> 之一。</para></value>
    public required uint BootDevice { get; init; }

    /// <summary>Gets the security operating mode. <para>获取安全操作模式。</para></summary>
    /// <value>One of the <c>RCM_OP_MODE_*</c> values. <para><c>RCM_OP_MODE_*</c> 之一。</para></value>
    public required uint OperatingMode { get; init; }

    /// <summary>Gets the device configuration strap word. <para>获取设备配置跳线字。</para></summary>
    /// <value>The strap word as reported. <para>上报的跳线字。</para></value>
    public required uint DeviceConfigurationStrap { get; init; }

    /// <summary>Gets the device configuration fuse word. <para>获取设备配置熔丝字。</para></summary>
    /// <value>The fuse word as reported. <para>上报的熔丝字。</para></value>
    public required uint DeviceConfigurationFuse { get; init; }

    /// <summary>Gets the SDRAM configuration strap word. <para>获取 SDRAM 配置跳线字。</para></summary>
    /// <value>The strap word as reported. <para>上报的跳线字。</para></value>
    public required uint SdramConfigurationStrap { get; init; }

    /// <summary>Gets the board identity. <para>获取板卡身份。</para></summary>
    /// <value>The reported board number and straps. <para>上报的板号与跳线。</para></value>
    public required TegraBoardId Board { get; init; }

    /// <summary>
    /// Gets whether the reported operating mode allows downloading a BCT and bootloader, using the same five modes
    /// tegrarcm accepts.
    /// <para>获取上报的操作模式是否允许下载 BCT 与 bootloader，采用 tegrarcm 接受的同五个模式。</para>
    /// </summary>
    /// <value><see langword="true"/> for the pre-production, developer and ODM modes.
    /// <para>预生产、开发者与 ODM 模式为 <see langword="true"/>。</para></value>
    public bool IsFlashable => TegraConstants.IsFlashableOperatingMode(OperatingMode);

    /// <summary>Gets the chip generation implied by <see cref="Chip"/>. <para>获取由 <see cref="Chip"/> 推出的芯片代际。</para></summary>
    /// <value>The inferred generation, or <see cref="TegraChip.Unknown"/> when the part number is not one of the four.
    /// <para>推断出的代际；器件型号不属于四种之一时为 <see cref="TegraChip.Unknown"/>。</para></value>
    public TegraChip Generation => (Chip.Id & 0xff) switch
    {
        0x20 => TegraChip.Tegra20,
        0x30 => TegraChip.Tegra30,
        0x35 => TegraChip.Tegra114,
        0x40 => TegraChip.Tegra124,
        _ => TegraChip.Unknown
    };
}