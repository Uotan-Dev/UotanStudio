namespace UotanStudio.Comm.Contracts;

/// <summary>
/// An exact route to one discovered transport endpoint, not a physical-device guess.
/// <para>指向单个已发现传输端点的精确路由，而不是对物理设备的猜测。</para>
/// </summary>
public sealed record DeviceEndpoint
{
    /// <summary>
    /// Name of the discovery source that reported this endpoint.
    /// <para>上报该端点的发现源名称。</para>
    /// </summary>
    /// <value>
    /// A required source identifier, such as the USB enumerator or a debug-bridge server source.
    /// A refresh rejects an endpoint whose source differs from the source that produced it.
    /// <para>必填的来源标识，例如 USB 枚举器或调试桥 server 发现源。刷新时会拒绝来源与产出该端点的
    /// 发现源不一致的端点。</para>
    /// </value>
    public required string Source { get; init; }
    /// <summary>
    /// Transport namespace the opener resolves when the endpoint is leased.
    /// <para>租用端点时由开启器解析的传输命名空间。</para>
    /// </summary>
    /// <value>
    /// A required non-blank key such as <c>adb-server</c>, <c>hdc-server</c>, or the name of the USB
    /// API kind that enumerated the device.
    /// <para>必填且非空的键，例如 <c>adb-server</c>、<c>hdc-server</c>，或枚举出该设备的 USB API
    /// 种类名称。</para>
    /// </value>
    public required string Transport { get; init; }
    /// <summary>
    /// Exact address of the endpoint inside its transport namespace.
    /// <para>端点在其传输命名空间内的精确地址。</para>
    /// </summary>
    /// <value>
    /// The server serial or connect key, or the USB device key; required and non-blank in a committed
    /// snapshot.
    /// <para>server 的 serial 或 connect key，或 USB 设备键；在已提交的快照中必填且非空。</para>
    /// </value>
    public required string Address { get; init; }
    /// <summary>
    /// Label the source suggests for user-visible listings.
    /// <para>发现源建议用于界面列表的显示名。</para>
    /// </summary>
    /// <value>
    /// Free-form display text, or <see langword="null"/> when the source exposes none.
    /// <para>自由文本形式的显示名；发现源未提供时为 <see langword="null"/>。</para>
    /// </value>
    public string? DisplayName { get; init; }
    /// <summary>
    /// Protocol observed by the source, when known.
    /// <para>发现源观察到的协议（若已知）。</para>
    /// </summary>
    /// <value>
    /// A protocol tag such as <c>adb</c> or <c>hdc</c>, or <see langword="null"/> when the source
    /// classified nothing.
    /// <para>协议标记，例如 <c>adb</c> 或 <c>hdc</c>；发现源未做出分类时为
    /// <see langword="null"/>。</para>
    /// </value>
    public string? Protocol { get; init; }
    /// <summary>
    /// Server or direct USB ownership; never inferred from a matching serial.
    /// <para>server 归属还是直连 USB 归属；绝不依据相匹配的 serial 推断。</para>
    /// </summary>
    /// <value>
    /// <c>server</c> when a debug-bridge server owns the target, <c>direct-usb</c> for a native USB
    /// open, or <see langword="null"/> when the source reports no ownership.
    /// <para>当调试桥 server 管理该目标时为 <c>server</c>，原生 USB 直连时为 <c>direct-usb</c>；
    /// 发现源未报告归属时为 <see langword="null"/>。</para>
    /// </value>
    public string? Route { get; init; }
    /// <summary>
    /// Target state text reported by the source.
    /// <para>发现源上报的目标状态文本。</para>
    /// </summary>
    /// <value>
    /// The state string as the server reported it, or <see langword="null"/> when the transport
    /// exposes no state.
    /// <para>server 原样上报的状态字符串；该传输不暴露状态时为 <see langword="null"/>。</para>
    /// </value>
    public string? State { get; init; }
    /// <summary>
    /// Serial or connect key exactly as the authoritative source reported it.
    /// <para>由权威发现源原样上报的 serial 或 connect key。</para>
    /// </summary>
    /// <value>
    /// The identity string used by server routing, or <see langword="null"/> when the endpoint exposes
    /// no serial. It is never mapped to a USB device by list position or VID/PID.
    /// <para>server 路由所使用的身份字符串；该端点未暴露 serial 时为 <see langword="null"/>。
    /// 不会按列表位置或 VID/PID 将其映射到某个 USB 设备。</para>
    /// </value>
    public string? Serial { get; init; }
    /// <summary>
    /// Server-reported target transport (for example USB or TCP), not an opening route.
    /// <para>server 上报的目标传输（例如 USB 或 TCP），并非开启路由。</para>
    /// </summary>
    /// <value>
    /// The target transport text as reported by the server, or <see langword="null"/> when unknown;
    /// it never selects how the transport is opened.
    /// <para>server 上报的目标传输文本；未知时为 <see langword="null"/>；它不会决定传输的开启方式。</para>
    /// </value>
    public string? TargetTransport { get; init; }
    /// <summary>
    /// Observed USB identity backing this endpoint.
    /// <para>支撑该端点的已观测 USB 身份。</para>
    /// </summary>
    /// <value>
    /// The immutable USB details used to validate a selected by-key open, or <see langword="null"/>
    /// for endpoints owned by a debug-bridge server.
    /// <para>用于校验按所选键开启的不可变 USB 详细信息；由调试桥 server 管理的端点为
    /// <see langword="null"/>。</para>
    /// </value>
    public UsbEndpointDetails? Usb { get; init; }
    /// <summary>
    /// A defensive copy for host projection; mutations never alter the snapshot.
    /// <para>供宿主投射的防御性副本；修改它永远不会影响快照。</para>
    /// </summary>
    /// <value>
    /// A copy of the native USB device information, or <see langword="null"/>. Both reads and
    /// inits copy, so the stored instance is never shared with the caller.
    /// <para>原生 USB 设备信息的副本，或 <see langword="null"/>。读取与初始化都会复制，
    /// 因此保存的实例不会与调用方共享。</para>
    /// </value>
    public UotanStudio.Comm.Transports.Usb.Abstractions.UsbDeviceInfo? UsbInfo
    {
        get => field is null ? null : UsbInfoCopy.Copy(field);
        init => field = value is null ? null : UsbInfoCopy.Copy(value);
    }

