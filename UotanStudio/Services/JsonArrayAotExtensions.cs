using System.Text.Json.Nodes;

namespace UotanStudio.Services;

/// <summary>
/// <para>AOT 安全的 <see cref="JsonArray"/> 追加扩展：泛型 <c>JsonArray.Add&lt;T&gt;</c> 带
/// RequiresUnreferencedCode / RequiresDynamicCode 注解，会在裁剪与 AOT 编译时产生警告；
/// 其非泛型接口 <c>IList&lt;JsonNode?&gt;.Add</c> 无这些注解且行为完全一致，走该接口即可消除警告。</para>
/// <para>AOT-safe <see cref="JsonArray"/> append extension: the generic
/// <c>JsonArray.Add&lt;T&gt;</c> carries RequiresUnreferencedCode / RequiresDynamicCode
/// annotations that trigger trimming/AOT warnings; the non-generic
/// <c>IList&lt;JsonNode?&gt;.Add</c> interface has no annotations and behaves identically.</para>
/// </summary>
internal static class JsonArrayAotExtensions
{
    /// <summary>
    /// <para>把节点追加到 <see cref="JsonArray"/>（经非泛型 <c>IList&lt;JsonNode?&gt;</c> 接口，AOT 安全）。</para>
    /// <para>Appends a node to the <see cref="JsonArray"/> via the non-generic
    /// <c>IList&lt;JsonNode?&gt;</c> interface (AOT-safe).</para>
    /// </summary>
    public static void Append(this JsonArray array, JsonNode? node) =>
        ((System.Collections.Generic.IList<JsonNode?>)array).Add(node);
}