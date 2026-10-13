using System.Text;
using UotanStudio.Firmware.AndroidMeta.Arsc;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// The structural location anchor of an ARSC resource-table search-text line: the owning
/// package/type/entry models of that line.
/// <para>ARSC 资源表的结构化搜索文本定位锚点：按行记录所属的包/类型/条目模型。</para>
/// </summary>
/// <param name="Package">
/// The owning package.
/// <para>所属包。</para>
/// </param>
/// <param name="Type">
/// The owning type table; <c>null</c> for package lines.
/// <para>所属类型表；包级行为 <c>null</c>。</para>
/// </param>
/// <param name="Entry">
/// The owning entry; <c>null</c> for package or type lines.
/// <para>所属条目；包或类型级行为 <c>null</c>。</para>
/// </param>
internal sealed record ResSearchAnchor(ResPackage Package, ResType? Type, ResEntry? Entry);

/// <summary>
/// Serializes an ARSC resource table into line-based search text (package → type → configuration →
/// entry), always yielding identical text for the same table.
/// <para>把 ARSC 资源表序列化为行式搜索文本（包 → 类型 → 配置 → 条目），同一张表始终产出一致的文本。</para>
/// </summary>
internal static class ResourceTableSearchText
{
    /// <summary>
    /// Serializes the table into search text; an optional callback reports each line's start offset and
    /// structural anchor.
    /// <para>把资源表序列化为搜索文本；可选回调按行报告起始偏移与结构锚点。</para>
    /// </summary>
    /// <param name="table">
    /// The resource-table model.
    /// <para>资源表模型。</para>
    /// </param>
    /// <param name="onLine">
    /// Per-line callback receiving the start offset and anchor; null for text-only output.
    /// <para>按行接收起始偏移与锚点的回调；为 null 时只生成文本。</para>
    /// </param>
    /// <returns>
    /// The generated search text.
    /// <para>生成的搜索文本。</para>
    /// </returns>
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
    /// The display name of a resource type ID.
    /// <para>资源类型 ID 的显示名。</para>
    /// </summary>
    /// <param name="typeId">
    /// The resource type ID byte.
    /// <para>资源类型 ID 字节。</para>
    /// </param>
    /// <returns>
    /// The mapped name, or a generic label for an unmapped ID.
    /// <para>已映射的名称；未映射的 ID 返回通用标签。</para>
    /// </returns>
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