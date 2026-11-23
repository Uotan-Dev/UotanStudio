using System.Text.Json;
using ModelContextProtocol.Server;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Internal action exposed through the MCP meta-tool surface.
/// <para>通过 MCP 元工具表面暴露的内部动作。</para>
/// </summary>
/// <param name="Action">Stable action name. <para>稳定动作名。</para></param>
/// <param name="Domain">Domain: parse/unpack/repack/modify/flash/verify/flow/device/project/env/misc. <para>所属领域。</para></param>
/// <param name="Summary">Short searchable description. <para>可检索的简短描述。</para></param>
/// <param name="InputSchema">JSON schema for params. <para>参数 JSON schema。</para></param>
/// <param name="ReadOnly">Whether action is read-only. <para>是否只读。</para></param>
/// <param name="Destructive">Whether action is destructive. <para>是否破坏性。</para></param>
/// <param name="FlashGated">Whether action requires flash gate. <para>是否需要刷写门控。</para></param>
/// <param name="NodeTypeKey">Optional workflow node key. <para>可选流程节点键。</para></param>
/// <param name="Tool">Underlying MCP tool implementation. <para>底层 MCP 工具实现。</para></param>
internal sealed record McpActionDescriptor(
    string Action,
    string Domain,
    string Summary,
    JsonElement InputSchema,
    bool ReadOnly,
    bool Destructive,
    bool FlashGated,
    string? NodeTypeKey,
    McpServerTool Tool);