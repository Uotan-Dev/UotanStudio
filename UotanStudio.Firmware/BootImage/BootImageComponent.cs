namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// 启动镜像的组件类型枚举。
/// <para>Enumeration of boot image component types.</para>
/// <para>定义了 Android 启动镜像中包含的各种组件，用于解析和重打包过程。</para>
/// <para>Defines the various components contained in an Android boot image, used during parsing and repacking.</para>
/// </summary>
public enum BootImageComponent
{
    /// <summary>
    /// 内核镜像。
    /// <para>Kernel image.</para>
    /// </summary>
    Kernel,

    /// <summary>
    /// 内核设备树。
    /// <para>Kernel device tree blob.</para>
    /// </summary>
    KernelDtb,

    /// <summary>
    /// RAMDisk (CPIO 归档)。
    /// <para>RAMDisk (CPIO archive).</para>
    /// </summary>
    RamdiskCpio,

    /// <summary>
    /// 第二阶段引导程序。
    /// <para>Second stage loader.</para>
    /// </summary>
    Second,

    /// <summary>
    /// 设备树 Blob。
    /// <para>Device Tree Blob.</para>
    /// </summary>
    Dtb,

    /// <summary>
    /// 额外数据区。
    /// <para>Extra data section.</para>
    /// </summary>
    Extra,

    /// <summary>
    /// Recovery 设备树。
    /// <para>Recovery DTBO (Device Tree Blob Overlay).</para>
    /// </summary>
    RecoveryDtbo,

    /// <summary>
    /// 图像头部信息。
    /// <para>Image header information.</para>
    /// </summary>
    Header,

    /// <summary>
    /// 未知组件。
    /// <para>Unknown component.</para>
    /// </summary>
    Unknown
}