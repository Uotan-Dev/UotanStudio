using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UotanStudio.Mcp.Tools;

namespace UotanStudio.Mcp;

/// <summary>
/// Assembles and runs the UotanStudio MCP server over stdio.
/// <para>组装并运行基于 stdio 的 UotanStudio MCP 服务器。</para>
/// Tools are registered explicitly via <see cref="McpServerTool.Create(Delegate, McpServerToolCreateOptions)"/>
/// instead of attribute reflection, keeping the registration path trim/AOT friendly and letting
/// flashing tools be gated behind <c>--allow-flash</c> at startup.
/// <para>工具通过 <see cref="McpServerTool.Create(Delegate, McpServerToolCreateOptions)"/> 显式注册
/// 而非特性反射，保持裁剪/AOT 友好，并允许刷写类工具在启动时按 <c>--allow-flash</c> 门控。</para>
/// </summary>
public sealed class McpServerHost
{
    /// <summary>
    /// Assembles the host from parsed launch config, building the matching path sandbox.
    /// <para>由解析后的启动配置组装宿主，并构建对应的路径沙箱。</para>
    /// </summary>
    /// <param name="config">Sandbox root and flash gate for this run. <para>本次运行的沙箱根目录与刷写门控。</para></param>
    /// <param name="deviceManager">Device gateway used by device tools; <see langword="null"/> selects the standalone gateway that owns device access without the desktop host.
    /// <para>设备工具使用的设备网关；为 <see langword="null"/> 时使用在无桌面宿主下自行持有设备访问的独立网关。</para></param>
    public McpServerHost(McpServerConfig config, IDeviceManagerGateway? deviceManager = null)
        : this(new PathSandbox(config.RootDirectory), config.AllowFlash, deviceManager)
    {
    }

    /// <summary>
    /// <para>用现成的沙箱与刷写门控组装宿主，供 HTTP 暴露路径复用同一套工具注册。</para>
    /// Assembles the host from an existing sandbox plus flash gate, so the HTTP exposure path
    /// registers exactly the same tool set.
    /// </summary>
    /// <param name="sandbox">Path confine shared by every file-touching tool; must not be <see langword="null"/>.
    /// <para>所有文件类工具共用的路径限制；不得为 <see langword="null"/>。</para></param>
    /// <param name="allowFlash">Registers the flashing/wiping/reboot tools when <see langword="true"/>.
    /// <para>为 <see langword="true"/> 时注册刷写、擦除、重启类工具。</para></param>
    /// <param name="deviceManager">Device gateway used by device tools; <see langword="null"/> selects the standalone gateway that owns device access without the desktop host.
    /// <para>设备工具使用的设备网关；为 <see langword="null"/> 时使用在无桌面宿主下自行持有设备访问的独立网关。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="sandbox"/> is <see langword="null"/>.
    /// <para><paramref name="sandbox"/> 为 <see langword="null"/>。</para></exception>
    public McpServerHost(PathSandbox sandbox, bool allowFlash, IDeviceManagerGateway? deviceManager = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        Sandbox = sandbox;
        AllowFlash = allowFlash;
        DeviceManager = deviceManager ?? new HeadlessDeviceManagerGateway();
    }

    private IDeviceManagerGateway DeviceManager { get; }

    /// <summary>Gets the sandbox all file-touching tools share. <para>所有文件工具共享的沙箱。</para></summary>
    /// <value>The instance built for this run, used to resolve and confine every client-supplied path.
    /// <para>本次运行所构建的实例，用于解析并限制客户端给出的所有路径。</para></value>
    public PathSandbox Sandbox { get; }

    /// <summary>Gets a value indicating whether flashing/wiping/reboot tools are registered.
    /// <para>获取是否注册刷写、擦除、重启类工具。</para></summary>
    /// <value><see langword="true"/> when this host also exposes device-mutating tools.
    /// <para>为该宿主同时暴露改动设备的工具时为 <see langword="true"/>。</para></value>
    public bool AllowFlash { get; }

    /// <summary>
    /// Builds the complete tool list for the current configuration.
    /// <para>按当前配置构建完整工具列表。</para>
    /// Exposed for tests: the flashing tools must appear only when allowed.
    /// <para>供测试使用：未授权时刷写类工具不得出现。</para>
    /// </summary>
    /// <returns>Every tool registered for this host, in firmware, compression, device, delivery, project, misc and environment order, with flashing tools appended only under <see cref="AllowFlash"/>.
    /// <para>本次宿主注册的全部工具，顺序为固件、压缩、设备、交付、工程、杂项与环境，且仅在 <see cref="AllowFlash"/> 允许时追加刷写类工具。</para></returns>
    public IReadOnlyList<McpServerTool> BuildTools()
    {
        var tools = new List<McpServerTool>();
        tools.AddRange(FirmwareToolFactory.CreateTools(Sandbox));
        tools.AddRange(CompressionToolFactory.CreateTools(Sandbox));
        tools.AddRange(DeviceToolFactory.CreateTools(Sandbox, DeviceManager));
        tools.AddRange(DeliveryToolFactory.CreateTools(Sandbox));
        tools.AddRange(ProjectToolFactory.CreateTools(Sandbox));
        tools.AddRange(MiscToolFactory.CreateTools(Sandbox));
        tools.AddRange(EnvToolFactory.CreateTools(Sandbox, AllowFlash));

        if (AllowFlash)
        {
            tools.AddRange(DeviceToolFactory.CreateFlashTools(Sandbox, DeviceManager));
        }

        return tools;
    }

    /// <summary>Builds the server options every transport (stdio, HTTP) shares.
    /// <para>构建各传输（stdio、HTTP）共用的服务器选项。</para></summary>
    internal static McpServerOptions CreateOptions(IReadOnlyList<McpServerTool> tools) => new()
    {
        ServerInfo = new Implementation
        {
            Name = "uotanstudio",
            Version = typeof(McpServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"
        },
        ToolCollection = [.. tools]
    };

    /// <summary>
    /// Runs the server until the client closes the stdio connection or cancellation fires.
    /// <para>运行服务器，直到客户端关闭 stdio 连接或取消触发。</para>
    /// </summary>
    /// <param name="cancellationToken">Stops the message loop; the transport and registered tools are left to unwind.
    /// <para>停止消息循环；传输与已注册工具由其自行收敛。</para></param>
    /// <returns>A task that completes when the stdio session ends. <para>stdio 会话结束时完成的任务。</para></returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var options = CreateOptions(BuildTools());

        var server = McpServer.Create(
            new StdioServerTransport("uotanstudio"),
            options);

        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}