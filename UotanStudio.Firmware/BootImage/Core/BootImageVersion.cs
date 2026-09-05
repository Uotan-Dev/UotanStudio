namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Android 启动镜像头部版本。
/// <para>Android boot image header version.</para>
/// </summary>
public enum BootImageVersion
{
    /// <summary>
    /// 未知版本。
    /// <para>Unknown version.</para>
    /// </summary>
    Unknown = -1,

    /// <summary>
    /// Android 启动镜像头部版本 0（原始格式）。
    /// <para>Android boot image header version 0 (original format).</para>
    /// </summary>
    V0 = 0,

    /// <summary>
    /// Android 启动镜像头部版本 1（添加 recovery_dtbo）。
    /// <para>Android boot image header version 1 (adds recovery_dtbo).</para>
    /// </summary>
    V1 = 1,

    /// <summary>
    /// Android 启动镜像头部版本 2（添加 dtb）。
    /// <para>Android boot image header version 2 (adds dtb).</para>
    /// </summary>
    V2 = 2,

    /// <summary>
    /// Android 启动镜像头部版本 3（vendor_boot 格式）。
    /// <para>Android boot image header version 3 (vendor_boot format).</para>
    /// </summary>
    V3 = 3,

    /// <summary>
    /// Android 启动镜像头部版本 4（添加签名）。
    /// <para>Android boot image header version 4 (adds signature).</para>
    /// </summary>
    V4 = 4
}