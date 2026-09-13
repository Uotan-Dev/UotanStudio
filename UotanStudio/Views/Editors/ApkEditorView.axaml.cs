using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Shell;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>APK 元数据编辑器视图：主视图展示应用图标、包名、版本、SDK、ABI 与权限等详细信息。</para>
/// APK metadata editor view: the main view shows the app icon, package name, version, SDK, ABIs
/// and permission details.
/// </summary>
public partial class ApkEditorView : UserControl
{
    /// <summary>
    /// <para>创建 APK 元数据编辑器视图并初始化组件。</para>
    /// Creates the APK metadata editor view and initializes its components.
    /// </summary>
    public ApkEditorView()
    {
        InitializeComponent();
    }

    private async void OnSignClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApkEditorViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var window = new UotanStudio.Views.Shell.ApkSigningWindow(vm);
        await window.ShowDialog(owner);
    }

}
