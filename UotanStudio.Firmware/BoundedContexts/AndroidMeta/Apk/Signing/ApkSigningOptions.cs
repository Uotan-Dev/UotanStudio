namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>
/// Options for signing an APK with user-provided key material.
/// <para>使用用户提供的密钥材料签署 APK 的选项。</para>
/// </summary>
public sealed class ApkSigningOptions
{
    /// <summary>Enables JAR (v1) signature artifacts (META-INF MANIFEST.MF/.SF/CMS).</summary>
    /// <para>启用 JAR（v1）签名工件（META-INF 的 MANIFEST.MF/.SF/CMS）。</para>
    public bool EnableV1 { get; set; } = true;

    /// <summary>Enables APK Signature Scheme v2 (API 24+).</summary>
    /// <para>启用 APK 签名方案 v2（API 24+）。</para>
    public bool EnableV2 { get; set; } = true;

    /// <summary>Enables APK Signature Scheme v3 (API 28+).</summary>
    /// <para>启用 APK 签名方案 v3（API 28+）。</para>
    public bool EnableV3 { get; set; } = true;

    /// <summary>
    /// Assumed minimum SDK passed to algorithm selection. RSA/EC choices follow apksig key-size defaults.
    /// <para>传给算法选择器的假定最低 SDK；RSA/EC 的算法按 apksig 密钥位数默认策略选择。</para>
    /// </summary>
    public int MinSdkVersion { get; set; } = 24;

    /// <summary>Minimum SDK protected by the v3 signer.</summary>
    /// <para>v3 签名者保护的最低 SDK。</para>
    public int V3MinSdk { get; set; } = ApkV2V3SchemeSigner.V3DefaultMinSdk;

    /// <summary>Maximum SDK protected by the v3 signer.</summary>
    /// <para>v3 签名者保护的最高 SDK。</para>
    public int V3MaxSdk { get; set; } = ApkV2V3SchemeSigner.V3DefaultMaxSdk;

    /// <summary>
    /// Base name of the v1 signature files inside META-INF (MANIFEST.MF is fixed).
    /// <para>META-INF 内 v1 签名文件的基础名（MANIFEST.MF 固定不变）。</para>
    /// </summary>
    public string SignerName { get; set; } = ApkV1Signer.DefaultSignerName;

    /// <summary>
    /// Strips stale signature artifacts (META-INF signature entries and an existing APK Signing
    /// Block) from the input before signing, which is required when re-signing.
    /// <para>签名前剥离输入中的旧签名工件（META-INF 签名条目与既有 APK 签名分块），重新签名时必需。</para>
    /// </summary>
    public bool StripExistingSignatures { get; set; } = true;
}
