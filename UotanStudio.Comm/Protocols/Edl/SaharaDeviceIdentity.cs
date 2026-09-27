namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Reports the legacy Sahara command-mode identity without claiming cryptographic device authentication.
/// <para>报告传统 Sahara 命令模式身份，不宣称完成设备密码学认证。</para>
/// </summary>
public sealed class SaharaDeviceIdentity
{
    internal SaharaDeviceIdentity(ulong serialNumber, ulong hardwareId, ulong sblVersion, string publicKeyHash)
    {
        SerialNumber = serialNumber;
        HardwareId = hardwareId;
        SblVersion = sblVersion;
        OemPublicKeyHashHex = publicKeyHash;
    }

    /// <summary>Gets the device-reported serial number.
    /// <para>获取设备报告的序列号。</para></summary>
    /// <value>The 32-bit or 64-bit little-endian serial value.
    /// <para>32 位或 64 位小端序列号值。</para></value>
    public ulong SerialNumber { get; }

    /// <summary>Gets the device-reported legacy hardware identifier.
    /// <para>获取设备报告的传统硬件标识符。</para></summary>
    /// <value>The complete 32-bit or 64-bit identifier without vendor inference.
    /// <para>完整的 32 位或 64 位标识符，不进行厂商推断。</para></value>
    public ulong HardwareId { get; }

    /// <summary>Gets the device-reported SBL version.
    /// <para>获取设备报告的 SBL 版本。</para></summary>
    /// <value>The 32-bit or 64-bit little-endian version.
    /// <para>32 位或 64 位小端版本值。</para></value>
    public ulong SblVersion { get; }

    /// <summary>Gets the exact OEM public-key-hash reply in hexadecimal, including device padding.
    /// <para>获取十六进制表示的精确 OEM 公钥哈希回复，包含设备填充。</para></summary>
    /// <value>The bounded raw response, not a guessed hash algorithm or trusted certificate.
    /// <para>有界的原始响应，并非推断的哈希算法或可信证书。</para></value>
    public string OemPublicKeyHashHex { get; }
}