namespace UotanStudio.Firmware.BootSecurity.BootImage.Models;

/// <summary>
/// Semantic values of the <c>ramdisk_type</c> field in a version 4 vendor boot ramdisk table entry:
/// 0 <c>NONE</c>, 1 <c>PLATFORM</c>, 2 <c>RECOVERY</c> and 3 <c>DLKM</c>.
/// <para>版本 4 Vendor Boot RAMDisk 表条目中 <c>ramdisk_type</c> 字段的语义取值：
/// 0 <c>NONE</c>、1 <c>PLATFORM</c>、2 <c>RECOVERY</c> 与 3 <c>DLKM</c>。</para>
/// </summary>
/// <remarks>
/// The field classifies ramdisk purpose, not compression: the vendor ramdisk section stores each sub-ramdisk in the
/// encoding chosen by the build, and the table's <c>type</c> word tells the bootloader which ramdisks to load.
/// Values outside 0 through 3 are vendor extensions and are preserved verbatim by parse and pack.
/// <para>该字段描述 RAMDisk 用途而非压缩方式：vendor ramdisk 区段按构建选择的方式存放各子 RAMDisk，表中的 <c>type</c> 字告诉引导加载器应加载
/// 哪些 RAMDisk。0 至 3 之外的取值属于厂商扩展，解析与打包时逐字节原样保留。</para>
/// </remarks>
public enum VendorRamdiskType : uint
{
    /// <summary>Type is unspecified (<c>VENDOR_RAMDISK_TYPE_NONE</c>).
    /// <para>类型未指定（<c>VENDOR_RAMDISK_TYPE_NONE</c>）。</para></summary>
    None = 0,

    /// <summary>Platform-specific ramdisk that the bootloader should always load into memory
    /// (<c>VENDOR_RAMDISK_TYPE_PLATFORM</c>). <para>平台专有 RAMDisk，引导加载器应始终加载入内存
    /// （<c>VENDOR_RAMDISK_TYPE_PLATFORM</c>）。</para></summary>
    Platform = 1,

    /// <summary>Recovery-resource ramdisk loaded only when booting into recovery
    /// (<c>VENDOR_RAMDISK_TYPE_RECOVERY</c>). <para>仅在进入 recovery 启动时加载的 recovery 资源 RAMDisk
    /// （<c>VENDOR_RAMDISK_TYPE_RECOVERY</c>）。</para></summary>
    Recovery = 2,

    /// <summary>Ramdisk containing dynamic loadable kernel modules (<c>VENDOR_RAMDISK_TYPE_DLKM</c>).
    /// <para>包含动态可加载内核模块的 RAMDisk（<c>VENDOR_RAMDISK_TYPE_DLKM</c>）。</para></summary>
    Dlkm = 3
}