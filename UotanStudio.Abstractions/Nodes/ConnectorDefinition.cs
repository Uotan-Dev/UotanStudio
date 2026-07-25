namespace UotanStudio.Abstractions.Nodes;

/// <summary>
/// <para>连接器（端口）定义，描述节点的输入/输出端口元数据。</para>
/// Connector (port) definition, describing the metadata of a node's input/output ports.
/// </summary>
public sealed class ConnectorDefinition
{
    /// <summary>
    /// <para>连接器标题（显示在节点端口旁）。</para>
    /// Connector title (displayed next to the port on the node).
    /// </summary>
    public string Title { get; init; }

    /// <summary>
    /// <para>连接器数据类型标识（如 <c>data</c>、<c>device</c>）。</para>
    /// Connector data type identifier (e.g. <c>data</c>, <c>device</c>).
    /// </summary>
    public string Type { get; init; }

    /// <summary>
    /// <para>方向：输入或输出。</para>
    /// Direction: input or output.
    /// </summary>
    public ConnectorDirection Direction { get; init; }

    /// <summary>
    /// <para>是否允许多个连接。默认 <c>false</c>（单连接）。</para>
    /// Whether multiple connections are allowed. Defaults to <c>false</c> (single connection).
    /// </summary>
    public bool AllowMultiple { get; init; }

    /// <summary>
    /// <para>可选：默认值（用于无连接时的输入）。</para>
    /// Optional: default value (used for inputs without a connection).
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// <para>创建连接器定义。</para>
    /// Creates a connector definition.
    /// </summary>
    /// <param name="title">
    /// <para>连接器标题。</para>
    /// Connector title.
    /// </param>
    /// <param name="type">
    /// <para>连接器数据类型标识。</para>
    /// Connector data type identifier.
    /// </param>
    /// <param name="direction">
    /// <para>方向。</para>
    /// Direction.
    /// </param>
    /// <param name="allowMultiple">
    /// <para>是否允许多个连接。</para>
    /// Whether multiple connections are allowed.
    /// </param>
    public ConnectorDefinition(string title, string type, ConnectorDirection direction, bool allowMultiple = false)
    {
        Title = title;
        Type = type;
        Direction = direction;
        AllowMultiple = allowMultiple;
    }

    /// <summary>
    /// <para>便捷工厂：创建输入端口。</para>
    /// Convenience factory: creates an input port.
    /// </summary>
    public static ConnectorDefinition Input(string title, string type = WellKnownConnectorTypes.Any, bool allowMultiple = false)
        => new(title, type, ConnectorDirection.Input, allowMultiple);

    /// <summary>
    /// <para>便捷工厂：创建输出端口。</para>
    /// Convenience factory: creates an output port.
    /// </summary>
    public static ConnectorDefinition Output(string title, string type = WellKnownConnectorTypes.Any, bool allowMultiple = true)
        => new(title, type, ConnectorDirection.Output, allowMultiple);
}
