using System.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>A stable persisted option with a separately localized display label.<para>稳定持久化的选项，带有独立本地化的显示标签。</para></summary>
/// <param name="value">The nonlocalized value to persist.<para>要持久化的非本地化值。</para></param>
/// <param name="resourcePrefix">Resource key prefix used to look up the display label.<para>用于查找显示标签的资源键前缀。</para></param>
public sealed class NodePortChoice(string value, string resourcePrefix = "Node_Choice_") : INotifyPropertyChanged
{
    /// <summary>Nonlocalized value stored in workflow snapshots.<para>存入工作流快照的非本地化值。</para></summary>
    /// <value>The stable persisted option value.<para>稳定持久化的选项值。</para></value>
    public string Value { get; } = value;

    /// <summary>Localized option label.<para>本地化的选项标签。</para></summary>
    /// <value>The display label resolved from the current language resources.<para>按当前语言资源解析出的显示标签。</para></value>
    public string DisplayName => LanguageService.Instance.GetString(resourcePrefix + Value.Replace('-', '_'));

    /// <summary>Raised when a property value changes.<para>属性值发生变化时触发。</para></summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
}