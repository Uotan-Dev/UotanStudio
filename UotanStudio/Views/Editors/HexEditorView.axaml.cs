using Avalonia.Controls;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// Hex editor view: binary hex viewing/editing on the custom-rendered <c>HexViewer</c> control.
/// The view only wires events (edits → ViewModel dirty flag); data flows through <c>RawBytes</c>.
/// <para>Hex 编辑器视图：基于自绘制 <c>HexViewer</c> 控件的二进制十六进制查看/编辑。
/// 视图层仅做事件接线（编辑 → ViewModel 脏标记）；数据流经 <c>RawBytes</c> 双向承载。</para>
/// </summary>
public partial class HexEditorView : UserControl
{
    /// <summary>
    /// Creates the Hex editor view and initializes its components.
    /// <para>创建 Hex 编辑器视图并初始化组件。</para>
    /// </summary>
    public HexEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// HexViewer byte-edit event: marks the document dirty.
    /// <para>HexViewer 字节编辑事件：把文档标记为已修改。</para>
    /// </summary>
    private void OnBytesEdited(object? sender, EventArgs e)
    {
        if (DataContext is HexEditorViewModel vm)
        {
            vm.IsDirty = true;
        }
    }
}