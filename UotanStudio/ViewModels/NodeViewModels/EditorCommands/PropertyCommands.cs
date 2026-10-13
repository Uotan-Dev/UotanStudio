namespace UotanStudio.ViewModels.NodeViewModels.EditorCommands;

/// <summary>Undoable change to a single node or connector property.<para>对单个节点或连接器属性的可撤销修改。</para></summary>
public sealed class PropertyChangeCommand : IEditorCommand
{
    private readonly Action<object?> _setter;
    private readonly object? _originalValue;
    private readonly object? _newValue;

    public PropertyChangeCommand(string displayName, Action<object?> setter, object? originalValue, object? newValue)
    {
        DisplayName = displayName;
        _setter = setter;
        _originalValue = originalValue;
        _newValue = newValue;
    }

    public string DisplayName { get; }

    public void Execute() => _setter(_newValue);

    public void Undo() => _setter(_originalValue);
}