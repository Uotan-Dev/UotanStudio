namespace UotanStudio.Abstractions.Devices;

/// <summary>
/// <para>已识别设备的运行时信息快照。</para>
/// Runtime information snapshot of an identified device.
/// </summary>
public sealed class DeviceInfo
{
    /// <summary>
    /// <para>设备唯一标识（如 ADB serial、fastboot serial、USB VID:PID 等）。</para>
    /// Unique device identifier (e.g. ADB serial, fastboot serial, USB VID:PID).
    /// </summary>
    public string Serial { get; init; } = string.Empty;

    /// <summary>
    /// <para>当前通讯协议。</para>
    /// Current communication protocol.
    /// </summary>
    public DeviceProtocol Protocol { get; set; } = DeviceProtocol.Unknown;

    /// <summary>
    /// <para>当前连接状态。</para>
    /// Current connection state.
    /// </summary>
    public DeviceConnectionState State { get; set; } = DeviceConnectionState.Disconnected;

    /// <summary>
    /// <para>设备制造商（如 <c>Xiaomi</c>、<c>Qualcomm</c>）。</para>
    /// Device manufacturer (e.g. <c>Xiaomi</c>, <c>Qualcomm</c>).
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// <para>设备型号（如 <c>2304FP19DG</c>）。</para>
    /// Device model (e.g. <c>2304FP19DG</c>).
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// <para>设备产品名。</para>
    /// Device product name.
    /// </summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>
    /// <para>主板代号。</para>
    /// Board codename.
    /// </summary>
    public string Board { get; set; } = string.Empty;

    /// <summary>
    /// <para>Android 版本（仅 ADB 模式有效）。</para>
    /// Android version (valid only in ADB mode).
    /// </summary>
    public string AndroidVersion { get; set; } = string.Empty;

    /// <summary>
    /// <para>当前 slot（A/B 设备，如 <c>a</c>、<c>b</c>）。</para>
    /// Current slot (A/B devices, e.g. <c>a</c>, <c>b</c>).
    /// </summary>
    public string CurrentSlot { get; set; } = string.Empty;

    /// <summary>
    /// <para>USB VID:PID（如 <c>18d1:4ee7</c>）。</para>
    /// USB VID:PID (e.g. <c>18d1:4ee7</c>).
    /// </summary>
    public string UsbVidPid { get; set; } = string.Empty;

    /// <summary>
    /// <para>设备能力描述。</para>
    /// Device capability descriptor.
    /// </summary>
    public DeviceCapabilities Capabilities { get; } = new();

    /// <summary>
    /// <para>最后一次状态更新的本地时间戳。</para>
    /// Local timestamp of the last state update.
    /// </summary>
    public long LastUpdatedTicks { get; set; }
}
