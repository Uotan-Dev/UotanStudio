using Avalonia.Controls;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>ARSC（resources.arsc）资源表编辑器视图：左栏资源树 + 右栏选中节点详情。</para>
/// ARSC (resources.arsc) resource-table editor view: a left resource tree plus a right details pane.
/// </summary>
public partial class ArscEditorView : UserControl
{
    /// <summary>
    /// <para>创建 ARSC 编辑器视图并初始化组件。</para>
    /// Creates the ARSC editor view and initializes its components.
    /// </summary>
    public ArscEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <para>左栏资源树选中变更：同步 ViewModel 选中项，驱动右栏详情面板。</para>
    /// Left tree selection changed: syncs the ViewModel selection, driving the right details pane.
    /// </summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ArscEditorViewModel vm)
        {
            vm.SelectedNode = ResourceTree.SelectedItem as ArscTreeNodeViewModel;
        }
    }
}
