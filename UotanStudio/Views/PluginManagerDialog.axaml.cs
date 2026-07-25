using Avalonia.Controls;
using UotanStudio.ViewModels.Plugins;

namespace UotanStudio.Views;

/// <summary>
/// <para>插件管理器独立对话框窗口，承载 <see cref="PluginManagerView"/>。</para>
/// Standalone dialog window hosting <see cref="PluginManagerView"/>.
/// </summary>
public partial class PluginManagerDialog : Window
{
    /// <summary>
    /// <para>构造插件管理器对话框。</para>
    /// Constructs the plugin manager dialog.
    /// </summary>
    public PluginManagerDialog()
    {
        InitializeComponent();
        DataContext = new PluginManagerViewModel();
    }
}
