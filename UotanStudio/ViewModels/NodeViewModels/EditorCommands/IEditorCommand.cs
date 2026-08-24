namespace UotanStudio.ViewModels.NodeViewModels.EditorCommands;

/// <summary>
/// <para>节点编辑器可撤销命令接口。所有可撤销/重做的编辑操作必须实现此接口，由命令栈统一执行与回滚。</para>
/// Undoable editor command interface. Every undoable/redoable edit operation must implement this interface,
/// executed and rolled back uniformly by the command stack.
/// </summary>
public interface IEditorCommand
{
    /// <summary>
    /// <para>获取命令的人类可读名称，用于日志与撤销/重做提示。</para>
    /// Gets the human-readable name of the command, used for logging and undo/redo hints.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// <para>执行命令（向前应用该编辑操作）。</para>
    /// Executes the command, applying the edit operation forward.
    /// </summary>
    void Execute();

    /// <summary>
    /// <para>撤销命令，将编辑操作回滚执行前的状态。</para>
    /// Undoes the command, rolling the edit operation back to its pre-execution state.
    /// </summary>
    void Undo();
}