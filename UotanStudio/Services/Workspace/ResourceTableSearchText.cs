using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// <para>ARSC 资源表的结构化搜索文本定位锚点：按行记录所属的包/类型/条目模型。</para>
/// The structural location anchor of an ARSC resource-table search-text line: the owning
/// package/type/entry models of that line.
/// </summary>
/// <param name="Package">所属包。<para>The owning package.</para></param>
/// <param name="Type">所属类型表；包级行为 <c>null</c>。<para>The owning type table; <c>null</c> for package lines.</para></param>
/// <param name="Entry">所属条目；包/类型级行为 <c>null</c>。<para>The owning entry; <c>null</c> for package/type lines.</para></param>
internal sealed record ResSearchAnchor(ResPackage Package, ResType? Type, ResEntry? Entry);

/// <summary>
/// <para>把 ARSC 资源表序列化为行式搜索文本（包 → 类型 → 配置 → 条目）。ARSC 编辑器与项目范围
/// 格式适配共用同一序列化，保证「同一文件两次文本化结果一致」，使项目范围导航的快照比对成立。</para>
/// Serializes an ARSC resource table into line-based search text (package → type → configuration →
/// entry). The ARSC editor and the project-scope format adapter share this serializer so the same
/// file always yields identical text, keeping project-scope navigation's snapshot comparison valid.
/// </summary>
internal static class ResourceTableSearchText
{
    /// <summary>
    /// <para>序列化资源表为搜索文本；可选回调按行报告起始偏移与结构锚点（供树节点定位）。</para>
    /// Serializes the table into search text; an optional callback reports each line's start offset
    /// and structural anchor (for tree-node location).
    /// </summary>
    /// <param name="table">资源表模型。<para>The resource-table model.</para></param>
    /// <param name="onLine">每行回调；为 <c>null</c> 时只生成文本。<para>Per-line callback; text-only when <c>null</c>.</param>
    internal static string Serialize(ResourceTable table, Action<int, ResSearchAnchor>? onLine = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var builder = new StringBuilder();
        foreach (var package in table.Packages)
        {
            Emit(builder, onLine, new(package, null, null), 0, $"{package.Name} (0x{package.Id:x2})");
            foreach (var group in package.Types.GroupBy(type => type.Id).OrderBy(group => group.Key))
            {
                var representative = group.First();
                Emit(builder, onLine, new(package, representative, null), 1,
                    $"0x{group.Key:x2} {TypeDisplayName(group.Key)} ({group.Count()})");
                foreach (var type in group)
                {
                    Emit(builder, onLine, new(package, type, null), 2, type.ConfigDescription);
                    foreach (var entry in type.Entries)
                    {
                        if (entry is null)
                        {
                            continue;
                        }

                        var key = package.KeyStrings.GetString(entry.KeyIndex);
                        var value = entry.Value?.FormatValue(table.GlobalPool, package.Id) ?? string.Empty;
                        var text = string.IsNullOrEmpty(key) ? value : $"{key} = {value}";
                        Emit(builder, onLine, new(package, type, entry), 3, text);
                    }
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// <para>资源类型 ID 的显示名（与 ARSC 编辑器的左栏类型分组一致）。</para>
    /// The display name of a resource type ID (aligned with the ARSC editor's left type group).
    /// </summary>
    internal static string TypeDisplayName(byte typeId) => typeId switch
    {
        1 => "attr",
        2 => "string",
        3 => "style",
        4 => "color",
        5 => "dimen",
        6 => "drawable",
        7 => "layout",
        8 => "anim",
        9 => "mipmap",
        10 => "menu",
        12 => "xml",
        _ => "type"
    };

    private static void Emit(StringBuilder builder, Action<int, ResSearchAnchor>? onLine,
        ResSearchAnchor anchor, int depth, string text)
    {
        onLine?.Invoke(builder.Length, anchor);
        builder.Append(' ', depth * 2).Append(text).Append('\n');
    }
}