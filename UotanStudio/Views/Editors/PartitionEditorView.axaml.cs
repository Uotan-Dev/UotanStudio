using Avalonia.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>分区表编辑器视图：左栏分区列表 + 右栏属性编辑（分区名/起始/大小/类型）。</para>
/// Partition-table editor view: a left partition list plus a right property-editing pane
/// (name/start/size/type).
/// </summary>
public partial class PartitionEditorView : UserControl
{
    /// <summary>
    /// <para>创建分区表编辑器视图并初始化组件。</para>
    /// Creates the partition-table editor view and initializes its components.
    /// </summary>
    public PartitionEditorView()
    {
        InitializeComponent();
    }
}
