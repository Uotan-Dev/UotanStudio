using System.Text.Json.Nodes;

namespace UotanStudio.Services;

/// <summary>AOT-safe append extension for <see cref="JsonArray"/> that routes through the non-generic
/// <c>IList&lt;JsonNode?&gt;</c> to avoid trimming warnings.
/// <para>AOT 安全的 <see cref="JsonArray"/> 追加扩展，经由非泛型 <c>IList&lt;JsonNode?&gt;</c> 以避免裁剪警告。</para></summary>
internal static class JsonArrayAotExtensions
{
    extension(JsonArray array)
    {
        /// <summary>Appends a node to the <see cref="JsonArray"/> via the non-generic
        /// <c>IList&lt;JsonNode?&gt;</c> interface, staying AOT-safe.
        /// <para>经非泛型 <c>IList&lt;JsonNode?&gt;</c> 接口把节点追加到 <see cref="JsonArray"/>，保持 AOT 安全。</para></summary>
        /// <param name="node">The node to append.<para>要追加的节点。</para></param>
        public void Append(JsonNode? node) =>
            ((IList<JsonNode?>)array).Add(node);
    }
}