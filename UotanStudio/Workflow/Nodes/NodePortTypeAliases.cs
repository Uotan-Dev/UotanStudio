namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Restores port types from their persisted names, including the names of port types that were merged into
/// another type.
/// <para>按持久化名称还原端口类型，包含已被合并到其他类型的端口类型的旧名称。</para>
/// </summary>
/// <remarks>
/// Port types are persisted by name, so a merge must not resolve the retired names to
/// <see cref="NodePortType.Any"/>: the wildcard would silently widen connection validation on every workflow
/// saved before the merge.
/// <para>端口类型按名称持久化，因此合并后不能让已退役的名称回落到 <see cref="NodePortType.Any"/>：
/// 通配会悄悄放宽每份合并前保存的工作流的连接校验。</para>
/// </remarks>
internal static class NodePortTypeAliases
{
    /// <summary>
    /// Retired port type names mapped to the type that absorbed them.
    /// <para>已退役端口类型名称到吸收它的类型的映射。</para>
    /// </summary>
    private static readonly Dictionary<string, NodePortType> Retired = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PatchWorkspace"] = NodePortType.Data,
        ["PatchAsset"] = NodePortType.Data,
        ["ImageEdits"] = NodePortType.Data,
        ["Record"] = NodePortType.Data,
        ["List"] = NodePortType.Data,
        ["UiForm"] = NodePortType.UiComponent
    };

    /// <summary>
    /// Parses a persisted port type name, resolving retired names to the type that absorbed them.
    /// <para>解析持久化的端口类型名称，把已退役的名称解析到吸收它的类型。</para>
    /// </summary>
    /// <param name="name">The persisted port type name. <para>持久化的端口类型名称。</para></param>
    /// <param name="type">The resolved port type. <para>解析得到的端口类型。</para></param>
    /// <returns>true when the name is a current or retired port type name; false when it is unknown. <para>名称为现行或已退役的端口类型名称时为 true；未知名称时为 false。</para></returns>
    internal static bool TryParse(string? name, out NodePortType type)
    {
        if (Enum.TryParse(name, true, out type))
        {
            return true;
        }
        if (name is not null && Retired.TryGetValue(name, out type))
        {
            return true;
        }
        type = NodePortType.Any;
        return false;
    }
}