using System.Collections.ObjectModel;
using Avalonia;

namespace UotanStudio.ViewModels.NodeViewModels.EditorCommands;

/// <summary>
/// Add-node command. Executing adds the node to the canvas; undoing removes the node and its related connections.
/// <para>添加节点命令。执行时将节点加入画布，撤销时移除该节点及其关联连接。</para>
/// </summary>
public sealed class AddNodeCommand : IEditorCommand
{
    private readonly ObservableCollection<FlowNodeViewModel> _nodes;
    private readonly ObservableCollection<NodeConnectionViewModel> _connections;
    private readonly FlowNodeViewModel _node;
    private readonly List<NodeConnectionViewModel> _related = [];

    public string DisplayName => "Add Node";

    /// <summary>
    /// Creates an add-node command.
    /// <para>创建添加节点命令。</para>
    /// </summary>
    /// <param name="nodes">画布节点集合。<para>Canvas node collection.</para></param>
    /// <param name="connections">画布连接线集合。<para>Canvas connection collection.</para></param>
    /// <param name="node">要添加的节点。<para>The node to add.</para></param>
    public AddNodeCommand(ObservableCollection<FlowNodeViewModel> nodes, ObservableCollection<NodeConnectionViewModel> connections, FlowNodeViewModel node)
    {
        _nodes = nodes;
        _connections = connections;
        _node = node;
    }

    /// <inheritdoc/>
    public void Execute()
    {
        if (!_nodes.Contains(_node))
        {
            _nodes.Add(_node);
        }
        _related.Clear();
        _related.AddRange(_connections.Where(c => _node.Input.Any(i => i == c.Target) || _node.Output.Any(o => o == c.Source)));
    }

    /// <inheritdoc/>
    public void Undo()
    {
        foreach (var c in _related)
        {
            _connections.Remove(c);
        }
        _nodes.Remove(_node);
    }
}

/// <summary>
/// Remove-node command. Executing removes the node and its connections; undoing restores the node and connections.
/// <para>移除节点命令。执行时移除节点及其关联连接，撤销时重新加入节点与连接。</para>
/// </summary>
public sealed class RemoveNodeCommand : IEditorCommand
{
    private readonly ObservableCollection<FlowNodeViewModel> _nodes;
    private readonly ObservableCollection<NodeConnectionViewModel> _connections;
    private readonly FlowNodeViewModel _node;
    private readonly List<NodeConnectionViewModel> _related = [];

    public string DisplayName => "Remove Node";

    /// <summary>
    /// Creates a remove-node command.
    /// <para>创建移除节点命令。</para>
    /// </summary>
    /// <param name="nodes">画布节点集合。<para>Canvas node collection.</para></param>
    /// <param name="connections">画布连接线集合。<para>Canvas connection collection.</para></param>
    /// <param name="node">要移除的节点。<para>The node to remove.</para></param>
    public RemoveNodeCommand(ObservableCollection<FlowNodeViewModel> nodes, ObservableCollection<NodeConnectionViewModel> connections, FlowNodeViewModel node)
    {
        _nodes = nodes;
        _connections = connections;
        _node = node;
    }

    /// <inheritdoc/>
    public void Execute()
    {
        _related.Clear();
        _related.AddRange(_connections.Where(c => _node.Input.Any(i => i == c.Target) || _node.Output.Any(o => o == c.Source)));
        foreach (var c in _related)
        {
            _connections.Remove(c);
        }
        _nodes.Remove(_node);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (!_nodes.Contains(_node))
        {
            _nodes.Add(_node);
        }
        foreach (var c in _related)
        {
            if (!_connections.Contains(c))
            {
                _connections.Add(c);
            }
        }
    }
}

/// <summary>
/// Move-node command, recording the before/after positions of a batch of nodes. Executing applies the new positions; undoing restores the original ones.
/// <para>移动节点命令，记录一组节点移动前后的位置。执行时应用新位置，撤销时恢复原位置。</para>
/// </summary>
public sealed class MoveNodeCommand : IEditorCommand
{
    private readonly List<(FlowNodeViewModel Node, Point Original, Point Target)> _targets = [];

    public string DisplayName => "Move Nodes";

    /// <summary>
    /// Creates a move-node command.
    /// <para>创建移动节点命令。</para>
    /// </summary>
    public MoveNodeCommand()
    {
    }

    /// <summary>
    /// Adds a node to move along with its target position; whether it is a valid move is determined at command creation.
    /// <para>添加一个要移动的节点及其目标位置；是否为有效移动（位置有变化）将在地点创建时判定。</para>
    /// </summary>
    /// <param name="node">要移动的节点。<para>The node to move.</para></param>
    /// <param name="newLocation">目标位置。<para>The target position.</para></param>
    public void AddTarget(FlowNodeViewModel node, Point newLocation)
    {
        if (node == null || node.Location == newLocation)
        {
            return;
        }
        _targets.Add((node, node.Location, newLocation));
    }

    /// <summary>Adds a target with an explicitly captured original position.</summary>
    public void AddTarget(FlowNodeViewModel node, Point originalLocation, Point newLocation)
    {
        if (node == null || originalLocation == newLocation)
        {
            return;
        }
        _targets.Add((node, originalLocation, newLocation));
    }

    /// <summary>
    /// Gets whether there is at least one valid move target, deciding whether the command needs to be pushed onto the stack.
    /// <para>获取是否包含至少一个有效的移动目标，用于决定命令是否需要入栈。</para>
    /// </summary>
    public bool HasTargets => _targets.Count > 0;

    /// <inheritdoc/>
    public void Execute()
    {
        foreach (var t in _targets)
        {
            t.Node.Location = t.Target;
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        foreach (var t in _targets)
        {
            t.Node.Location = t.Original;
        }
    }
}