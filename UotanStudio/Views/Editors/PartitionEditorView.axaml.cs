using Avalonia.Controls;
using Avalonia.Input;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// Partition-table editor view: a GNOME Disks style visual partition band on top (clicking a segment
/// selects the partition), a left partition list plus a right property-editing pane
/// (name/start/size/type) below.
/// <para>分区表编辑器视图：顶部 GNOME Disks 风格可视化分区段布局（点击段联动选中），
/// 下方左栏分区列表 + 右栏属性编辑（分区名/起始/大小/类型）。</para>
/// </summary>
public partial class PartitionEditorView : UserControl
{
    /// <summary>
    /// Creates the partition-table editor view and initializes its components.
    /// <para>创建分区表编辑器视图并初始化组件。</para>
    /// </summary>
    public PartitionEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shared by band segments and the label row: clicking an owned segment selects its partition.
    /// <para>条带段与标签行共用：点击有主段时联动选中对应分区。</para>
    /// </summary>
    private void OnSegmentPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PartitionSegmentViewModel { Item: not null } segment }
            && DataContext is PartitionEditorViewModel viewModel)
        {
            viewModel.SelectedPartition = segment.Item;
        }
    }
}