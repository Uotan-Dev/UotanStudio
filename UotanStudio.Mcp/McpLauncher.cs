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
    /// <param name="audit">Sink observing every served tool call; <see langword="null"/> records nothing.
    /// <para>观察每次工具调用的接收端；为 <see langword="null"/> 时不记录。</para></param>
    /// <returns>A task that completes when the transport closes or cancellation is observed.
    /// <para>在传输关闭或观察到取消时完成的任务。</para></returns>
    public static async Task RunHeadlessAsync(string[] args, CancellationToken cancellationToken = default,
        Tools.IMcpToolCallAudit? audit = null)
    {
        Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
        var host = new McpServerHost(McpServerConfig.Parse(args));
        await host.RunAsync(cancellationToken, audit).ConfigureAwait(false);
    }

    /// <summary>Runs the MCP server alongside the desktop UI, injecting the host-equivalent delivery tool-preparation service so delivery_build works in companion mode.
    /// <para>伴随桌面 UI 运行 MCP 服务器，注入与宿主等价的交付工具准备服务， 使 delivery_build 在伴随模式可用。</para></summary>
    /// <param name="args">Launch arguments read by <see cref="McpServerConfig.Parse"/>; the sandbox root still applies, so companion mode confines client paths as well.
    /// <para>由 <see cref="McpServerConfig.Parse"/> 读取的启动参数；沙箱根目录依然生效，伴随模式同样限制客户端路径。</para></param>
    /// <param name="deviceManager">Host-supplied gateway that owns device enumeration and transactions for every tool call.
    /// <para>由宿主提供的网关，为每次工具调用持有设备枚举与事务管理。</para></param>
    /// <param name="signingKeys">Host-supplied signing key provider for <c>delivery_build</c>; <see langword="null"/> keeps signed plans failing honestly.
    /// <para>宿主提供的签名密钥供应器，供 <c>delivery_build</c> 使用；为 <see langword="null"/> 时签名计划如实失败。</para></param>
    /// <param name="cancellationToken">Stops the stdio loop; defaults to no cancellation.
    /// <para>停止 stdio 循环；默认不取消。</para></param>
    /// <param name="audit">Sink observing every served tool call; <see langword="null"/> records nothing.
    /// <para>观察每次工具调用的接收端；为 <see langword="null"/> 时不记录。</para></param>
    /// <param name="sandboxRoot">
    /// Explicit sandbox root supplied by the desktop host, overriding the argument-derived root;
    /// <see langword="null"/> keeps the argument behavior (defaulting to the process working directory),
    /// an empty or whitespace value selects the unrestricted sandbox with the flash gate closed, and an
    /// invalid path falls back the same way.
    /// <para>由桌面宿主提供的显式沙箱根目录，覆盖来自启动参数的根；为 <see langword="null"/> 时保持参数行为
    /// （默认取进程工作目录），空白表示不受限沙箱并关闭刷写门控，路径非法时同样回退。</para>
    /// </param>
    /// <returns>A task that completes when the transport closes or cancellation is observed.
    /// <para>在传输关闭或观察到取消时完成的任务。</para></returns>
    public static async Task RunCompanionAsync(string[] args, Tools.IDeviceManagerGateway deviceManager,
        Func<UotanStudio.Pipeline.Delivery.IDeliverySigningKeyProvider?>? signingKeys = null,
        CancellationToken cancellationToken = default,
        Tools.IMcpToolCallAudit? audit = null,
        string? sandboxRoot = null)
    {
        var config = McpServerConfig.Parse(args);
        var (sandbox, allowFlash) = sandboxRoot is null
            ? (new PathSandbox(config.RootDirectory), config.AllowFlash)
            : BuildSandbox(sandboxRoot, config.AllowFlash);
        var host = new McpServerHost(
            sandbox, allowFlash, deviceManager,
            CreateDesktopDeliveryTools(), signingKeys);
        await host.RunAsync(cancellationToken, audit).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the sandbox for an explicitly supplied companion root, mirroring the desktop exposure service:
    /// a blank root selects the unrestricted sandbox with the flash gate closed, and an invalid path falls
    /// back the same way.
    /// <para>为显式提供的伴随模式根目录构建沙箱，与桌面暴露服务一致：空白根目录选择不受限沙箱并关闭刷写门控，
    /// 非法路径同样回退。</para>
    /// </summary>
    /// <param name="sandboxRoot">Explicit sandbox root from the host. <para>宿主提供的显式沙箱根目录。</para></param>
    /// <param name="requestedAllowFlash">Flash gate requested through the launch arguments.
    /// <para>启动参数请求的刷写门控。</para></param>
    /// <returns>The sandbox and the effective flash gate. <para>沙箱与生效的刷写门控。</para></returns>
    private static (PathSandbox Sandbox, bool AllowFlash) BuildSandbox(string sandboxRoot, bool requestedAllowFlash)
    {
        var root = sandboxRoot.Trim();
        if (root.Length == 0)
        {
            return (PathSandbox.Unrestricted, false);
        }

        try
        {
            return (new PathSandbox(root), requestedAllowFlash);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                   or NotSupportedException or System.Security.SecurityException)
        {
            return (PathSandbox.Unrestricted, false);
        }
    }

    /// <summary>
    /// Builds the host-equivalent delivery tool-preparation provider for <c>delivery_build</c> inside a
    /// desktop session. It is passed to a single <see cref="McpServerHost"/> instead of being written to
    /// process-wide state, so availability follows the host that was constructed rather than launch order.
    /// <para>构建桌面会话内 <c>delivery_build</c> 使用的宿主等价工具准备提供器。它只交给单个
    /// <see cref="McpServerHost"/>，不再写入进程级状态，因此能力可用性取决于所构建的宿主而非启动顺序。</para>
    /// </summary>
    /// <returns>A provider creating a tool service over the shared cache directory on each call. <para>每次调用创建缓存目录与 HTTP 客户端组合的工具准备服务。</para></returns>
    public static Func<UotanStudio.Pipeline.Delivery.IDeliveryTools?> CreateDesktopDeliveryTools() =>
        static () =>
            new UotanStudio.Pipeline.Delivery.DeliveryToolService(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                UotanStudio.Pipeline.Delivery.DeliveryService.DefaultToolCacheDirectory);

    private static bool HasFlag(string[] args, string flag) => args.Any(a =>
        string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}