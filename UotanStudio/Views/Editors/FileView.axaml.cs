using Avalonia;
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

    private FileViewModel? _hexSearchDocument;

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

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => UpdateEditorOwner();
        Unloaded += (_, _) => UpdateEditorOwner(detach: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        BindHexSearchDocument(null);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UpdateEditorOwner();
        BindHexSearchDocument(DataContext as FileViewModel);
    }

    private void BindHexSearchDocument(FileViewModel? document)
    {
        if (ReferenceEquals(_hexSearchDocument, document))
        {
            return;
        }

        if (_hexSearchDocument is not null)
        {
            _hexSearchDocument.HexSearchMatchLocated -= OnHexSearchMatchLocated;
        }

        _hexSearchDocument = document;
        if (_hexSearchDocument is not null)
        {
            _hexSearchDocument.HexSearchMatchLocated += OnHexSearchMatchLocated;
        }
    }

    private void OnHexSearchMatchLocated(int offset, int length)
    {
        if (this.FindControl<HexViewer>("HexEditor") is { } viewer)
        {
            viewer.RevealMatch(offset, length);
        }
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