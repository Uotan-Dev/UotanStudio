using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UotanStudio.Mcp.Resources;
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
    /// Assembles a host with a delivery tool-preparation provider; that provider's type is a
    /// delivery-project internal contract, so this overload stays visible only to this assembly and the
    /// desktop host granted through <c>InternalsVisibleTo</c>.
    /// <para>组装带交付工具准备提供器的宿主；该提供器类型是交付子项目的内部契约，因此此重载只对本程序集与
    /// 经 <c>InternalsVisibleTo</c> 授权的桌面宿主可见。</para>
    /// </summary>
    /// <param name="config">Sandbox root and flash gate for this run. <para>本次运行的沙箱根目录与刷写门控。</para></param>
    /// <param name="deviceManager">Device gateway used by device tools. <para>设备工具使用的设备网关。</para></param>
    /// <param name="deliveryTools">Tool-preparation provider enabling <c>delivery_build</c> on this host only.
    /// <para>仅对本宿主启用 <c>delivery_build</c> 的工具准备提供器。</para></param>
    /// <param name="signingKeys">Signing key provider used by <c>delivery_build</c>; <see langword="null"/> keeps signed plans failing honestly.
    /// <para><c>delivery_build</c> 使用的签名密钥供应器；为 <see langword="null"/> 时签名计划如实失败。</para></param>
    internal McpServerHost(McpServerConfig config, IDeviceManagerGateway? deviceManager,
        Func<Services.Delivery.IDeliveryTools?>? deliveryTools,
        Func<Services.Delivery.IDeliverySigningKeyProvider?>? signingKeys = null)
        : this(new PathSandbox(config.RootDirectory), config.AllowFlash, deviceManager)
    {
        DeliveryTools = deliveryTools;
        SigningKeys = signingKeys;
    }

    /// <summary>Assembles the host from an existing sandbox plus flash gate, so the HTTP exposure path registers exactly the same tool set.
    /// <para>用现成的沙箱与刷写门控组装宿主，供 HTTP 暴露路径复用同一套工具注册。</para></summary>
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

    /// <summary>
    /// Assembles a host from an existing sandbox, gate, device gateway and delivery tool-preparation
    /// provider; the provider's type is a delivery-project internal contract, so this overload stays
    /// visible only to this assembly and the desktop host granted through <c>InternalsVisibleTo</c>.
    /// <para>用现成沙箱、门控、设备网关与交付工具准备提供器组装宿主；提供器类型是交付子项目的内部契约，
    /// 因此此重载只对本程序集与经 <c>InternalsVisibleTo</c> 授权的桌面宿主可见。</para>
    /// </summary>
    /// <param name="sandbox">Path confine shared by every file-touching tool. <para>所有文件类工具共用的路径限制。</para></param>
    /// <param name="allowFlash">Registers the flashing/wiping/reboot tools when <see langword="true"/>. <para>为 <see langword="true"/> 时注册刷写、擦除、重启类工具。</para></param>
    /// <param name="deviceManager">Device gateway used by device tools. <para>设备工具使用的设备网关。</para></param>
    /// <param name="deliveryTools">Tool-preparation provider enabling <c>delivery_build</c> on this host only.
    /// <para>仅对本宿主启用 <c>delivery_build</c> 的工具准备提供器。</para></param>
    /// <param name="signingKeys">Signing key provider used by <c>delivery_build</c>; <see langword="null"/> keeps signed plans failing honestly.
    /// <para><c>delivery_build</c> 使用的签名密钥供应器；为 <see langword="null"/> 时签名计划如实失败。</para></param>
    internal McpServerHost(PathSandbox sandbox, bool allowFlash, IDeviceManagerGateway? deviceManager,
        Func<Services.Delivery.IDeliveryTools?>? deliveryTools,
        Func<Services.Delivery.IDeliverySigningKeyProvider?>? signingKeys = null)
        : this(sandbox, allowFlash, deviceManager)
    {
        DeliveryTools = deliveryTools;
        SigningKeys = signingKeys;
    }

    /// <summary>
    /// This host's delivery tool-preparation provider; visible only to this assembly and the granted
    /// desktop host because its payload type is a delivery-project internal contract.
    /// <para>本宿主的交付工具准备提供器；只对本程序集与经授权的桌面宿主可见。</para>
    /// </summary>
    /// <value>The provider supplied at construction, or <see langword="null"/>. <para>构造时给出的提供器，或 <see langword="null"/>。</para></value>
    internal Func<Services.Delivery.IDeliveryTools?>? DeliveryTools { get; private set; }

    /// <summary>
    /// <para>本宿主的交付签名密钥供应器，决定 <c>delivery_build</c> 能否产出签名输出。</para>
    /// This host's delivery signing key provider, deciding whether <c>delivery_build</c> can produce signed output.</summary>
    /// <value>The provider supplied at construction, or <see langword="null"/>. <para>构造时给出的供应器，或 <see langword="null"/>。</para></value>
    internal Func<Services.Delivery.IDeliverySigningKeyProvider?>? SigningKeys { get; private set; }

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
    /// <returns>Every tool registered for this host, in firmware, compression, device, delivery, OTA,
    /// project, misc and environment order, with flashing tools appended only under <see cref="AllowFlash"/>.
    /// <para>本次宿主注册的全部工具，顺序为固件、压缩、设备、交付、OTA、工程、杂项与环境，且仅在
    /// <see cref="AllowFlash"/> 允许时追加刷写类工具。</para></returns>
    public IReadOnlyList<McpServerTool> BuildTools()
    {
        var context = new PluginToolContext(Sandbox, DeviceManager, DeliveryTools, SigningKeys, AllowFlash);
        var tools = new List<McpServerTool>();
        foreach (var descriptor in PluginToolCatalog.All)
        {
            if (!descriptor.FlashGated || AllowFlash)
            {
                tools.Add(descriptor.Create(context));
            }
        }

        return tools;
    }

    /// <summary>
    /// Builds the resource collection every transport shares, so a client can inspect containers,
    /// images and filesystem entries without asking a tool to unpack them first.
    /// <para>构建各传输共用的资源集合，让客户端无需先调用解包工具即可检查容器、镜像与文件系统条目。</para>
    /// </summary>
    /// <returns>Resources and resource templates confined to <see cref="Sandbox"/>. <para>限制在 <see cref="Sandbox"/> 内的资源与资源模板。</para></returns>
    public IReadOnlyList<McpServerResource> BuildResources() => ContainerResourceFactory.CreateResources(Sandbox);

    /// <summary>Builds the server options every transport (stdio, HTTP) shares.
    /// <para>构建各传输（stdio、HTTP）共用的服务器选项。</para></summary>
    internal static McpServerOptions CreateOptions(IReadOnlyList<McpServerTool> tools) => CreateOptions(tools, null);

    /// <summary>
    /// Builds the shared server options, declaring the resources capability when resources are supplied.
    /// <para>构建各传输共用的服务器选项，并在给出资源时同时声明 Resources 能力。</para>
    /// </summary>
    /// <param name="tools">Tool set for this host. <para>本次宿主的工具集合。</para></param>
    /// <param name="resources">Resource collection for this host; <see langword="null"/> keeps the server tools-only.
    /// <para>本次宿主的资源集合；为 <see langword="null"/> 时服务器仍只暴露工具。</para></param>
    /// <returns>The options passed to <see cref="McpServer.Create"/>. <para>传给 <see cref="McpServer.Create"/> 的选项。</para></returns>
    internal static McpServerOptions CreateOptions(
        IReadOnlyList<McpServerTool> tools,
        IReadOnlyList<McpServerResource>? resources)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "uotanstudio",
                Version = typeof(McpServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"
            },
            ToolCollection = [.. tools],
            ServerInstructions = "UotanStudio firmware workbench. Read structure with resources before "
                + "calling tools: uotan://container/{path}/entries and uotan://filesystem/{path}/entries "
                + "list inner partitions, ZIP members and filesystem entries without extracting, and "
                + "uotan://image/{path}/metadata reports format headers only. Tools then act on those "
                + "names: unpack_firmware/pack_firmware convert whole images, extract_container_entries "
                + "writes selected entries, delivery_* build flashable outputs, and ota_* patch or rebuild "
                + "OTA ZIPs. Every client-supplied path is confined to the advertised sandbox root, "
                + "failures set isError with the underlying cause in the text, and long operations report "
                + "notifications/progress and honor cancellation."
        };

        if (resources is { Count: > 0 })
        {
            options.ResourceCollection = [.. resources];
            options.Capabilities = new ServerCapabilities
            {
                Resources = new ResourcesCapability { ListChanged = false, Subscribe = false }
            };
        }

        return options;
    }

    /// <summary>
    /// Runs the server until the client closes the stdio connection or cancellation fires.
    /// <para>运行服务器，直到客户端关闭 stdio 连接或取消触发。</para>
    /// </summary>
    /// <param name="cancellationToken">Stops the message loop; the transport and registered tools are left to unwind.
    /// <para>停止消息循环；传输与已注册工具由其自行收敛。</para></param>
    /// <returns>A task that completes when the stdio session ends. <para>stdio 会话结束时完成的任务。</para></returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var options = CreateOptions(BuildTools(), BuildResources());

        var server = McpServer.Create(
            new StdioServerTransport("uotanstudio"),
            options);

        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}