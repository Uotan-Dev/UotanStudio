namespace UotanStudio.Firmware.VendorFirmware.Oppo.Models;

/// <summary>
/// Identifies which concrete Oppo firmware container a file uses.
/// <para>标识文件所使用的具体 Oppo 固件容器类型。</para>
/// </summary>
public enum OppFormat
{
    /// <summary>
    /// The format could not be determined.
    /// <para>无法确定格式。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Qualcomm-based OFP package.
    /// <para>基于高通平台的 OFP 包。</para>
    /// </summary>
    OfpQc = 1,

    /// <summary>
    /// MediaTek-based OFP package.
    /// <para>基于联发科平台的 OFP 包。</para>
    /// </summary>
    OfpMtk = 2,

    /// <summary>
    /// OPS firmware package.
    /// <para>OPS 固件包。</para>
    /// </summary>
    Ops = 3,

    /// <summary>
    /// OZIP encrypted archive package.
    /// <para>OZIP 加密归档包。</para>
    /// </summary>
    Ozip = 4
}