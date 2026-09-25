namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

/// <summary>
/// Summary of one Oppo extraction run.
/// <para>一次 Oppo 提取运行的结果概要。</para>
/// </summary>
public sealed class OppExtractResult
{
    /// <summary>
    /// Number of files actually written to disk during this run.
    /// <para>本次运行实际写入磁盘的文件数量。</para>
    /// </summary>
    /// <value>
    /// Entries skipped for zero size, for an existing output without overwrite, or for not being selected are not counted.
    /// <para>因大小为零、输出已存在且未允许覆盖，或未被选中而跳过的条目不计入。</para>
    /// </value>
    public int ExtractedFileCount { get; init; }

    /// <summary>
    /// Directory the files were extracted into.
    /// <para>文件提取到的目标目录。</para>
    /// </summary>
    /// <value>
    /// Exactly the directory requested by the caller.
    /// <para>与调用方传入的目录一致。</para>
    /// </value>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Identifier of the decryption key applied during extraction.
    /// <para>提取时使用的解密密钥标识。</para>
    /// </summary>
    /// <value>
    /// The requested key when set, otherwise the key recorded in archive metadata or auto-detected while decrypting; <see langword="null"/> when no key information is available.
    /// <para>调用方指定的密钥；未指定时为归档元数据记录或解密时自动检测到的密钥；无可用密钥信息时为 <see langword="null"/>。</para>
    /// </value>
    public string? KeyId { get; init; }
}