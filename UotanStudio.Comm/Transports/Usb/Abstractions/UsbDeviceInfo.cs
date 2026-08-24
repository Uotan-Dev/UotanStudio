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
    public string ApiName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the backend family.
    /// <para>获取或设置后端类型。</para>
    /// </summary>
    public UsbApiKind SourceApiKind { get; set; }

    /// <summary>
    /// Gets or sets the concrete source device type.
    /// <para>获取或设置具体来源设备类型。</para>
    /// </summary>
    public string SourceDeviceType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device path.
    /// <para>获取或设置设备路径。</para>
    /// </summary>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stable identity key for the device.
    /// <para>获取或设置设备的稳定标识键。</para>
    /// </summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the serial number.
    /// <para>获取或设置设备序列号。</para>
    /// </summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Gets or sets the vendor identifier.
    /// <para>获取或设置厂商 ID。</para>
    /// </summary>
    public ushort VendorId { get; set; }

    /// <summary>
    /// Gets or sets the product identifier.
    /// <para>获取或设置产品 ID。</para>
    /// </summary>
    public ushort ProductId { get; set; }

    /// <summary>
    /// Gets or sets the USB interface class code when available.
    /// <para>获取或设置 USB 接口类代码（若可用）。</para>
    /// </summary>
    public byte? InterfaceClass { get; set; }

    /// <summary>
    /// Gets or sets the USB interface subclass code when available.
    /// <para>获取或设置 USB 接口子类代码（若可用）。</para>
    /// </summary>
    public byte? InterfaceSubClass { get; set; }

    /// <summary>
    /// Gets or sets the USB interface protocol code when available.
    /// <para>获取或设置 USB 接口协议代码（若可用）。</para>
    /// </summary>
    public byte? InterfaceProtocol { get; set; }

    /// <summary>
    /// Gets or sets whether the interface metadata was observed from the backend rather than inferred from filter criteria.
    /// <para>获取或设置接口元数据是否由后端真实观测得到，而非由过滤条件推断得到。</para>
    /// </summary>
    public bool InterfaceMetadataObserved { get; set; }

    /// <summary>
    /// Gets or sets the USB transfer speed reported by the backend, when available.
    /// <para>获取或设置后端报告的 USB 传输速度（若可用）。</para>
    /// </summary>
    public UsbDeviceSpeed Speed { get; set; }

    /// <summary>
    /// Gets or sets the interfaces (and their endpoints) observed for this device.
    /// <para>获取或设置观测到的设备接口（及其端点）列表。</para>
    /// </summary>
    public IReadOnlyList<UsbInterfaceInfo> Interfaces { get; set; } = [];
}