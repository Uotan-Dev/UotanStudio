using AvaloniaEdit;

namespace UotanStudio.Views.Controls;

/// <summary>Releases only the editor reference owned by this view when a model is replaced or detached.</summary>
internal sealed class EditorOwnerBinding<T>(Func<T, TextEditor?> getEditor, Action<T, TextEditor?> setEditor) where T : class
{
    private T? _owner;
    private TextEditor? _editor;

    internal void Update(T? owner, TextEditor? editor)
    {
        if (_owner is { } previous && ReferenceEquals(getEditor(previous), _editor)) setEditor(previous, null);
        _owner = owner;
        _editor = editor;
        if (owner is not null) setEditor(owner, editor);
    }
}