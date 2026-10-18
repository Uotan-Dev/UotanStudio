namespace UotanStudio.Firmware.Core;

/// <summary>
/// Uniform outcome of a firmware operation: a success flag, a human-readable summary, list entries,
/// free-form metadata and the components involved.
/// <para>固件操作的统一结果：成功标记、人类可读摘要、列表条目、自由格式元数据以及所涉及的组件。</para>
/// </summary>
public sealed class FirmwareOpResult
{
    /// <summary>Whether the operation completed successfully.
    /// <para>操作是否成功完成。</para></summary>
    /// <value><see langword="true"/> when the operation reported success, <see langword="false"/> for a
    /// failure. <para>操作报告成功时为 <see langword="true"/>，失败时为 <see langword="false"/>。</para></value>
    public bool Success { get; init; }

    /// <summary>One-line summary shown to the user; empty for a result built without one.
    /// <para>展示给用户的一行摘要；构建时未提供则为空。</para></summary>
    /// <value>The summary text, never <see langword="null"/>.
    /// <para>摘要文本，绝不为 <see langword="null"/>。</para></value>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Detail lines listed under the summary, for example per-file or per-partition rows. Empty when none.
    /// <para>摘要下方列出的明细行，例如按文件或按分区的记录；无记录时为空。</para></summary>
    /// <value>A read-only list that is never <see langword="null"/>.
    /// <para>只读列表，绝不为 <see langword="null"/>。</para></value>
    public IReadOnlyList<string> Entries { get; init; } = [];

    /// <summary>Additional key/value facts about the operation. Empty when none, never <see langword="null"/>.
    /// <para>关于该操作的额外键值信息；无内容时为空，绝不为 <see langword="null"/>。</para></summary>
    /// <value>A read-only dictionary keyed by fact name. <para>以信息名为键的只读字典。</para></value>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Components produced or touched by the operation. Empty when the operation reports none.
    /// <para>操作产生或涉及的组件；未上报时为空。</para></summary>
    /// <value>A read-only list of components that is never <see langword="null"/>.
    /// <para>组件只读列表，绝不为 <see langword="null"/>。</para></value>
    public IReadOnlyList<FirmwareComponent> Components { get; init; } = [];

    /// <summary>Creates a success result.<para>创建成功结果。</para></summary>
    /// <param name="summary">One-line summary of what succeeded.<para>对成功内容的一行摘要。</para></param>
    /// <param name="entries">Detail lines; <see langword="null"/> yields an empty list.
    /// <para>明细行；传 <see langword="null"/> 时得到空列表。</para></param>
    /// <param name="metadata">Key/value facts; <see langword="null"/> yields an empty dictionary.
    /// <para>键值信息；传 <see langword="null"/> 时得到空字典。</para></param>
    /// <param name="components">Involved components; <see langword="null"/> yields an empty list.
    /// <para>所涉及的组件；传 <see langword="null"/> 时得到空列表。</para></param>
    /// <returns>A result with <see cref="Success"/> set.
    /// <para><see cref="Success"/> 为真的结果。</para></returns>
    public static FirmwareOpResult Ok(string summary, IReadOnlyList<string>? entries = null, IReadOnlyDictionary<string, string>? metadata = null, IReadOnlyList<FirmwareComponent>? components = null)
        => new()
        {
            Success = true,
            Summary = summary,
            Entries = entries ?? [],
            Metadata = metadata ?? new Dictionary<string, string>(),
            Components = components ?? []
        };

    /// <summary>
    /// Creates a failure result.
    /// <para>创建失败结果。</para>
    /// </summary>
    /// <param name="summary">
    /// Human-readable failure description; entries, metadata and components stay empty.
    /// <para>人类可读的失败说明；明细行、元数据与组件保持为空。</para>
    /// </param>
    /// <returns>A result with <see cref="Success"/> cleared. <para><see cref="Success"/> 为假的结果。</para></returns>
    public static FirmwareOpResult Fail(string summary)
        => new() { Success = false, Summary = summary };
}