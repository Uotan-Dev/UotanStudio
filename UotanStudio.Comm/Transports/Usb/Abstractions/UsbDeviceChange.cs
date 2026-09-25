namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Describes a USB device change kind.
/// <para>描述 USB 设备变化类型。</para>
/// </summary>
public enum UsbDeviceChangeKind
{
    /// <summary>
    /// The device has been discovered.
    /// <para>设备被发现。</para>
    /// </summary>
    Added = 0,

    /// <summary>
    /// The device is absent.
    /// <para>设备已消失。</para>
    /// </summary>
    Removed = 1,

    /// <summary>
    /// The device is still present but its metadata changed (serial, interface class,
    /// speed, etc.) - e.g. a mode switch that keeps the identity key stable.
    /// <para>设备仍在但元数据发生变化（序列号、接口类、速度等）——例如身份键保持
    /// 稳定的模式切换。</para>
    /// </summary>
    Changed = 2
}

/// <summary>
/// Represents a single device change entry.
/// <para>表示单个设备变化条目。</para>
/// </summary>
public sealed class UsbDeviceChange
{
    /// <summary>
    /// Gets or sets the change kind.
    /// <para>获取或设置变化类型。</para>
    /// </summary>
    /// <value>Whether the device was added, removed, or its metadata changed.
    /// <para>表示设备是新发现、已消失，还是元数据发生变化。</para></value>
    public UsbDeviceChangeKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the device metadata.
    /// <para>获取或设置设备元数据。</para>
    /// </summary>
    /// <value>The device metadata snapshot carried with this change event.
    /// <para>随本条变化事件携带的设备元数据快照。</para></value>
    public UsbDeviceInfo Device { get; set; } = new();
}