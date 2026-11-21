namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Result of probing one hotplug-identified USB device.
/// <para>热插拔事件命中的单台 USB 设备的探测结果。</para>
/// </summary>
/// <remarks>
/// A backend distinguishes "cannot answer from one device" from "one device was checked and is absent";
/// only the former forces a full enumeration. This keeps a stale event from silently deleting a device.
/// <para>后端区分“无法只凭一台设备作答”与“已检查单台设备但其不在位”；只有前者才强制全量枚举，
/// 避免过期事件静默删除仍在位的设备。</para>
/// </remarks>
internal readonly record struct UsbDeviceProbeResult(bool IsHandled, UsbDeviceInfo? Device)
{
    /// <summary>Gets a result meaning the backend cannot probe this hint incrementally.
    /// <para>获取表示后端无法增量探测该提示的结果。</para></summary>
    public static UsbDeviceProbeResult Unhandled => new(false, null);

    /// <summary>Gets a result meaning the hinted device is absent.
    /// <para>获取表示被提示设备已不在位的结果。</para></summary>
    public static UsbDeviceProbeResult Absent => new(true, null);

    /// <summary>
    /// Creates a handled result carrying the probed device metadata.
    /// <para>创建携带已探测设备元数据的已处理结果。</para>
    /// </summary>
    /// <param name="device">The probed metadata. <para>探测到的元数据。</para></param>
    /// <returns>The handled result. <para>已处理结果。</para></returns>
    public static UsbDeviceProbeResult Found(UsbDeviceInfo device) =>
        new(true, device ?? throw new ArgumentNullException(nameof(device)));
}