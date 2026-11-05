namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// Role an IMAGEWTY entry plays in an Allwinner firmware package, resolved from the vendor
/// <c>image.cfg</c> file list by its main type and sub type.
/// <para>IMAGEWTY 条目在全志固件包中的角色，由厂商 <c>image.cfg</c> 文件清单按主类型与子类型解析得到。</para>
/// </summary>
public enum AllwinnerImageItem
{
    /// <summary>No vendor file list entry matches the record. <para>没有厂商文件清单条目与该记录匹配。</para></summary>
    Unknown,

    /// <summary>FES1 boot loader uploaded over FEL before the device leaves USB boot mode. <para>设备离开 USB 引导模式前经 FEL 上传的 FES1 引导程序。</para></summary>
    Fes1,

    /// <summary>Main U-Boot image that provides the FES service. <para>提供 FES 服务的主 U-Boot 镜像。</para></summary>
    UBoot,

    /// <summary>Crash-recovery U-Boot image. <para>崩溃恢复用 U-Boot 镜像。</para></summary>
    UBootCrash,

    /// <summary>Sunxi MBR partition table written at the start of storage. <para>写入存储起始处的 Sunxi MBR 分区表。</para></summary>
    Mbr,

    /// <summary>Sunxi GPT partition table. <para>Sunxi GPT 分区表。</para></summary>
    Gpt,

    /// <summary>Text <c>sys_config</c> board configuration. <para>文本形式的 <c>sys_config</c> 板级配置。</para></summary>
    SysConfig,

    /// <summary>Compiled <c>sys_config</c> binary. <para>编译后的 <c>sys_config</c> 二进制。</para></summary>
    SysConfigBinary,

    /// <summary>Partition definition consumed by the flashing flow. <para>烧录流程使用的分区定义。</para></summary>
    SysPartition,

    /// <summary>Board configuration binary. <para>板级配置二进制。</para></summary>
    BoardConfig,

    /// <summary>Device tree blob consumed by the boot loaders. <para>引导程序使用的设备树。</para></summary>
    DeviceTree,

    /// <summary>Split package descriptor. <para>分包描述文件。</para></summary>
    Split,

    /// <summary>Preboot image uploaded before the boot chain in normal and package boot modes.
    /// <para>正常与打包启动模式下在引导链之前上传的预启动镜像。</para></summary>
    Preboot,

    /// <summary>Preboot image for the TOC trust chain. <para>TOC 信任链使用的预启动镜像。</para></summary>
    Toc0Preboot,

    /// <summary>NAND first-stage boot loader. <para>NAND 一级引导程序。</para></summary>
    Boot0Nand,

    /// <summary>SD card first-stage boot loader. <para>SD 卡一级引导程序。</para></summary>
    Boot0Card,

    /// <summary>SPI NOR first-stage boot loader. <para>SPI NOR 一级引导程序。</para></summary>
    Boot0Nor,

    /// <summary>UFS first-stage boot loader. <para>UFS 一级引导程序。</para></summary>
    Boot0Ufs,

    /// <summary>Boot package bundling the first-stage loaders. <para>打包一级引导程序的启动包。</para></summary>
    BootPackage,

    /// <summary>SPI NOR boot package. <para>SPI NOR 启动包。</para></summary>
    BootPackageNor,

    /// <summary>TOC0 trust chain descriptor. <para>TOC0 信任链描述。</para></summary>
    Toc0,

    /// <summary>SD card TOC0 descriptor. <para>SD 卡 TOC0 描述。</para></summary>
    Toc0SdCard,

    /// <summary>NAND TOC0 descriptor. <para>NAND TOC0 描述。</para></summary>
    Toc0Nand,

    /// <summary>SPI NOR TOC0 descriptor. <para>SPI NOR TOC0 描述。</para></summary>
    Toc0Nor,

    /// <summary>UFS TOC0 descriptor. <para>UFS TOC0 描述。</para></summary>
    Toc0Ufs,

    /// <summary>TOC1 trust chain descriptor. <para>TOC1 信任链描述。</para></summary>
    Toc1,

