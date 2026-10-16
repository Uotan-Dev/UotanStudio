using ModelContextProtocol.Server;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// <para>Declarative description of one MCP tool: its protocol name, owning domain, the workflow-node
/// type key it mirrors when one exists, the safety annotations the protocol advertises, and whether it
/// is registered only under <c>--allow-flash</c>. The catalog projects these descriptors into the tool
/// list, so a new tool is declared once instead of being hand-appended to a builder method.</para>
/// <para>单个 MCP 工具的声明式描述：协议名称、所属领域、存在时对应的流程节点类型键、协议对外声明的安全标注，
/// 以及是否仅在 <c>--allow-flash</c> 下注册。目录据此投影出工具列表，新增工具只需声明一次，而不必手工追加到
/// 构建方法里。</para>
/// </summary>
/// <param name="Name">The protocol tool name, also the key of the emitted <c>inputSchema</c>. <para>协议工具名，也是 <c>inputSchema</c> 的键。</para></param>
/// <param name="Domain">The domain the tool belongs to, used for grouping and consistency assertions. <para>工具所属领域，用于分组与一致性断言。</para></param>
/// <param name="NodeTypeKey">The matching workflow-node type key, or <see langword="null"/> when the tool has no node counterpart. <para>对应的流程节点类型键；没有对应节点时为 <see langword="null"/>。</para></param>
/// <param name="ReadOnly">Whether the tool is advertised as read-only. <para>是否声明为只读工具。</para></param>
/// <param name="Destructive">Whether the tool is advertised as destructive. <para>是否声明为破坏性工具。</para></param>
/// <param name="FlashGated">Whether the tool is registered only under <c>--allow-flash</c>. <para>是否仅在 <c>--allow-flash</c> 下注册。</para></param>
/// <param name="Create">Builds the tool for one host context. <para>按宿主上下文构造该工具。</para></param>
internal sealed record PluginToolDescriptor(
    string Name,
    string Domain,
    string? NodeTypeKey,
    bool ReadOnly,
    bool Destructive,
    bool FlashGated,
    Func<PluginToolContext, McpServerTool> Create);

/// <summary>
/// <para>Per-host inputs every catalogued tool needs: the shared path sandbox, the device gateway, the
/// delivery tool-preparation and signing providers, and the flash gate.</para>
/// <para>每个目录工具都需要的宿主级输入：共享路径沙箱、设备网关、交付工具准备与签名提供器，以及刷写门控。</para>
/// </summary>
/// <param name="Sandbox">The sandbox confining every client path. <para>约束全部客户端路径的沙箱。</para></param>
/// <param name="DeviceManager">The gateway exposing device inventory and sessions. <para>访问设备清单与会话的网关。</para></param>
/// <param name="DeliveryTools">This host's tool-preparation provider, possibly <see langword="null"/>. <para>本宿主的工具准备服务提供器；可能为 <see langword="null"/>。</para></param>
/// <param name="SigningKeys">This host's delivery signing key provider, possibly <see langword="null"/>. <para>本宿主的交付签名密钥供应器；可能为 <see langword="null"/>。</para></param>
/// <param name="AllowFlash">Whether this host authorises flashing tools. <para>本宿主是否已授权刷写类工具。</para></param>
internal sealed record PluginToolContext(
    PathSandbox Sandbox,
    IDeviceManagerGateway DeviceManager,
    Func<Services.Delivery.IDeliveryTools?>? DeliveryTools,
    Func<Services.Delivery.IDeliverySigningKeyProvider?>? SigningKeys,
    bool AllowFlash);