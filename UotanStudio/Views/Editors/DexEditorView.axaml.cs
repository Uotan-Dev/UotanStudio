using Avalonia.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>DEX 编辑器视图：全量反汇编（Smali）文本、文件路径与解析错误状态栏。
/// 无左栏结构树——大 DEX 的数万树节点与 TreeView 控件是主要内存开销。</para>
/// DEX editor view: full disassembly (Smali) text pane with file-path and parse-error status.
/// No left structure tree — tens of thousands of tree nodes and TreeView controls are the
/// dominant memory cost when opening a large DEX.
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