    /// <summary>Card tool payload. <para>卡工具负载。</para></summary>
    CardTool,

    /// <summary>Card tool script. <para>卡工具脚本。</para></summary>
    CardScript,

    /// <summary>USB tool payload. <para>USB 工具负载。</para></summary>
    UsbTool,

    /// <summary>USB tool crash-recovery payload. <para>USB 工具崩溃恢复负载。</para></summary>
    UsbToolCrash,

    /// <summary>Firmware upgrade tool payload. <para>固件升级工具负载。</para></summary>
    Aultools,

    /// <summary>32-bit firmware upgrade tool. <para>32 位固件升级工具。</para></summary>
    Aultls32,

    /// <summary>Download information descriptor. <para>下载信息描述。</para></summary>
    Dlinfo,

    /// <summary>ARISC coprocessor firmware. <para>ARISC 协处理器固件。</para></summary>
    Arisc,

    /// <summary>Kernel image entry. <para>内核镜像条目。</para></summary>
    Vmlinux
}

/// <summary>
/// Vendor file list of an Allwinner firmware package, mapping the main type and sub type stored in
/// each IMAGEWTY record to the role the flashing flow expects.
/// <para>全志固件包的厂商文件清单，把每条 IMAGEWTY 记录中存储的主类型与子类型映射到烧录流程期望的角色。</para>
/// </summary>
internal static class AllwinnerImageItemTable
{
    private readonly record struct Signature(AllwinnerImageItem Item, string FileName, string MainType, string SubType);

    private static readonly Signature[] Signatures =
    [
        new(AllwinnerImageItem.SysConfig, "sys_config.fex", "COMMON", "SYS_CONFIG100000"),
        new(AllwinnerImageItem.SysConfigBinary, "config.fex", "COMMON", "SYS_CONFIG_BIN00"),
        new(AllwinnerImageItem.BoardConfig, "board.fex", "COMMON", "BOARD_CONFIG_BIN"),
        new(AllwinnerImageItem.Split, "split_xxxx.fex", "COMMON", "SPLIT_0000000000"),
        new(AllwinnerImageItem.SysPartition, "sys_partition.fex", "COMMON", "SYS_CONFIG000000"),
        new(AllwinnerImageItem.DeviceTree, "sunxi.fex", "COMMON", "DTB_CONFIG000000"),
        new(AllwinnerImageItem.Preboot, "preboot.fex", "12345678", "1234567890PREB_0"),
        new(AllwinnerImageItem.Toc0Preboot, "toc0_preboot.fex", "12345678", "TOC0_PREBOOT0000"),
        new(AllwinnerImageItem.Boot0Nand, "boot0_nand.fex", "BOOT", "BOOT0_0000000000"),
        new(AllwinnerImageItem.Boot0Card, "boot0_sdcard.fex", "12345678", "1234567890BOOT_0"),
        new(AllwinnerImageItem.Boot0Nor, "boot0_spinor.fex", "12345678", "1234567890BNOR_0"),
        new(AllwinnerImageItem.Boot0Ufs, "boot0_ufs.fex", "12345678", "1234567890BUFS_0"),
        new(AllwinnerImageItem.UBoot, "u-boot-efex.fex", "12345678", "UBOOT_0000000000"),
        new(AllwinnerImageItem.UBootCrash, "u-boot-crash.fex", "12345678", "UBOOT_CRASH_0000"),
        new(AllwinnerImageItem.Toc1, "toc1.fex", "12345678", "TOC1_00000000000"),
        new(AllwinnerImageItem.Toc0, "toc0.fex", "12345678", "TOC0_00000000000"),
        new(AllwinnerImageItem.Fes1, "fes1.fex", "FES", "FES_1-0000000000"),
        new(AllwinnerImageItem.BootPackage, "boot_package.fex", "12345678", "BOOTPKG-00000000"),
        new(AllwinnerImageItem.BootPackageNor, "boot_package_nor.fex", "12345678", "BOOTPKG-NOR00000"),
        new(AllwinnerImageItem.Toc0SdCard, "toc0_sdcard.fex", "12345678", "TOC0_SDCARD00000"),
        new(AllwinnerImageItem.Toc0Nand, "toc0_nand.fex", "12345678", "TOC0_NAND0000000"),
        new(AllwinnerImageItem.Toc0Nor, "toc0_nor.fex", "12345678", "TOC0_SPINOR00000"),
        new(AllwinnerImageItem.Toc0Ufs, "toc0_ufs.fex", "12345678", "TOC0_UFS00000000"),
        new(AllwinnerImageItem.UsbTool, "usbtool.fex", "PXTOOLSB", "xxxxxxxxxxxxxxxx"),
        new(AllwinnerImageItem.UsbToolCrash, "usbtool_crash.fex", "PXTOOLCH", "xxxxxxxxxxxxxxxx"),
        new(AllwinnerImageItem.Aultools, "aultools.fex", "UPFLYTLS", "xxxxxxxxxxxxxxxx"),
        new(AllwinnerImageItem.Aultls32, "aultls32.fex", "UPFLTL32", "xxxxxxxxxxxxxxxx"),
        new(AllwinnerImageItem.CardTool, "cardtool.fex", "12345678", "1234567890cardtl"),
        new(AllwinnerImageItem.CardScript, "cardscript.fex", "12345678", "1234567890script"),
        new(AllwinnerImageItem.Gpt, "sunxi_gpt.fex", "12345678", "1234567890___GPT"),
        new(AllwinnerImageItem.Mbr, "sunxi_mbr.fex", "12345678", "1234567890___MBR"),
        new(AllwinnerImageItem.Dlinfo, "dlinfo.fex", "12345678", "1234567890DLINFO"),
        new(AllwinnerImageItem.Arisc, "arisc.fex", "12345678", "1234567890ARISC"),
        new(AllwinnerImageItem.Vmlinux, "vmlinux.fex", "12345678", "123456789VMLINUX")
    ];

