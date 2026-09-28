namespace UotanStudio.Firmware.BootSecurity.BootImage.Models;

/// <summary>
/// Identifies a component section of an Android boot image when it is unpacked or inspected.
/// <para>标识 Android boot image 中各组件段，用于解包或检查。</para>
/// </summary>
public enum BootImageComponent
{
    /// <summary>Kernel section. <para>内核段。</para></summary>
    Kernel,

    /// <summary>Device tree appended to the kernel section. <para>附加在内核段之后的设备树。</para></summary>
    KernelDtb,

    /// <summary>Ramdisk section stored as uncompressed or compressed cpio. <para>以未压缩或压缩 cpio 保存的 ramdisk 段。</para></summary>
    RamdiskCpio,

    /// <summary>Second-stage bootloader section. <para>二级引导加载程序段。</para></summary>
    Second,

    /// <summary>Standalone DTB section carried by the image layout. <para>由镜像布局承载的独立 DTB 段。</para></summary>
    Dtb,

    /// <summary>Extra vendor data beyond the standard sections. <para>标准段之外的额外厂商数据。</para></summary>
    Extra,

    /// <summary>Recovery DTBO section referenced by the header fields. <para>头部字段引用的 recovery DTBO 段。</para></summary>
    RecoveryDtbo,

    /// <summary>The boot image header block itself. <para>boot image 头部块本身。</para></summary>
    Header,

    /// <summary>Section that could not be classified into a known component. <para>无法归入已知组件的段。</para></summary>
    Unknown
}