    /// <summary>
    /// Identity triple that leases this exact endpoint.
    /// <para>用于租用该精确端点的身份三元组。</para>
    /// </summary>
    /// <value>
    /// A <see cref="DeviceEndpointId"/> built from <see cref="Source"/>, <see cref="Transport"/> and
    /// <see cref="Address"/>; equal ids denote the same leaseable endpoint.
    /// <para>由 <see cref="Source"/>、<see cref="Transport"/> 与 <see cref="Address"/> 构成的
    /// <see cref="DeviceEndpointId"/>；相同的标识表示同一个可租用的端点。</para>
    /// </value>
    public DeviceEndpointId Id => new(Source, Transport, Address);
}

/// <summary>
/// Identity includes the discovery source and transport namespace.
/// <para>标识包含发现源与传输命名空间。</para>
/// </summary>
/// <param name="Source">
/// Name of the discovery source that reported the endpoint.
/// <para>上报该端点的发现源名称。</para>
/// </param>
/// <param name="Transport">
/// Transport namespace key the opener resolves.
/// <para>开启器所解析的传输命名空间键。</para>
/// </param>
/// <param name="Address">
/// Exact address of the endpoint inside that transport namespace.
/// <para>该端点在对应传输命名空间内的精确地址。</para>
/// </param>
public readonly record struct DeviceEndpointId(string Source, string Transport, string Address);