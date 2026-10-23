namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Wire-level constants for SpacemiT K1/K3 fastboot flashing (BootROM FDL mode and
/// U-Boot fastboot gadget), matching the vendor fastboot.yaml flow and the device
/// USB identities observed by the vendor flasher.
/// <para>SpacemiT K1/K3 fastboot 烧录（BootROM FDL 模式与 U-Boot fastboot gadget）的
/// 线协议常量，与厂商 fastboot.yaml 刷机流程及其烧录器使用的设备 USB 身份一致。</para>
/// </summary>
public static class SpacemiTConstants
{
    /// <summary>
    /// USB vendor id reported by SpacemiT fastboot gadgets (BootROM and U-Boot).
    /// <para>SpacemiT fastboot gadget（BootROM 与 U-Boot）上报的 USB 厂商 ID。</para>
    /// </summary>
    public const ushort UsbVendorId = 0x361C;

    /// <summary>
    /// USB product id reported by SpacemiT fastboot gadgets (BootROM and U-Boot).
    /// <para>SpacemiT fastboot gadget（BootROM 与 U-Boot）上报的 USB 产品 ID。</para>
    /// </summary>
    public const ushort UsbProductId = 0x1001;

    /// <summary>
    /// getvar key answered only by the BootROM fastboot gadget; a non-empty value marks
    /// the device as being in FDL mode and needing FSBL/U-Boot staging.
    /// <para>仅 BootROM fastboot gadget 应答的 getvar 键；非空值表示设备处于 FDL 模式，
    /// 需要 stage FSBL/U-Boot。</para>
    /// </summary>
    public const string VarBromVersion = "version-brom";

    /// <summary>
    /// getvar key reporting the SPI NOR (MTD) storage size, used to select
    /// <c>partition_{size}.json</c> for the MTD partition table.
    /// <para>上报 SPI NOR（MTD）存储大小的 getvar 键，用于为 MTD 分区表选择
    /// <c>partition_{size}.json</c>。</para>
    /// </summary>
    public const string VarMtdSize = "mtd-size";

    /// <summary>
    /// getvar key reporting the block storage size, used to select
    /// <c>partition_{size}.json</c> for the GPT partition table.
    /// <para>上报块存储大小的 getvar 键，用于为 GPT 分区表选择
    /// <c>partition_{size}.json</c>。</para>
    /// </summary>
    public const string VarBlockSize = "blk-size";

    /// <summary>
    /// OEM command body that asks the U-Boot gadget to switch the USB link to
    /// super speed; the device re-enumerates afterwards.
    /// <para>请求 U-Boot gadget 将 USB 链路切换到 super speed 的 OEM 命令体；
    /// 设备随后会重新枚举。</para>
    /// </summary>
    public const string OemSuperSpeed = "speed:super-speed";

    /// <summary>
    /// Template whose <c>{0}</c> placeholder is replaced with the raw getvar value of
    /// <see cref="VarMtdSize"/> or <see cref="VarBlockSize"/> to form the partition
    /// table file name inside the flash package.
    /// <para>分区表文件名模板：<c>{0}</c> 占位符被替换为 <see cref="VarMtdSize"/> 或
    /// <see cref="VarBlockSize"/> 的 getvar 原始值，得到烧录包内的分区表文件名。</para>
    /// </summary>
    public const string PartitionFileTemplate = "partition_{0}.json";

    /// <summary>
    /// Default pause inserted after a <c>continue</c> action before re-scanning for the
    /// re-enumerated device, matching the vendor reference flow's settle delay.
    /// <para><c>continue</c> 动作之后、重新扫描重新枚举设备之前插入的默认暂停，
    /// 与厂商参考流程的等待时间一致。</para>
    /// </summary>
    public const int ReEnumerateSettleMs = 10_000;

    /// <summary>
    /// Minimum payload a partition entry must reference for a flash attempt; entries
    /// without an <c>image</c> field (offset/size-only placeholders) are skipped.
    /// <para>分区条目发起刷写所需的最小引用；没有 <c>image</c> 字段的条目
    /// （仅偏移/大小的占位项）会被跳过。</para>
    /// </summary>
    public const string PartitionFormatGpt = "gpt";

    /// <summary>
    /// Partition table format value selecting the MTD (SPI NOR) writer on the device.
    /// <para>选择设备端 MTD（SPI NOR）写入器的分区表 format 值。</para>
    /// </summary>
    public const string PartitionFormatMtd = "mtd";
}