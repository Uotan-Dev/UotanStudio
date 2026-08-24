namespace UotanStudio.Firmware.Oppo.Models;

/// <summary>
/// <para>包含 Oppo 固件文件的元信息。</para>
/// Contains metadata of an Oppo firmware file.
/// </summary>
public sealed class OppMetadata
{
    /// <summary>
    /// <para>格式名称（如 "OFP QC"、"OFP MTK"、"OPS"、"OZip"）。</para>
    /// Format name (e.g., "OFP QC", "OFP MTK", "OPS", "OZip").
    /// </summary>
    public string FormatName { get; init; } = string.Empty;

    /// <summary>
    /// <para>格式类型。</para>
    /// The format type.
    /// </summary>
    public OppFormat Format { get; init; }

    /// <summary>
    /// <para>格式版本（若头部包含版本字段）。</para>
    /// Format version (if the header contains a version field).
    /// </summary>
    public Version? FormatVersion { get; init; }

    /// <summary>
    /// <para>文件大小（字节）。</para>
    /// File size in bytes.
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// <para>条目数量。</para>
    /// Number of entries.
    /// </summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// <para>项目名称（如 "OP4867"、"OP5A05D1"）。</para>
    /// Project name (e.g., "OP4867", "OP5A05D1").
    /// </summary>
    public string? ProjectName { get; init; }

    /// <summary>
    /// <para>CPU 型号（如 "sdm660"、"mt6771"）。</para>
    /// CPU model (e.g., "sdm660", "mt6771").
    /// </summary>
    public string? CpuModel { get; init; }

    /// <summary>
    /// <para>闪存类型（如 "UFS"、"EMMC"）。</para>
    /// Flash type (e.g., "UFS", "EMMC").
    /// </summary>
    public string? FlashType { get; init; }

    /// <summary>
    /// <para>固件版本名称。</para>
    /// Firmware version name.
    /// </summary>
    public string? FirmwareName { get; init; }

    /// <summary>
    /// <para>项目 ID（OPS 格式）。</para>
    /// Project ID (OPS format).
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// <para>是否为 ZIP 包裹的 OFP 文件。</para>
    /// Whether the file is a ZIP-wrapped OFP file.
    /// </summary>
    public bool IsZipWrapped { get; init; }

    /// <summary>
    /// <para>使用的密钥标识（解密后填充）。</para>
    /// Key identifier used (populated after decryption).
    /// </summary>
    public string? KeyId { get; init; }
}