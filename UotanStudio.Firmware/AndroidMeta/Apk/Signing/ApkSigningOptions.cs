namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Options for signing an APK with user-provided key material.
/// <para>使用用户提供的密钥材料签署 APK 的选项。</para>
/// </summary>
public sealed class ApkSigningOptions
{
    /// <summary>Enables JAR (v1) signature artifacts (META-INF MANIFEST.MF/.SF/CMS).</summary>
    /// <para>启用 JAR（v1）签名工件（META-INF 的 MANIFEST.MF/.SF/CMS）。</para>
    /// <value><see langword="true"/> (default) appends META-INF signature artifacts.
    /// <para>默认 <see langword="true"/>，追加 META-INF 签名工件。</para></value>
    public bool EnableV1 { get; set; } = true;

    /// <summary>Enables APK Signature Scheme v2 (API 24+).</summary>
    /// <para>启用 APK 签名方案 v2（API 24+）。</para>
    /// <value><see langword="true"/> (default) emits the v2 signer under block ID 0x7109871a.
    /// <para>默认 <see langword="true"/>，在分块 ID 0x7109871a 下写入 v2 签名者。</para></value>
    public bool EnableV2 { get; set; } = true;

    /// <summary>Enables APK Signature Scheme v3 (API 28+).</summary>
    /// <para>启用 APK 签名方案 v3（API 28+）。</para>
    /// <value><see langword="true"/> (default) emits the v3 signer under block ID 0xf05368c0.
    /// <para>默认 <see langword="true"/>，在分块 ID 0xf05368c0 下写入 v3 签名者。</para></value>
    public bool EnableV3 { get; set; } = true;

    /// <summary>
    /// Assumed minimum SDK passed to algorithm selection. RSA/EC choices follow apksig key-size defaults.
    /// <para>传给算法选择器的假定最低 SDK；RSA/EC 的算法按 apksig 密钥位数默认策略选择。</para>
    /// </summary>
    /// <value>Defaults to 24; forwarded to the algorithm selector, which keys off key size.
    /// <para>默认 24；传给算法选择器，但选择器实际按密钥位数决策。</para></value>
    public int MinSdkVersion { get; set; } = 24;

    /// <summary>Minimum SDK protected by the v3 signer.</summary>
    /// <para>v3 签名者保护的最低 SDK。</para>
    /// <value>Defaults to 28; signing rejects values below 28. <para>默认 28；低于 28 时签名入口直接拒绝。</para></value>
    public int V3MinSdk { get; set; } = ApkV2V3SchemeSigner.V3DefaultMinSdk;

    /// <summary>Maximum SDK protected by the v3 signer.</summary>
    /// <para>v3 签名者保护的最高 SDK。</para>
    /// <value>Defaults to <see cref="int.MaxValue"/>; must not be below <see cref="V3MinSdk"/>.
    /// <para>默认 <see cref="int.MaxValue"/>；不得小于 <see cref="V3MinSdk"/>。</para></value>
    public int V3MaxSdk { get; set; } = ApkV2V3SchemeSigner.V3DefaultMaxSdk;

    /// <summary>
    /// Base name of the v1 signature files inside META-INF (MANIFEST.MF is fixed).
    /// <para>META-INF 内 v1 签名文件的基础名（MANIFEST.MF 固定不变）。</para>
    /// </summary>
    /// <value>Defaults to <see cref="ApkV1Signer.DefaultSignerName"/>; when v1 is enabled it must be
    /// 1-8 ASCII letters, digits, <c>-</c> or <c>_</c>. <para>默认
    /// <see cref="ApkV1Signer.DefaultSignerName"/>；启用 v1 时必须是 1-8 位 ASCII 字母、数字、<c>-</c> 或 <c>_</c>。</para></value>
    public string SignerName { get; set; } = ApkV1Signer.DefaultSignerName;

    /// <summary>
    /// Strips stale signature artifacts (META-INF signature entries and an existing APK Signing
    /// Block) from the input before signing, which is required when re-signing.
    /// <para>签名前剥离输入中的旧签名工件（META-INF 签名条目与既有 APK 签名分块），重新签名时必需。</para>
    /// </summary>
    /// <value><see langword="true"/> (default) rewrites the working copy without old signature entries
    /// before signing. <para>默认 <see langword="true"/>，签名前先重写工作副本以移除旧签名条目。</para></value>
    public bool StripExistingSignatures { get; set; } = true;
}