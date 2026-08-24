namespace UotanStudio.Firmware.Oppo.Models;

/// <summary>
/// <para>包含 Oppo 固件提取操作的结果。</para>
/// Contains the result of an Oppo firmware extraction operation.
/// </summary>
public sealed class OppExtractResult
{
    /// <summary>
    /// <para>提取的文件总数。</para>
    /// Total number of extracted files.
    /// </summary>
    public int ExtractedFileCount { get; init; }

    /// <summary>
    /// <para>输出目录路径。</para>
    /// Output directory path.
    /// </summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>
    /// <para>使用的密钥标识。</para>
    /// Key identifier used.
    /// </summary>
    public string? KeyId { get; init; }
}