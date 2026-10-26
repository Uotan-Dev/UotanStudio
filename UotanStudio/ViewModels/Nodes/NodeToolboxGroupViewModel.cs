using System.Collections.ObjectModel;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// A categorized group of operations in the node editor toolbox, presented inside an expander control.
/// <para>节点编辑器工具箱中的分类操作组，呈现在折叠展开（Expander）控件中。</para>
/// </summary>
public sealed class NodeToolboxGroupViewModel : ViewModelBase
{
    private bool _isExpanded;
    private bool _isVisible = true;

    /// <summary>
    /// Gets the unique identifier of this toolbox group.
    /// <para>获取该工具箱分组的唯一标识符。</para>
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the localized title displayed on the group expander header.
    /// <para>获取分组折叠头部显示的本地化标题。</para>
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the collection of toolbox items within this group.
    /// <para>获取此分组下的工具箱节点项集合。</para>
    /// </summary>
    public ObservableCollection<NodeToolboxItem> Items { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether this group is expanded.
    /// <para>获取或设置此分组是否展开。</para>
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether this group has matching items and should be visible.
    /// <para>获取或设置此分组是否包含匹配项且应当可见。</para>
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NodeToolboxGroupViewModel"/> class.
    /// <para>初始化 <see cref="NodeToolboxGroupViewModel"/> 类的新实例。</para>
    /// </summary>
    /// <param name="key">Group key.<para>分组键。</para></param>
    /// <param name="title">Localized title.<para>本地化标题。</para></param>
    public NodeToolboxGroupViewModel(string key, string title)
    {
        Key = key;
        Title = title;
    }
}