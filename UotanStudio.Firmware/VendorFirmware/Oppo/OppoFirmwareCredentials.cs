namespace UotanStudio.Firmware.VendorFirmware.Oppo;

/// <summary>
/// Fixed credential material built into Oppo vendor firmware containers.
/// <para>Oppo 厂商固件容器内置的固定凭据材料。</para>
/// </summary>
/// <remarks>
/// The ZIP wrapper around OFP vendor firmware packages is sealed with a fixed well-known password, so such
/// packages can be opened without user-provided keys.
/// <para>OFP 厂商固件包外层的 ZIP 包裹使用固定的公开口令封装，因此此类包无需用户提供密钥即可打开。</para>
/// </remarks>
internal static class OppoFirmwareCredentials
{
    /// <summary>
    /// Fixed password sealing the ZIP wrapper around OFP vendor firmware packages.
    /// <para>封装 OFP 厂商固件包外层 ZIP 的固定口令。</para>
    /// </summary>
    internal const string ZipPassword = "flash@realme$50E7F7D847732396F1582CD62DD385ED7ABB0897";
}