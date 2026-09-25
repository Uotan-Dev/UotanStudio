namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;

/// <summary>
/// Boot image header generations recognized by the boot image parser and packer.
/// <para>引导镜像解析器与打包器所识别的引导镜像头部代际。</para>
/// </summary>
public enum BootImageVersion
{
    /// <summary>Sentinel for a header whose version could not be matched to a known generation. <para>头部版本无法匹配到已知代际时的哨兵值。</para></summary>
    Unknown = -1,

    /// <summary>Header version 0, the original Android boot image layout. <para>头部版本 0，即最初的 Android 引导镜像布局。</para></summary>
    V0 = 0,

    /// <summary>Header version 1. <para>头部版本 1。</para></summary>
    V1 = 1,

    /// <summary>Header version 2. <para>头部版本 2。</para></summary>
    V2 = 2,

    /// <summary>Header version 3. <para>头部版本 3。</para></summary>
    V3 = 3,

    /// <summary>Header version 4, the newest layout the parser and packer accept. <para>头部版本 4，解析器与打包器接受的最新布局。</para></summary>
    V4 = 4
}