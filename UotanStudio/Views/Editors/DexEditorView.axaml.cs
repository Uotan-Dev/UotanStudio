using Avalonia.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>DEX 编辑器视图：右栏全量反汇编（Smali）文本 + 保存（重建）工具栏。
/// 已移除左栏结构树——大 DEX 的数万树节点与 TreeView 控件是主要内存开销。</para>
/// DEX editor view: full disassembly (Smali) text pane plus a save/rebuild toolbar.
/// The left structure tree has been removed — tens of thousands of tree nodes and TreeView
/// controls were the dominant memory cost when opening a large DEX.
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
}
