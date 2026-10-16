using Avalonia.Controls;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// File document view, hosting the text editor and the binary hex viewer.
/// <para>文件文档视图，承载文本编辑器与二进制 16 进制查看器。</para>
/// </summary>
public partial class FileView : UserControl
{
    private readonly EditorOwnerBinding<FileViewModel> _editorOwner =
        new(static model => model.Editor, static (model, editor) => model.Editor = editor);

    /// <summary>
    /// Creates the file document view and initializes its components.
    /// <para>构造文件文档视图并初始化组件。</para>
    /// </summary>
    public FileView()
    {
        InitializeComponent();

        if (this.FindControl<HexViewer>("HexEditor") is { } hex)
        {
            hex.BytesEdited += OnHexBytesEdited;
        }

        Loaded += (_, _) => UpdateEditorOwner();
        Unloaded += (_, _) => UpdateEditorOwner(detach: true);
        DataContextChanged += (_, _) => UpdateEditorOwner();
    }

    private void UpdateEditorOwner(bool detach = false)
        => _editorOwner.Update(!detach && IsLoaded ? DataContext as FileViewModel : null, this.FindControl<AvaloniaEdit.TextEditor>("Editor"));

    private void OnHexBytesEdited(object? sender, EventArgs e)
    {
        if (DataContext is FileViewModel vm)
        {
            vm.MarkModifiedByHexEdit();
        }
    }
}