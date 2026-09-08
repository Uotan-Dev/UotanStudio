using Avalonia.Controls;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>文件文档视图，承载文本编辑器与二进制 16 进制查看器。</para>
/// File document view, hosting the text editor and the binary hex viewer.
/// </summary>
public partial class FileView : UserControl
{
    private FileViewModel? _editorOwner;
    /// <summary>
    /// <para>构造文件文档视图并初始化组件。</para>
    /// Creates the file document view and initializes its components.
    /// </summary>
    public FileView()
    {
        InitializeComponent();

        // 16 进制查看器的字节编辑须反向标记文档为已修改（脏状态）。
        // Byte edits in the hex viewer must mark the document dirty in the opposite direction.
        if (this.FindControl<HexViewer>("HexEditor") is { } hex)
        {
            hex.BytesEdited += OnHexBytesEdited;
        }

        Loaded += (_, _) => UpdateEditorOwner();
        Unloaded += (_, _) => UpdateEditorOwner(detach: true);
        DataContextChanged += (_, _) => UpdateEditorOwner();
    }

    private void UpdateEditorOwner(bool detach = false)
    {
        var editor = this.FindControl<AvaloniaEdit.TextEditor>("Editor");
        if (_editorOwner is { } previous && ReferenceEquals(previous.Editor, editor)) previous.Editor = null;
        _editorOwner = !detach && IsLoaded ? DataContext as FileViewModel : null;
        if (_editorOwner is { } current) current.Editor = editor;
    }

    private void OnHexBytesEdited(object? sender, System.EventArgs e)
    {
        if (DataContext is FileViewModel vm)
        {
            vm.MarkModifiedByHexEdit();
        }
    }
}