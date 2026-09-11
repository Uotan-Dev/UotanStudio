namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Describes optional device matching criteria.
/// <para>描述可选的设备匹配条件。</para>
/// </summary>
public sealed class UsbDeviceFilter
{
    /// <summary>
    /// Gets or sets the vendor identifier filter.
    /// <para>获取或设置厂商 ID 过滤条件。</para>
    /// </summary>
    public ushort? VendorId { get; set; }

    /// <summary>
    /// Gets or sets the product identifier filter.
    /// <para>获取或设置产品 ID 过滤条件。</para>
    /// </summary>
    public ushort? ProductId { get; set; }

    /// <summary>
    /// Gets or sets the serial number filter.
    /// <para>获取或设置序列号过滤条件。</para>
    /// </summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Gets or sets a substring that must appear in the device path.
    /// <para>获取或设置设备路径必须包含的子串。</para>
    /// </summary>
    public string? DevicePathContains { get; set; }

    /// <summary>Exact physical path for opening a selected device without opening its peers.</summary>
    public string? DevicePathEquals { get; set; }

    /// <summary>
    /// Gets or sets the backend family filter.
    /// <para>获取或设置后端类型过滤条件。</para>
    /// </summary>
    public UsbApiKind? SourceApiKind { get; set; }

    /// <summary>
    /// Gets or sets the required USB interface class code.
    /// <para>获取或设置要求的 USB 接口类代码。</para>
    /// </summary>
    public byte? InterfaceClass { get; set; }

    /// <summary>
    /// Gets or sets the required USB interface subclass code.
    /// <para>获取或设置要求的 USB 接口子类代码。</para>
    /// </summary>
    public byte? InterfaceSubClass { get; set; }

    /// <summary>
    /// Gets or sets the required USB interface protocol code.
    /// <para>获取或设置要求的 USB 接口协议代码。</para>
    /// </summary>
    public byte? InterfaceProtocol { get; set; }

    /// <summary>
    /// Gets or sets the required USB interface number.
    /// <para>获取或设置要求的 USB 接口编号。</para>
    /// Backends that can select an interface (Linux/macOS/libusb/HarmonyOS) prefer this
    /// interface when opening a session; on WinUSB the bound interface is determined by the
    /// driver, so mismatches are filtered out during projection.
    /// <para>可选择接口的后端（Linux/macOS/libusb/HarmonyOS）打开会话时优先使用该接口；
    /// WinUSB 的绑定接口由驱动决定，不匹配的设备会在投影阶段被过滤。</para>
    /// </summary>
    public byte? InterfaceNumber { get; set; }

    /// <summary>
    /// Gets or sets the required USB interface numbers — every listed interface must be
    /// present on the device.
    /// <para>获取或设置要求的 USB 接口编号列表——设备必须包含列表中的每一个接口。</para>
    /// The primary data interface stays in <see cref="InterfaceNumber"/>; this list covers
    /// additional interfaces that must also be claimed (e.g. CDC-ACM control + data).
    /// <para>主数据接口仍由 <see cref="InterfaceNumber"/> 指定；本列表涵盖同样需要声明的
    /// 附加接口（例如 CDC-ACM 控制 + 数据接口）。</para>
    /// </summary>
    public IReadOnlyList<byte>? InterfaceNumbers { get; set; }

    /// <summary>
    /// Gets or sets the required bulk IN endpoint address (bit 7 set), when a protocol
    /// layer must target a specific endpoint pair instead of the first bulk pair found
    /// (e.g. Rockchip loader on 0x82/0x02).
    /// <para>获取或设置要求的批量 IN 端点地址（bit 7 置位），用于协议层必须针对特定端点对
    /// 而非第一对 bulk 端点（例如 Rockchip loader 使用 0x82/0x02）的场景。</para>
    /// Backends that can select endpoints prefer an interface containing this endpoint;
    /// devices without it are filtered out during projection.
    /// <para>可选择端点的后端优先选择包含该端点的接口；不含该端点的设备会在投影阶段被过滤。</para>
    /// </summary>
    public byte? EndpointAddressIn { get; set; }

    /// <summary>
    /// Gets or sets the required bulk OUT endpoint address (bit 7 clear).
    /// <para>获取或设置要求的批量 OUT 端点地址（bit 7 清零）。</para>
    /// </summary>
    public byte? EndpointAddressOut { get; set; }

    /// <summary>
    /// Determines whether the supplied metadata matches this filter.
    /// <para>判断给定元数据是否匹配当前过滤器。</para>
    /// </summary>
    /// <param name="info">The device metadata to test. <para>待匹配的设备元数据。</para></param>
    /// <returns><c>true</c> if the device matches; otherwise, <c>false</c>. <para>匹配返回 <c>true</c>，否则返回 <c>false</c>。</para></returns>
    public bool Matches(UsbDeviceInfo info) => MatchesCore(info, false);

    internal bool MatchesBeforeOpen(UsbDeviceInfo info) => MatchesCore(info, true);

    private bool MatchesCore(UsbDeviceInfo info, bool beforeOpen)
    {
        if (!string.IsNullOrEmpty(DevicePathEquals) &&
            !string.Equals(DevicePathEquals, info.DevicePath, StringComparison.OrdinalIgnoreCase)) return false;
        if (VendorId.HasValue && info.VendorId != VendorId.Value)
        {
            return false;
        }

        if (ProductId.HasValue && info.ProductId != ProductId.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SerialNumber) &&
            !(beforeOpen && !string.IsNullOrEmpty(DevicePathEquals) && string.IsNullOrEmpty(info.SerialNumber)) &&
            !string.Equals(info.SerialNumber, SerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(DevicePathContains) &&
            (string.IsNullOrWhiteSpace(info.DevicePath) ||
             info.DevicePath.IndexOf(DevicePathContains, StringComparison.OrdinalIgnoreCase) < 0))
        {
            return false;
        }

        if (SourceApiKind.HasValue && info.SourceApiKind != SourceApiKind.Value)
        {
            return false;
        }

        if (InterfaceClass.HasValue && info.InterfaceClass != InterfaceClass.Value)
        {
            return false;
        }

        if (InterfaceSubClass.HasValue && info.InterfaceSubClass != InterfaceSubClass.Value)
        {
            return false;
        }

        if (InterfaceProtocol.HasValue && info.InterfaceProtocol != InterfaceProtocol.Value)
        {
            return false;
        }

        if (InterfaceNumber.HasValue &&
            !info.Interfaces.Any(i => i.InterfaceNumber == InterfaceNumber.Value))
        {
            return false;
        }

        if (InterfaceNumbers is { Count: > 0 } requestedInterfaces &&
            requestedInterfaces.Any(required =>
                !info.Interfaces.Any(i => i.InterfaceNumber == required)))
        {
            return false;
        }

        if (EndpointAddressIn.HasValue || EndpointAddressOut.HasValue)
        {
            var hasIn = EndpointAddressIn is byte eIn &&
                info.Interfaces.Any(i => i.Endpoints.Any(e => e.EndpointAddress == eIn));
            var hasOut = EndpointAddressOut is byte eOut &&
                info.Interfaces.Any(i => i.Endpoints.Any(e => e.EndpointAddress == eOut));
            if (EndpointAddressIn.HasValue && !hasIn)
            {
                return false;
            }

            if (EndpointAddressOut.HasValue && !hasOut)
            {
                return false;
            }
        }

        return true;
    }
}