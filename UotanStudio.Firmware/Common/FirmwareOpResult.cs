namespace UotanStudio.Firmware.Common;

/// <summary>
/// A structured component entry inside a firmware container (e.g. a payload partition or a
/// boot-image kernel/ramdisk), carrying its name and size for list display.
/// <para>固件容器内的结构化组件条目（如 payload 分区、boot 镜像的 kernel/ramdisk），
/// 携带名称与大小供列表展示。</para>
/// </summary>
public sealed class FirmwareComponent
{
    /// <summary>
    /// Gets the component name (e.g. "system" or "kernel").
    /// <para>获取组件名称（如 "system" 或 "kernel"）。</para>
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the component size in bytes; 0 when unknown.
    /// <para>获取组件字节大小；未知时为 0。</para>
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Creates a component entry.
    /// <para>创建组件条目。</para>
    /// </summary>
    public static FirmwareComponent Of(string name, long sizeBytes) => new() { Name = name, SizeBytes = sizeBytes };
}

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
    /// Gets the structured component list (name + size) when the format exposes it; empty otherwise.
    /// <para>获取结构化组件列表（名称 + 大小）；格式未提供时为空。</para>
    /// </summary>
    public IReadOnlyList<FirmwareComponent> Components { get; init; } = [];

    /// <summary>
    /// Creates a success result.
    /// <para>创建成功结果。</para>
    /// </summary>
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
    public static FirmwareOpResult Fail(string summary)
        => new() { Success = false, Summary = summary };
}