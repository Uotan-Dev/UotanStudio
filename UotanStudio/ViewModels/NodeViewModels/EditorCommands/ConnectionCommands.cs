using System.Collections.ObjectModel;

namespace UotanStudio.ViewModels.NodeViewModels.EditorCommands;

/// <summary>
/// Add-connection command. Executing creates a connection between two connectors; undoing removes it.
/// <para>添加连接命令。执行时在两个连接器之间建立连接，撤销时移除该连接。</para>
/// </summary>
public sealed class AddConnectionCommand : IEditorCommand
{
    private readonly ObservableCollection<NodeConnectionViewModel> _connections;
    private readonly NodeConnectionViewModel _connection;

    public string DisplayName => "Add Connection";

    /// <summary>
    /// Creates an add-connection command.
    /// <para>创建添加连接命令。</para>
    /// </summary>
    /// <param name="connections">画布连接线集合。<para>Canvas connection collection.</para></param>
    /// <param name="connection">要建立的连接。<para>The connection to establish.</para></param>
    public AddConnectionCommand(ObservableCollection<NodeConnectionViewModel> connections, NodeConnectionViewModel connection)
    {
        _connections = connections;
        _connection = connection;
    }

    /// <inheritdoc/>
    public void Execute()
    {
        if (!_connections.Contains(_connection))
        {
            _connections.Add(_connection);
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _connections.Remove(_connection);
    }
}

/// <summary>
/// Remove-connection command. Executing removes the specified connection; undoing restores it.
/// <para>移除连接命令。执行时移除指定连接，撤销时恢复该连接。</para>
/// </summary>
public sealed class RemoveConnectionCommand : IEditorCommand
{
    private readonly ObservableCollection<NodeConnectionViewModel> _connections;
    private readonly NodeConnectionViewModel _connection;

    public string DisplayName => "Remove Connection";

    /// <summary>
    /// Creates a remove-connection command.
    /// <para>创建移除连接命令。</para>
    /// </summary>
    /// <param name="connections">画布连接线集合。<para>Canvas connection collection.</para></param>
    /// <param name="connection">要移除的连接。<para>The connection to remove.</para></param>
    public RemoveConnectionCommand(ObservableCollection<NodeConnectionViewModel> connections, NodeConnectionViewModel connection)
    {
        _connections = connections;
        _connection = connection;
    }

    /// <inheritdoc/>
    public void Execute()
    {
        _connections.Remove(_connection);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (!_connections.Contains(_connection))
        {
            _connections.Add(_connection);
        }
    }
}