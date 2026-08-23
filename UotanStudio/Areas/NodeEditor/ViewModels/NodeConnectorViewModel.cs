using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.NodeEditor.ViewModels;

/// <summary>
/// <para>节点连接器视图模型，表示节点上的输入或输出端口。</para>
/// View model for a node connector, representing an input or output port on a node.
/// </summary>
public partial class NodeConnectorViewModel : ViewModelBase
{
    /// <summary>
    /// <para>获取或设置连接器的显示标题。</para>
    /// Gets or sets the display title of the connector.
    /// </summary>
    public string Title
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>
    /// <para>获取或设置连接器携带的数据值。连接建立时由源端口（输出）同步到目标端口（输入）。</para>
    /// Gets or sets the data value carried by the connector. Synced from source (output) to target (input) upon connection.
    /// </summary>
    public string Value
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>
    /// <para>获取或设置连接器的唯一标识符。</para>
    /// Gets or sets the unique identifier of the connector.
    /// </summary>
    public Guid Id
    {
        get;
        set => SetProperty(ref field, value);
    } = Guid.NewGuid();

    /// <summary>
    /// <para>获取或设置连接器的锚点位置（用于连接线定位）。</para>
    /// Gets or sets the anchor position of the connector (used for connection line positioning).
    /// </summary>
    public Point Anchor
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>获取或设置连接器允许的最大连接数，0 表示不限制。</para>
    /// Gets or sets the maximum number of connections allowed for this connector. 0 means unlimited.
    /// </summary>
    public int MaxConnections
    {
        get;
        set => SetProperty(ref field, value);
    } = 0;

    /// <summary>
    /// <para>获取或设置连接器是否已连接。</para>
    /// Gets or sets whether the connector is connected.
    /// </summary>
    public bool IsConnected
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>获取该连接器所属的节点（运行时由节点设置，用于断开时回溯查找连接）。</para>
    /// Gets the node that owns this connector (set at runtime by the node).
    /// </summary>
    public FlowNodeViewModel? Node { get; internal set; }

    /// <summary>
    /// <para>获取该连接器当前的全部连接（每次访问返回快照，避免枚举过程中集合被修改）。</para>
    /// Gets all connections currently linked to this connector. Returns a snapshot on each access to avoid modification-during-enumeration errors.
    /// </summary>
    public IReadOnlyList<NodeConnectionViewModel> Connections =>
        Node is null ? Array.Empty<NodeConnectionViewModel>() : Node.NodeConnections.Where(c => c.Source == this || c.Target == this).ToList();

    /// <summary>
    /// <para>断开该连接器上的所有连接。</para>
    /// Disconnects all connections on this connector. Exposed as <c>DisconnectCommand</c> for XAML binding.
    /// </summary>
    [RelayCommand]
    public void Disconnect()
    {
        if (Node is null)
        {
            return;
        }

        var toRemove = Node.NodeConnections.Where(c => c.Source == this || c.Target == this).ToList();
        foreach (var conn in toRemove)
        {
            var s = conn.Source;
            var t = conn.Target;
            Node.NodeConnections.Remove(conn);
            s?.SetConnected(s.Connections.Any());
            t?.SetConnected(t.Connections.Any());
        }
    }

    internal void SetConnected(bool value) => IsConnected = value;
}