namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Reports the Sahara command-mode identity without claiming cryptographic device authentication.
/// <para>报告 Sahara 命令模式身份，不宣称完成设备密码学认证。</para>
/// </summary>
public sealed class SaharaDeviceIdentity
{
    internal SaharaDeviceIdentity(ulong serialNumber, ulong hardwareId, ulong sblVersion, string publicKeyHash,
        uint? protocolVersion = null, uint chipIdentifierV3 = 0)
    {
        SerialNumber = serialNumber;
        HardwareId = hardwareId;
        SblVersion = sblVersion;
        OemPublicKeyHashHex = publicKeyHash;
        ProtocolVersion = protocolVersion;
        ChipIdentifierV3 = chipIdentifierV3;
    }

    /// <summary>Gets the device-reported serial number.
    /// <para>获取设备报告的序列号。</para></summary>
    /// <value>The 32-bit or 64-bit little-endian serial value.
    /// <para>32 位或 64 位小端序列号值。</para></value>
    public ulong SerialNumber { get; }

    /// <summary>Gets the device-reported hardware identifier.
    /// <para>获取设备报告的硬件标识符。</para></summary>
    /// <value>The complete 32-bit or 64-bit identifier without vendor inference. On protocol
    /// version 3 targets this is composed from the version 3 chip identifier.
    /// <para>完整的 32 位或 64 位标识符，不进行厂商推断。协议版本 3 目标上由版本 3 芯片标识组合而成。</para></value>
    public ulong HardwareId { get; }

    /// <summary>Gets the device-reported SBL version.
    /// <para>获取设备报告的 SBL 版本。</para></summary>
    /// <value>The 32-bit or 64-bit little-endian version, or zero when the target refused the read.
    /// <para>32 位或 64 位小端版本值；目标拒绝读取时为零。</para></value>
    public ulong SblVersion { get; }

    /// <summary>Gets the exact OEM public-key-hash reply in hexadecimal, including device padding.
    /// <para>获取十六进制表示的精确 OEM 公钥哈希回复，包含设备填充。</para></summary>
    /// <value>The bounded raw response, not a guessed hash algorithm or trusted certificate; empty
    /// when the target refused the read.
    /// <para>有界的原始响应，并非推断的哈希算法或可信证书；目标拒绝读取时为空。</para></value>
    public string OemPublicKeyHashHex { get; }

    /// <summary>Gets the protocol version acknowledged during the identity exchange, when known.
    /// <para>获取身份交换期间确认的协议版本（若已知）。</para></summary>
    /// <value>The negotiated version, or <see langword="null"/> when the identity was not captured.
    /// <para>协商版本；未捕获身份时为 <see langword="null"/>。</para></value>
    public uint? ProtocolVersion { get; }

    /// <summary>Gets the raw 32-bit version 3 chip identifier reported by protocol version 3 targets.
    /// <para>获取协议版本 3 目标报告的原始 32 位版本 3 芯片标识。</para></summary>
    /// <value>The chip identifier, or zero on pre-version 3 targets.
    /// <para>芯片标识；版本 3 之前的目标上为零。</para></value>
    public uint ChipIdentifierV3 { get; }
}