using System.Collections.Concurrent;
using ModelContextProtocol.Server;
using UotanStudio.Mcp;

namespace UotanStudio.Tests.Services;

/// <summary>
/// The MCP tool catalog built once per flashing gate, shared by the tests that only read tool names,
/// descriptions and input schemas. Each build costs seconds because the protocol layer generates a schema for
/// every tool, so a catalog-shape test must not rebuild it.
/// <para>按刷写门控各构建一次的 MCP 工具目录，供只读取工具名、描述与输入架构的测试共用。每次构建都要为
/// 每个工具生成架构，耗时数秒，因此只校验目录形态的用例不得各自重建。</para>
/// </summary>
internal static class McpToolCatalogCache
{
    private static readonly ConcurrentDictionary<bool, IReadOnlyList<McpServerTool>> Catalogs = new();

    /// <summary>
    /// Gets the shared tool list for one flashing gate.
    /// <para>取某一刷写门控下共用的工具列表。</para>
    /// </summary>
    /// <param name="allowFlash">Whether the host may change devices. <para>宿主是否允许改动设备。</para></param>
    /// <returns>The built tools, in declared order. <para>按声明顺序构建出的工具。</para></returns>
    internal static IReadOnlyList<McpServerTool> Tools(bool allowFlash) =>
        Catalogs.GetOrAdd(allowFlash, static gate =>
            new McpServerHost(PathSandbox.Unrestricted, gate).BuildTools());

    /// <summary>
    /// Gets the protocol names of the shared tool list for one flashing gate.
    /// <para>取某一刷写门控下共用工具列表的协议名。</para>
    /// </summary>
    /// <param name="allowFlash">Whether the host may change devices. <para>宿主是否允许改动设备。</para></param>
    /// <returns>The emitted protocol names. <para>已发出的协议名。</para></returns>
    internal static IReadOnlyList<string> Names(bool allowFlash) =>
        [.. Tools(allowFlash).Select(tool => tool.ProtocolTool.Name ?? string.Empty)];
}