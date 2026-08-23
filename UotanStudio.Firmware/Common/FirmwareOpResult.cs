namespace UotanStudio.Firmware.Common;

/// <summary>
/// Firmware operation result, carrying success flag and output summary.
/// <para>固件操作结果,承载成功标志与输出摘要。</para>
/// </summary>
public sealed class FirmwareOpResult
{
    /// <summary>
    /// Gets whether the operation succeeded.
    /// <para>获取操作是否成功。</para>
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the operation summary.
    /// <para>获取操作摘要。</para>
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the list of entry names / partition names.
    /// <para>获取条目名称列表。</para>
    /// </summary>
    public IReadOnlyList<string> Entries { get; init; } = [];

    /// <summary>
    /// Gets additional structured metadata (format-specific).
    /// <para>获取附加结构化元数据(格式相关)。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Creates a success result.
    /// <para>创建成功结果。</para>
    /// </summary>
    public static FirmwareOpResult Ok(string summary, IReadOnlyList<string>? entries = null, IReadOnlyDictionary<string, string>? metadata = null)
        => new() { Success = true, Summary = summary, Entries = entries ?? [], Metadata = metadata ?? new Dictionary<string, string>() };

    /// <summary>
    /// Creates a failure result.
    /// <para>创建失败结果。</para>
    /// </summary>
    public static FirmwareOpResult Fail(string summary)
        => new() { Success = false, Summary = summary };
}