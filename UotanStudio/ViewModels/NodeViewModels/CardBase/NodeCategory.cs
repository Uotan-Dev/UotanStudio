namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Enum defining different categories of toolbox nodes.
/// <para>节点类别枚举，定义不同类型的工具箱节点。</para>
/// </summary>
public enum NodeCategory
{
    /// <summary>
    /// Firmware operation node.
    /// <para>固件操作节点。</para>
    /// </summary>
    Firmware,
    /// <summary>
    /// Flash operation node.
    /// <para>烧录操作节点。</para>
    /// </summary>
    Flash,
    /// <summary>
    /// Data processing node.
    /// <para>数据处理节点。</para>
    /// </summary>
    Data,
    /// <summary>
    /// Flow control node.
    /// <para>流程控制节点。</para>
    /// </summary>
    Flow,
    /// <summary>
    /// Output node.
    /// <para>输出节点。</para>
    /// </summary>
    Output,
    /// <summary>Host-rendered composite node provided by a visual-plugin graph.</summary>
    Plugin
}