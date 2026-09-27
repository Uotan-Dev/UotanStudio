namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Describes a discovered USB device.
/// <para>描述已发现的 USB 设备。</para>
/// </summary>
public sealed class UsbDeviceInfo
{
    /// <summary>
    /// Gets or sets the public API name.
    /// <para>获取或设置对外 API 名称。</para>
    /// </summary>
    /// <value>The display name of the backend API that produced this entry; empty when unknown.
    /// <para>产生该条目的后端 API 显示名称；未知时为空。</para></value>
    public string ApiName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the backend family.
    /// <para>获取或设置后端类型。</para>
    /// </summary>
    /// <value>The <see cref="UsbApiKind"/> backend family that discovered this device.
    /// <para>发现该设备的 <see cref="UsbApiKind"/> 后端族。</para></value>
    public UsbApiKind SourceApiKind { get; set; }

    /// <summary>
    /// Gets or sets the concrete source device type.
    /// <para>获取或设置具体来源设备类型。</para>
    /// </summary>
    /// <value>The concrete backend device type name for diagnostics; empty when unavailable.
    /// <para>用于诊断的具体后端设备类型名称；不可用时为空。</para></value>
    public string SourceDeviceType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device path.
    /// <para>获取或设置设备路径。</para>
    /// </summary>
    /// <value>The OS device path used to open this device; empty when the backend does not expose one.
    /// <para>用于打开该设备的操作系统设备路径；后端未暴露时为空。</para></value>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stable identity key for the device.
    /// <para>获取或设置设备的稳定标识键。</para>
    /// </summary>
    /// <value>The identity key kept stable across replug and mode switches so events can be correlated.
    /// <para>在重新插拔和模式切换后保持稳定的身份键，用于关联事件。</para></value>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the serial number.
    /// <para>获取或设置设备序列号。</para>
    /// </summary>
    /// <value>The device serial number, or <see langword="null"/> when the backend does not report one.
    /// <para>设备序列号；后端未报告时为 <see langword="null"/>。</para></value>
    public string? SerialNumber { get; set; }

    /// <summary>OS-provided physical container identity, when available.
    /// <para>操作系统提供的物理容器标识（若可用）。</para></summary>
    /// <value>The container id grouping interfaces of the same physical device, or <see langword="null"/>.
    /// <para>将同一物理设备的各接口归组的容器 ID，或 <see langword="null"/>。</para></value>
    public Guid? ContainerId { get; set; }

    /// <summary>Gets or sets the backend-observed USB bus and physical port chain.
    /// <para>获取或设置后端观测到的 USB 总线与物理端口链。</para></summary>
    /// <value>An opaque backend-scoped location, or null when unavailable. This identifies a connection
    /// position, not a device or cryptographic identity, and need not survive host or hub topology changes.
    /// <para>后端范围内的不透明位置，不可用时为 null。它标识连接位置，而非设备或密码学身份，
    /// 不保证在主机或集线器拓扑变化后保持不变。</para></value>
    public string? PhysicalPortPath { get; set; }

    /// <summary>OS device-instance identity for this USB interface, when available.
    /// <para>该 USB 接口的操作系统设备实例标识（若可用）。</para></summary>
    /// <value>The OS device-instance id, or <see langword="null"/> when the backend does not expose one.
    /// <para>操作系统设备实例 ID；后端未暴露时为 <see langword="null"/>。</para></value>
    public string? DeviceInstanceId { get; set; }

    /// <summary>
    /// Gets or sets the vendor identifier.
    /// <para>获取或设置厂商 ID。</para>
    /// </summary>
    /// <value>The USB-IF assigned vendor identifier of the device.
    /// <para>设备的 USB-IF 分配厂商 ID。</para></value>
    public ushort VendorId { get; set; }

    /// <summary>
    /// Gets or sets the product identifier.
    /// <para>获取或设置产品 ID。</para>
    /// </summary>
    /// <value>The vendor-assigned product identifier of the device.
    /// <para>设备由厂商分配的产品 ID。</para></value>
    public ushort ProductId { get; set; }

    /// <summary>
    /// Gets or sets the USB interface class code when available.
    /// <para>获取或设置 USB 接口类代码（若可用）。</para>
    /// </summary>
    /// <value>The class code of the matched interface, or <see langword="null"/> when not observed.
    /// <para>匹配接口的类代码；未观测到时为 <see langword="null"/>。</para></value>
    public byte? InterfaceClass { get; set; }

    /// <summary>
    /// Gets or sets the USB interface subclass code when available.
    /// <para>获取或设置 USB 接口子类代码（若可用）。</para>
    /// </summary>
    /// <value>The subclass code of the matched interface, or <see langword="null"/> when not observed.
    /// <para>匹配接口的子类代码；未观测到时为 <see langword="null"/>。</para></value>
    public byte? InterfaceSubClass { get; set; }

    /// <summary>
    /// Gets or sets the USB interface protocol code when available.
    /// <para>获取或设置 USB 接口协议代码（若可用）。</para>
    /// </summary>
    /// <value>The protocol code of the matched interface, or <see langword="null"/> when not observed.
    /// <para>匹配接口的协议代码；未观测到时为 <see langword="null"/>。</para></value>
    public byte? InterfaceProtocol { get; set; }

    /// <summary>
    /// Gets or sets whether the interface metadata was observed from the backend rather than inferred from filter criteria.
    /// <para>获取或设置接口元数据是否由后端真实观测得到，而非由过滤条件推断得到。</para>
    /// </summary>
    /// <value><c>true</c> when the interface codes come from backend observation; <c>false</c> when inferred.
    /// <para>接口代码来自后端观测时为 <c>true</c>；由过滤条件推断时为 <c>false</c>。</para></value>
    public bool InterfaceMetadataObserved { get; set; }

    /// <summary>
    /// Gets or sets the USB transfer speed reported by the backend, when available.
    /// <para>获取或设置后端报告的 USB 传输速度（若可用）。</para>
    /// </summary>
    /// <value>The reported <see cref="UsbDeviceSpeed"/>; <see cref="UsbDeviceSpeed.Unknown"/> when unreported.
    /// <para>后端报告的 <see cref="UsbDeviceSpeed"/>；未报告时为 <see cref="UsbDeviceSpeed.Unknown"/>。</para></value>
    public UsbDeviceSpeed Speed { get; set; }

    /// <summary>
    /// Gets or sets the interfaces (and their endpoints) observed for this device.
    /// <para>获取或设置观测到的设备接口（及其端点）列表。</para>
    /// </summary>
    /// <value>The observed interfaces; empty when the backend reports no interface metadata.
    /// <para>观测到的接口列表；后端未报告接口元数据时为空。</para></value>
    public IReadOnlyList<UsbInterfaceInfo> Interfaces { get; set; } = [];
}