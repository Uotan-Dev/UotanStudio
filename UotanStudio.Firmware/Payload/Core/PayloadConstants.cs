namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Defines constants used by the Android OTA payload format.
/// <para>定义 Android OTA 载荷格式使用的常量。</para>
/// </summary>
internal static class PayloadConstants
{
    /// <summary>
    /// The magic bytes that identify a ZIP archive.
    /// <para>标识 ZIP 归档的魔数字节。</para>
    /// </summary>
    internal const string ZipMagic = "PK";

    /// <summary>
    /// The magic value that identifies an Android OTA payload.
    /// <para>标识 Android OTA 载荷的魔数值。</para>
    /// </summary>
    internal const string PayloadMagic = "CrAU";

    /// <summary>
    /// The canonical file name of a payload inside an OTA ZIP archive.
    /// <para>OTA ZIP 归档中载荷的标准文件名。</para>
    /// </summary>
    internal const string PayloadFilename = "payload.bin";
}