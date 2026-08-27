using UotanStudio.ViewModels.Editors;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>DEX 编辑器视图（仿设备树面板）：左栏类/方法/字段结构树 + 右栏只读反汇编文本。</para>
/// DEX editor view (modeled on the device-tree panel): a left class/method/field structure tree plus
/// a right read-only disassembly text pane.
/// </summary>
public partial class DexEditorView : UserControl
{
    /// <summary>
    /// <para>创建 DEX 编辑器视图并初始化组件。</para>
    /// Creates the DEX editor view and initializes its components.
    /// </summary>
    public DexEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <para>左栏结构树选中变更：同步 ViewModel 选中项并向属性面板广播节点选中消息。</para>
    /// Left tree selection changed: syncs the ViewModel selection and broadcasts a node-selection
    /// message to the properties panel.
    /// </summary>
    private void OnClassTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not DexEditorViewModel vm)
        {
            return;
        }

        var node = ClassTree.SelectedItem as DexTreeNodeViewModel;
        vm.SelectedNode = node;
        WeakReferenceMessenger.Default.Send(new DexNodeSelectionChangedMessage(vm, node));
    }
}