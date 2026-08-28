namespace UotanStudio.ViewModels.NodeViewModels.EditorCommands;

/// <summary>
/// <para>复合命令，将多个可撤销命令作为一个整体执行/撤销，用于批量操作（如删除多个节点、断开多条连接）。</para>
/// Compound command, executing/undoing multiple undoable commands as one unit, used for batch operations
/// (e.g. removing multiple nodes, disconnecting several connections).
/// </summary>
public sealed class CompoundCommand : IEditorCommand
{
    private readonly List<IEditorCommand> _commands;

    /// <summary>
    /// <para>获取命令名称。</para>
    /// Gets the command name.
    /// </summary>
    public string DisplayName => "Compound Edit";

    /// <summary>
    /// <para>创建复合命令。</para>
    /// Creates a compound command.
    /// </summary>
    /// <param name="commands">
    /// <para>子命令集合（按执行顺序）。</para>
    /// The child commands (in execution order).
    /// </param>
    public CompoundCommand(IEnumerable<IEditorCommand> commands)
    {
        _commands = commands.ToList();
    }

    /// <summary>
    /// <para>获取子命令数量。</para>
    /// Gets the number of child commands.
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