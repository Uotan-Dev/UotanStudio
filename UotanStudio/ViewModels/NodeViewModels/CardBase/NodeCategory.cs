namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>节点类别枚举，定义不同类型的工具箱节点。</para>
/// Enum defining different categories of toolbox nodes.
/// </summary>
public enum NodeCategory
{
    /// <summary>
    /// <para>固件操作节点。</para>
    /// Firmware operation node.
    /// </summary>
    Firmware,
    /// <summary>
    /// <para>DEX 工具节点。</para>
    /// DEX tool node.
    /// </summary>
    Dex,
    /// <summary>
    /// <para>烧录操作节点。</para>
    /// Flash operation node.
    /// </summary>
    Flash,
    /// <summary>
    /// <para>数据处理节点。</para>
    /// Data processing node.
    /// </summary>
    Data,
    /// <summary>
    /// <para>流程控制节点。</para>
    /// Flow control node.
    /// </summary>
    Flow,
    /// <summary>
    /// <para>输出节点。</para>
    /// Output node.
    /// </summary>
    Output,
    /// <summary>
    /// <para>MCP 工具节点（调用设置页配置的 MCP 服务器）。</para>
    /// MCP tool node (calls MCP servers configured on the settings page).
    /// </summary>
    Mcp
}