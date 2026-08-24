namespace UotanStudio.Firmware.Oppo.Models;

/// <summary>
/// <para>指定 Oppo 固件文件的格式类型。</para>
/// Specifies the format type of an Oppo firmware file.
/// </summary>
public enum OppFormat
{
    /// <summary>
    /// <para>未知格式，需要自动检测。</para>
    /// Unknown format, requires auto-detection.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// <para>OFP 高通（Qualcomm）芯片固件格式。</para>
    /// OFP Qualcomm chipset firmware format.
    /// </summary>
    OfpQc = 1,

    /// <summary>
    /// <para>OFP 联发科（MediaTek）芯片固件格式。</para>
    /// OFP MediaTek chipset firmware format.
    /// </summary>
    OfpMtk = 2,

    /// <summary>
    /// <para>OnePlus OPS 固件格式。</para>
    /// OnePlus OPS firmware format.
    /// </summary>
    Ops = 3,

    /// <summary>
    /// <para>Oppo OZip 加密固件格式。</para>
    /// Oppo OZip encrypted firmware format.
    /// </summary>
    Ozip = 4
}