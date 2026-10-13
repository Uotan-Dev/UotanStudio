namespace UotanStudio.ViewModels.NodeViewModels.EditorCommands;

/// <summary>
/// Compound command, executing/undoing multiple undoable commands as one unit, used for batch operations
/// (e.g. removing multiple nodes, disconnecting several connections).
/// <para>复合命令，将多个可撤销命令作为一个整体执行/撤销，用于批量操作（如删除多个节点、断开多条连接）。</para>
/// </summary>
public sealed class CompoundCommand : IEditorCommand
{
    private readonly List<IEditorCommand> _commands;

    /// <summary>
    /// Gets the command name.
    /// <para>获取命令名称。</para>
    /// </summary>
    public string DisplayName => "Compound Edit";

    /// <summary>
    /// Creates a compound command.
    /// <para>创建复合命令。</para>
    /// </summary>
    /// <param name="commands">
    /// The child commands (in execution order).
    /// <para>子命令集合（按执行顺序）。</para>
    /// </param>
    public CompoundCommand(IEnumerable<IEditorCommand> commands)
    {
        _commands = [.. commands];
    }

    /// <summary>
    /// Gets the number of child commands.
    /// <para>获取子命令数量。</para>
    /// </summary>
    public int Count => _commands.Count;

    /// <inheritdoc/>
    public void Execute()
    {
        foreach (var c in _commands)
        {
            c.Execute();
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        for (var i = _commands.Count - 1; i >= 0; i--)
        {
            _commands[i].Undo();
        }
    }
}