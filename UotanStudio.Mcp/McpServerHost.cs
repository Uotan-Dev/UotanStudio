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
    public McpServerHost(McpServerConfig config, IDeviceManagerGateway? deviceManager = null)
        : this(new PathSandbox(config.RootDirectory), config.AllowFlash, deviceManager)
    {
    }

    /// <summary>
    /// <para>用现成的沙箱与刷写门控组装宿主，供 HTTP 暴露路径复用同一套工具注册。</para>
    /// Assembles the host from an existing sandbox plus flash gate, so the HTTP exposure path
    /// registers exactly the same tool set.
    /// </summary>
    public McpServerHost(PathSandbox sandbox, bool allowFlash, IDeviceManagerGateway? deviceManager = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        Sandbox = sandbox;
        AllowFlash = allowFlash;
        DeviceManager = deviceManager ?? new HeadlessDeviceManagerGateway();
    }

    private IDeviceManagerGateway DeviceManager { get; }

    /// <summary>Gets the sandbox all file-touching tools share. <para>所有文件工具共享的沙箱。</para></summary>
    public PathSandbox Sandbox { get; }

    /// <summary>Gets a value indicating whether flashing/wiping/reboot tools are registered.
    /// <para>获取是否注册刷写、擦除、重启类工具。</para></summary>
    public bool AllowFlash { get; }

    /// <summary>
    /// Builds the complete tool list for the current configuration.
    /// <para>按当前配置构建完整工具列表。</para>
    /// Exposed for tests: the flashing tools must appear only when allowed.
    /// <para>供测试使用：未授权时刷写类工具不得出现。</para>
    /// </summary>
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
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var options = CreateOptions(BuildTools());

        var server = McpServer.Create(
            new StdioServerTransport("uotanstudio"),
            options);

        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}