using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
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
    /// <para>左栏资源树选中变更：同步 ViewModel 选中项，并广播节点选中消息
    /// （供属性面板展示选中包/类型/条目的详情）。</para>
    /// Left tree selection changed: syncs the ViewModel selection and broadcasts a node-selection
    /// message so the properties panel can show the selected package/type/entry details.
    /// </summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ArscEditorViewModel vm)
        {
            vm.SelectedNode = ResourceTree.SelectedItem as ArscTreeNodeViewModel;
            WeakReferenceMessenger.Default.Send(new ArscNodeSelectionChangedMessage(vm, vm.SelectedNode));
        }
    }
}