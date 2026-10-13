using AvaloniaEdit;

namespace UotanStudio.Views.Controls;

/// <summary>Releases only the TextEditor reference owned by this control when a model is replaced or detached.<para>模型被替换或分离时，只释放此控件拥有的 TextEditor 引用。</para></summary>
/// <param name="getEditor">Reads the TextEditor currently attached to a model.<para>读取当前附加到模型的 TextEditor。</para></param>
/// <param name="setEditor">Writes the TextEditor attached to a model.<para>写入附加到模型的 TextEditor。</para></param>
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