    /// <summary>
    /// Resolves the role of one record from its declared main type and sub type; the comparison
    /// ignores the NUL and space padding that fixed-width records carry.
    /// <para>由记录声明的主类型与子类型解析其角色；比较会忽略定长记录携带的 NUL 与空格填充。</para>
    /// </summary>
    /// <param name="mainType">Main type field of the record. <para>记录的主类型字段。</para></param>
    /// <param name="subType">Sub type field of the record. <para>记录的子类型字段。</para></param>
    /// <returns>The matching role, or <see cref="AllwinnerImageItem.Unknown"/>.
    /// <para>匹配的角色；没有匹配时为 <see cref="AllwinnerImageItem.Unknown"/>。</para></returns>
    public static AllwinnerImageItem Resolve(ReadOnlySpan<char> mainType, ReadOnlySpan<char> subType)
    {
        var normalizedMain = mainType.TrimEnd(['\0', ' ']);
        var normalizedSub = subType.TrimEnd(['\0', ' ']);
        foreach (var signature in Signatures)
        {
            if (normalizedSub.SequenceEqual(signature.SubType)
                && normalizedMain.SequenceEqual(signature.MainType))
            {
                return signature.Item;
            }
        }

        return AllwinnerImageItem.Unknown;
    }

    /// <summary>
    /// Returns the vendor file name of a role, which is what an <c>image.cfg</c> driven flow expects
    /// next to the container.
    /// <para>返回角色的厂商文件名，即 <c>image.cfg</c> 驱动的流程在容器旁期望的名称。</para>
    /// </summary>
    /// <param name="item">Role to look up. <para>待查询的角色。</para></param>
    /// <returns>The file name, or <see langword="null"/> for <see cref="AllwinnerImageItem.Unknown"/>.
    /// <para>文件名；<see cref="AllwinnerImageItem.Unknown"/> 时为 <see langword="null"/>。</para></returns>
    public static string? GetFileName(AllwinnerImageItem item)
    {
        foreach (var signature in Signatures)
        {
            if (signature.Item == item)
            {
                return signature.FileName;
            }
        }

        return null;
    }
}