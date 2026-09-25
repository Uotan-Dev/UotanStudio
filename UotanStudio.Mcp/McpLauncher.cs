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
    /// <param name="args">Launch arguments; the match on <c>--mcp-headless</c> ignores case.
    /// <para>启动参数；对 <c>--mcp-headless</c> 的匹配忽略大小写。</para></param>
    /// <returns><see langword="true"/> when any argument equals <c>--mcp-headless</c>.
    /// <para>任一参数等于 <c>--mcp-headless</c> 时为 <see langword="true"/>。</para></returns>
    public static bool IsHeadlessRequested(string[] args) => HasFlag(args, "--mcp-headless");

    /// <summary>
    /// Returns true when a desktop session should also host an MCP server.
    /// <para>判断桌面会话是否应同时承载 MCP 服务器。</para>
    /// </summary>
    /// <param name="args">Launch arguments; the match on <c>--mcp</c> ignores case.
    /// <para>启动参数；对 <c>--mcp</c> 的匹配忽略大小写。</para></param>
    /// <returns><see langword="true"/> when any argument equals <c>--mcp</c>.
    /// <para>任一参数等于 <c>--mcp</c> 时为 <see langword="true"/>。</para></returns>
    public static bool IsCompanionRequested(string[] args) => HasFlag(args, "--mcp");

    /// <summary>
    /// Runs the headless MCP server until the client closes the stdio connection.
    /// <para>运行无头 MCP 服务器，直到客户端关闭 stdio 连接。</para>
    /// Firmware format handlers are registered first so every format tool works without the desktop host.
    /// <para>会先注册固件格式处理器，使全部格式工具在无桌面宿主时可用。</para>
    /// </summary>
    /// <param name="args">Launch arguments read by <see cref="McpServerConfig.Parse"/>, including <c>--mcp-root</c> and <c>--allow-flash</c>.
    /// <para>由 <see cref="McpServerConfig.Parse"/> 读取的启动参数，含 <c>--mcp-root</c> 与 <c>--allow-flash</c>。</para></param>
    /// <param name="cancellationToken">Stops the stdio loop; defaults to no cancellation.
    /// <para>停止 stdio 循环；默认不取消。</para></param>
    /// <returns>A task that completes when the transport closes or cancellation is observed.
    /// <para>在传输关闭或观察到取消时完成的任务。</para></returns>
    public static async Task RunHeadlessAsync(string[] args, CancellationToken cancellationToken = default)
    {
        UotanStudio.Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
        var host = new McpServerHost(McpServerConfig.Parse(args));
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the MCP server alongside the desktop UI, injecting the host-equivalent delivery
    /// tool-preparation service so delivery_build works in companion mode.
    /// <para>伴随桌面 UI 运行 MCP 服务器，注入与宿主等价的交付工具准备服务，
    /// 使 delivery_build 在伴随模式可用。</para>
    /// </summary>
    /// <param name="args">Launch arguments read by <see cref="McpServerConfig.Parse"/>; the sandbox root still applies, so companion mode confines client paths as well.
    /// <para>由 <see cref="McpServerConfig.Parse"/> 读取的启动参数；沙箱根目录依然生效，伴随模式同样限制客户端路径。</para></param>
    /// <param name="deviceManager">Host-supplied gateway that owns device enumeration and transactions for every tool call.
    /// <para>由宿主提供的网关，为每次工具调用持有设备枚举与事务管理。</para></param>
    /// <param name="cancellationToken">Stops the stdio loop; defaults to no cancellation.
    /// <para>停止 stdio 循环；默认不取消。</para></param>
    /// <returns>A task that completes when the transport closes or cancellation is observed.
    /// <para>在传输关闭或观察到取消时完成的任务。</para></returns>
    public static async Task RunCompanionAsync(string[] args, Tools.IDeviceManagerGateway deviceManager,
        CancellationToken cancellationToken = default)
    {
        EnableDeliveryBuild();

        var host = new McpServerHost(McpServerConfig.Parse(args), deviceManager);
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Injects the host-equivalent delivery tool-preparation service so <c>delivery_build</c> works
    /// inside a desktop session, both for the companion stdio launch and for the settings-page HTTP
    /// exposure.
    /// <para>注入与宿主等价的交付工具准备服务，使 <c>delivery_build</c> 在伴随 stdio 启动与设置页的
    /// HTTP 暴露中均可用。</para>
    /// </summary>
    public static void EnableDeliveryBuild()
    {
        Tools.DeliveryToolFactory.ToolsProvider = static () =>
            new UotanStudio.Services.Delivery.DeliveryToolService(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UotanStudio", "delivery-tools"));
    }

    private static bool HasFlag(string[] args, string flag) => args.Any(a =>
        string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}