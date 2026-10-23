namespace UotanStudio.Workflow.Nodes.EditorCommands;

/// <summary>
/// Undoable editor command interface. Every undoable/redoable edit operation must implement this interface,
/// executed and rolled back uniformly by the command stack.
/// <para>节点编辑器可撤销命令接口。所有可撤销/重做的编辑操作必须实现此接口，由命令栈统一执行与回滚。</para>
/// </summary>
public interface IEditorCommand
{
    /// <summary>
    /// Gets the human-readable name of the command, used for logging and undo/redo hints.
    /// <para>获取命令的人类可读名称，用于日志与撤销/重做提示。</para>
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Executes the command, applying the edit operation forward.
    /// <para>执行命令（向前应用该编辑操作）。</para>
    /// </summary>
    void Execute();

    /// <summary>
    /// Undoes the command, rolling the edit operation back to its pre-execution state.
    /// <para>撤销命令，将编辑操作回滚执行前的状态。</para>
    /// </summary>
    void Undo();
}