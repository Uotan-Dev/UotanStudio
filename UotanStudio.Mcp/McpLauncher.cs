using System.Net.Http;

namespace UotanStudio.Mcp;

/// <summary>
/// Launch entry points shared by the headless and companion desktop paths.
/// <para>无头模式与桌面伴随模式共用的启动入口。</para>
/// </summary>
public static class McpLauncher
{
    /// <summary>
    /// Returns true when the process should run as a headless MCP server.
    /// <para>判断进程是否应以无头 MCP 服务器方式运行。</para>
    /// </summary>
    public static bool IsHeadlessRequested(string[] args) => HasFlag(args, "--mcp-headless");

    /// <summary>
    /// Returns true when a desktop session should also host an MCP server.
    /// <para>判断桌面会话是否应同时承载 MCP 服务器。</para>
    /// </summary>
    public static bool IsCompanionRequested(string[] args) => HasFlag(args, "--mcp");

    /// <summary>
    /// Runs the headless MCP server until the client closes the stdio connection.
    /// <para>运行无头 MCP 服务器，直到客户端关闭 stdio 连接。</para>
    /// </summary>
    public static async Task RunHeadlessAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // Minimal host-service bootstrap: format registry only. Device tools enumerate through
        // the shared CommKit / adb server and need no desktop monitor state.
        // 最小宿主服务引导：仅格式注册表。设备工具经共享 CommKit / adb server 枚举，
        // 不依赖桌面监视器状态。
        UotanStudio.Firmware.SharedKernel.FirmwareFormatRegistry.RegisterAll();
        var host = new McpServerHost(McpServerConfig.Parse(args));
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the MCP server alongside the desktop UI, injecting the host-equivalent delivery
    /// tool-preparation service so delivery_build works in companion mode.
    /// <para>伴随桌面 UI 运行 MCP 服务器，注入与宿主等价的交付工具准备服务，
    /// 使 delivery_build 在伴随模式可用。</para>
    /// </summary>
    public static async Task RunCompanionAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // Same pinned-tool cache directory as the desktop delivery view; both share downloads.
        // 与桌面交付视图相同的钉住工具缓存目录；两者共享下载结果。
        Tools.DeliveryToolFactory.ToolsProvider = static () =>
            new UotanStudio.Services.Delivery.DeliveryToolService(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UotanStudio", "delivery-tools"));

        var host = new McpServerHost(McpServerConfig.Parse(args));
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool HasFlag(string[] args, string flag) => args.Any(a =>
        string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}