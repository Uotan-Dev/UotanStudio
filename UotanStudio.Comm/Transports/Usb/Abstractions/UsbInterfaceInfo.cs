namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Describes the USB transfer speed of a device.
/// <para>描述 USB 设备的传输速度。</para>
/// </summary>
public enum UsbDeviceSpeed
{
    /// <summary>
    /// Speed is unknown or was not reported by the backend.
    /// <para>速度未知或后端未报告。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Low speed (USB 1.0, 1.5 Mbit/s).
    /// <para>低速（USB 1.0，1.5 Mbit/s）。</para>
    /// </summary>
    Low = 1,

    /// <summary>
    /// Full speed (USB 1.1, 12 Mbit/s).
    /// <para>全速（USB 1.1，12 Mbit/s）。</para>
    /// </summary>
    Full = 2,

    /// <summary>
    /// High speed (USB 2.0, 480 Mbit/s).
    /// <para>高速（USB 2.0，480 Mbit/s）。</para>
    /// </summary>
    High = 3,

    /// <summary>
    /// Super speed (USB 3.0, 5 Gbit/s).
    /// <para>超速（USB 3.0，5 Gbit/s）。</para>
    /// </summary>
    Super = 4,

    /// <summary>
    /// Super speed plus (USB 3.1+, 10 Gbit/s).
    /// <para>超速增强（USB 3.1+，10 Gbit/s）。</para>
    /// </summary>
    SuperPlus = 5
}

/// <summary>
/// Describes a single USB endpoint of an interface.
/// <para>描述接口的单个 USB 端点。</para>
/// </summary>
public sealed class UsbEndpointInfo
{
    /// <summary>
    /// Gets or sets the endpoint address (bit 7 = direction IN, bits 3-0 = number).
    /// <para>获取或设置端点地址（bit 7 = IN 方向，bit 3-0 = 端点号）。</para>
    /// </summary>
    /// <value>The raw endpoint address byte as reported by the backend.
    /// <para>后端报告的原始端点地址字节。</para></value>
    public byte EndpointAddress { get; set; }

    /// <summary>
    /// Gets or sets the endpoint attributes (bits 1-0 = transfer type: 0 control, 1 isochronous, 2 bulk, 3 interrupt).
    /// <para>获取或设置端点属性（bit 1-0 = 传输类型：0 控制、1 等时、2 批量、3 中断）。</para>
    /// </summary>
    /// <value>The raw endpoint attributes byte from the endpoint descriptor.
    /// <para>端点描述符中的原始属性字节。</para></value>
    public byte Attributes { get; set; }

    /// <summary>
    /// Gets or sets the maximum packet size in bytes.
    /// <para>获取或设置最大包大小（字节）。</para>
    /// </summary>
    /// <value>The maximum packet size in bytes for this endpoint.
    /// <para>该端点的最大包大小（字节）。</para></value>
    public ushort MaxPacketSize { get; set; }

    /// <summary>
    /// Gets or sets the polling interval in milliseconds for interrupt endpoints.
    /// <para>获取或设置中断端点的轮询间隔（毫秒）。</para>
    /// </summary>
    /// <value>The polling interval in milliseconds; meaningful only for interrupt endpoints.
    /// <para>轮询间隔（毫秒），仅对中断端点有意义。</para></value>
    public byte Interval { get; set; }

    /// <summary>
    /// Gets a value indicating whether this endpoint transfers data IN (device-to-host).
    /// <para>获取一个值，指示该端点是否为 IN 方向（设备到主机）。</para>
    /// </summary>
    /// <value><c>true</c> when bit 7 of <see cref="EndpointAddress"/> is set (IN, device-to-host);
    /// <c>false</c> for OUT (host-to-device).
    /// <para>当 <see cref="EndpointAddress"/> 的 bit 7 置位（IN，设备到主机）时为 <c>true</c>；
    /// OUT（主机到设备）时为 <c>false</c>。</para></value>
    public bool IsIn => (EndpointAddress & 0x80) != 0;
}

/// <summary>
/// Describes a USB interface of a device.
/// <para>描述设备的 USB 接口。</para>
/// </summary>
public sealed class UsbInterfaceInfo
{
    /// <summary>
    /// Gets or sets the interface number.
    /// <para>获取或设置接口编号。</para>
    /// </summary>
    /// <value>The interface number within the device configuration.
    /// <para>接口在设备配置中的编号。</para></value>
    public byte InterfaceNumber { get; set; }

    /// <summary>
    /// Gets or sets the interface class code.
    /// <para>获取或设置接口类代码。</para>
    /// </summary>
    /// <value>The USB base interface class code from the interface descriptor.
    /// <para>接口描述符中的 USB 基础接口类代码。</para></value>
    public byte Class { get; set; }

    /// <summary>
    /// Gets or sets the interface subclass code.
    /// <para>获取或设置接口子类代码。</para>
    /// </summary>
    /// <value>The subclass code refining the behaviour within <see cref="Class"/>.
    /// <para>在 <see cref="Class"/> 范围内细化行为的子类代码。</para></value>
    public byte SubClass { get; set; }

    /// <summary>
    /// Gets or sets the interface protocol code.
    /// <para>获取或设置接口协议代码。</para>
    /// </summary>
    /// <value>The protocol code selecting the interface protocol within the subclass.
    /// <para>在子类内选择接口协议的协议代码。</para></value>
    public byte Protocol { get; set; }

    /// <summary>
    /// Gets or sets the endpoints belonging to this interface.
    /// <para>获取或设置该接口的端点列表。</para>
    /// </summary>
    /// <value>The endpoints observed for this interface; empty when the backend reports none.
    /// <para>观测到的该接口端点列表；后端未报告时为空。</para></value>
    public IReadOnlyList<UsbEndpointInfo> Endpoints { get; set; } = [];
}