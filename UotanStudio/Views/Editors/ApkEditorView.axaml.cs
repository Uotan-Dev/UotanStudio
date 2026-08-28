using Avalonia.Controls;

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
}