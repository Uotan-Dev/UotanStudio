using Avalonia.Controls;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>Manifest（AXML）编辑器视图：左栏元素结构树 + 右栏属性编辑与 XML 文本预览。</para>
/// Manifest (AXML) editor view: a left element tree plus right attribute-editing and XML preview panes.
/// </summary>
public partial class ManifestEditorView : UserControl
{
    /// <summary>
    /// <para>创建 Manifest 编辑器视图并初始化组件。</para>
    /// Creates the Manifest editor view and initializes its components.
    /// </summary>
    public ManifestEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <para>左栏结构树选中变更：同步 ViewModel 选中项，驱动右栏属性面板。</para>
    /// Left tree selection changed: syncs the ViewModel selection, driving the right properties pane.
    /// </summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ManifestEditorViewModel vm)
        {
            vm.SelectedNode = ElementTree.SelectedItem as ManifestTreeNodeViewModel;
        }
    }
}
