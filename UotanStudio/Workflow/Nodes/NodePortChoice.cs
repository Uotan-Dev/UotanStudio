using System.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.Workflow.Nodes;

/// <summary>A stable persisted option with a separately localized display label.<para>稳定持久化的选项，带有独立本地化的显示标签。</para></summary>
/// <param name="value">The nonlocalized value to persist.<para>要持久化的非本地化值。</para></param>
/// <param name="resourcePrefix">Resource key prefix used to look up the display label when no explicit label is given.<para>未提供显式标签时用于查找显示标签的资源键前缀。</para></param>
/// <param name="displayLabel">Optional explicit label, such as a resource file name, that takes precedence over the localized resource.<para>可选的显式标签（例如资源文件名），优先于本地化资源。</para></param>
public sealed class NodePortChoice(string value, string resourcePrefix = "Node_Choice_", string? displayLabel = null) : INotifyPropertyChanged
{
    /// <summary>Nonlocalized value stored in workflow snapshots.<para>存入工作流快照的非本地化值。</para></summary>
    /// <value>The stable persisted option value.<para>稳定持久化的选项值。</para></value>
    public string Value { get; } = value;

    /// <summary>Option label, preferring the explicit display label over the localized resource.<para>选项标签：优先使用显式显示标签，其次使用本地化资源。</para></summary>
    /// <value>The explicit label when one was supplied; otherwise the display label resolved from the current language resources.<para>提供显式标签时为该标签；否则为按当前语言资源解析出的显示标签。</para></value>
    public string DisplayName => string.IsNullOrWhiteSpace(displayLabel)
        ? LanguageService.Instance.GetString(resourcePrefix + Value.Replace('-', '_'))
        : displayLabel;

    /// <summary>Raised when a property value changes.<para>属性值发生变化时触发。</para></summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
}