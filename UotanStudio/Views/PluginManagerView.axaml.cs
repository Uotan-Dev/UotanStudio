using Avalonia.Controls;
using UotanStudio.ViewModels.Plugins;

namespace UotanStudio.Views;

/// <summary>
/// <para>插件管理器视图，展示已加载插件及其节点定义，支持启用/禁用。</para>
/// Plugin manager view, showing loaded plugins and their node definitions, with enable/disable support.
/// </summary>
public partial class PluginManagerView : UserControl
{
    /// <summary>
    /// <para>构造插件管理器视图。</para>
    /// Constructs the plugin manager view.
    /// </summary>
    public PluginManagerView()
    {
        InitializeComponent();
    }
}
