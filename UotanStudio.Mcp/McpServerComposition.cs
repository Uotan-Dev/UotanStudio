namespace UotanStudio.Mcp;

/// <summary>
/// Everything one embedded server needs: the path sandbox, the flashing gate, the device gateway and the
/// delivery providers. One object replaces the constructor overloads, so a new capability extends the record
/// instead of adding another shape of <see cref="McpServerHost"/>.
/// <para>一个嵌入式服务器所需的全部内容：路径沙箱、刷写门控、设备网关与交付提供器。用单个对象替代构造函数重载，
/// 新增能力只扩展本记录，而不是再增加一种 <see cref="McpServerHost"/> 形态。</para>
/// </summary>
public sealed record McpServerComposition
{
    /// <summary>Gets the path confine shared by every file-touching tool.
    /// <para>获取所有文件类工具共用的路径限制。</para></summary>
    /// <value>The sandbox backing this host. <para>支撑本宿主的沙箱。</para></value>
    public required PathSandbox Sandbox { get; init; }

    /// <summary>Gets a value indicating whether flashing, wiping and reboot tools are registered.
    /// <para>获取是否注册刷写、擦除与重启类工具。</para></summary>
    /// <value><see langword="true"/> when the host also exposes device-mutating tools.
    /// <para>宿主同时暴露改动设备的工具时为 <see langword="true"/>。</para></value>
    public bool AllowFlash { get; init; }

    /// <summary>Gets the device gateway; <see langword="null"/> selects the standalone gateway.
    /// <para>获取设备网关；为 <see langword="null"/> 时使用独立网关。</para></summary>
    /// <value>The gateway serving device tools, or <see langword="null"/> for the headless default.
    /// <para>为设备工具服务的网关；为 <see langword="null"/> 时使用无头默认实现。</para></value>
    public Tools.IDeviceManagerGateway? DeviceManager { get; init; }

    /// <summary>Gets the delivery tool-preparation provider enabling <c>delivery_build</c>.
    /// <para>获取启用 <c>delivery_build</c> 的交付工具准备提供器。</para></summary>
    /// <value>The provider, or <see langword="null"/> to keep the delivery tool unavailable.
    /// <para>提供器；为 <see langword="null"/> 时交付工具不可用。</para></value>
    public Func<UotanStudio.Delivery.IDeliveryTools?>? DeliveryTools { get; init; }

    /// <summary>Gets the signing key provider used by signed delivery plans.
    /// <para>获取签名交付计划使用的签名密钥供应器。</para></summary>
    /// <value>The provider, or <see langword="null"/> so signed plans fail honestly.
    /// <para>供应器；为 <see langword="null"/> 时签名计划如实失败。</para></value>
    public Func<UotanStudio.Delivery.IDeliverySigningKeyProvider?>? SigningKeys { get; init; }
}