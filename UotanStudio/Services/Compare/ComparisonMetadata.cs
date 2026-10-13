using UotanStudio.Firmware.Core;

namespace UotanStudio.Services.Compare;

/// <summary>
/// One file's metadata captured for comparison: format name, handler summary, key/value metadata,
/// named entries and components.
/// <para>为比较而采集的单个文件元数据：格式名、处理器摘要、键值元数据、具名条目与部件。</para>
/// </summary>
/// <param name="FormatName">Detected format name; empty for unrecognized files. <para>检测到的格式名；无法识别的文件为空。</para></param>
/// <param name="Summary">The format handler's summary. <para>格式处理器给出的摘要。</para></param>
/// <param name="Metadata">The format handler's metadata values. <para>格式处理器报告的键值元数据。</para></param>
/// <param name="Entries">Named entries reported by the handler. <para>处理器报告的具名条目。</para></param>
/// <param name="Components">Components reported by the handler. <para>处理器报告的部件清单。</para></param>
internal sealed record ComparisonMetadata(
    string FormatName,
    string Summary,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<string> Entries,
    IReadOnlyList<FirmwareComponent> Components)
{
    /// <summary>
    /// Placeholder for an unknown format or a handler-reported read failure.
    /// <para>用于未知格式或处理器上报读取失败的占位值。</para>
    /// </summary>
    /// <value>An instance whose text fields and collections are all empty. <para>文本字段与集合均为空的实例。</para></value>
    internal static ComparisonMetadata Unrecognized { get; } =
        new(string.Empty, string.Empty, new Dictionary<string, string>(), [], []);
}