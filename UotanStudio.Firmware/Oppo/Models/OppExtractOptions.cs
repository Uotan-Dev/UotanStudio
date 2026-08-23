namespace UotanStudio.Firmware.Oppo.Models;

/// <summary>
/// <para>提供 Oppo 固件提取操作的配置选项。</para>
/// Provides configuration options for Oppo firmware extraction operations.
/// </summary>
public sealed class OppExtractOptions
{
    /// <summary>
    /// <para>是否覆盖已存在的文件。</para>
    /// Whether to overwrite existing files.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// <para>解密时使用的密钥标识，为 null 时使用自动密钥探测。</para>
    /// Key identifier for decryption, null for auto key detection.
    /// </summary>
    public string? KeyId { get; init; }
}