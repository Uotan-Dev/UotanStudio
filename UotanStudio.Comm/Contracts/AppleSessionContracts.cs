namespace UotanStudio.Comm.Contracts;

/// <summary>Describes how strongly the device trusts this host. <para>描述设备对本主机的信任程度。</para></summary>
public enum AppleTrustState
{
    /// <summary>No pair record exists for the device. <para>设备没有配对记录。</para></summary>
    Unpaired = 0,

    /// <summary>A pair record exists but the device rejected or reset the pairing. <para>存在配对记录但设备已拒绝或重置配对。</para></summary>
    Untrusted = 1,

    /// <summary>The device accepts services from this host. <para>设备接受本主机的服务请求。</para></summary>
    Trusted = 2,
}

/// <summary>
/// Information read from lockdownd about a connected Apple device.
/// <para>从 lockdownd 读取的已连接 Apple 设备信息。</para>
/// </summary>
/// <param name="Serial">The device UDID. <para>设备 UDID。</para></param>
/// <param name="DeviceId">The usbmuxd connection identifier. <para>usbmuxd 连接标识。</para></param>
/// <param name="Name">The user-visible device name. <para>用户可见的设备名称。</para></param>
/// <param name="ProductType">The hardware identifier, for example <c>iPhone15,3</c>. <para>硬件标识，例如 <c>iPhone15,3</c>。</para></param>
/// <param name="ProductVersion">The operating system version. <para>操作系统版本。</para></param>
/// <param name="ModelNumber">The model number reported by the device. <para>设备上报的型号编号。</para></param>
/// <param name="CpuArchitecture">The CPU architecture. <para>CPU 架构。</para></param>
/// <param name="WifiAddress">The Wi-Fi MAC address. <para>Wi-Fi MAC 地址。</para></param>
/// <param name="ConnectionType">The mux connection type, <c>USB</c> or <c>Network</c>. <para>复用连接类型，<c>USB</c> 或 <c>Network</c>。</para></param>
/// <param name="ActivationState">The activation state reported by the device. <para>设备上报的激活状态。</para></param>
/// <param name="BatteryPercent">The battery charge percentage, or <see langword="null"/> when unavailable. <para>电量百分比；不可用时为 <see langword="null"/>。</para></param>
/// <param name="TrustState">How strongly the device trusts this host. <para>设备对本主机的信任程度。</para></param>
public sealed record AppleDeviceInfo(
    string Serial,
    int DeviceId,
    string? Name,
    string? ProductType,
    string? ProductVersion,
    string? ModelNumber,
    string? CpuArchitecture,
    string? WifiAddress,
    string ConnectionType,
    string? ActivationState,
    int? BatteryPercent,
    AppleTrustState TrustState);

/// <summary>
/// Session facade over one connected Apple device: reads device information and opens lockdown
/// services as TLS-ready streams. Pairing is performed automatically the first time a service that
/// requires trust is opened; the device then shows the trust dialog.
/// <para>一台已连接 Apple 设备的会话门面：读取设备信息并以就绪 TLS 的流打开 lockdown 服务。
/// 首次打开需要信任的服务时自动执行配对，设备随后弹出信任对话框。</para>
/// </summary>
public interface IAppleDeviceSession : IDisposable
{
    /// <summary>Gets the protocol name. <para>获取协议名称。</para></summary>
    /// <value>The fixed tag <c>apple</c>. <para>固定标记 <c>apple</c>。</para></value>
    string ProtocolName { get; }

    /// <summary>Gets whether the session can currently reach the device. <para>获取会话当前能否到达设备。</para></summary>
    bool IsConnected { get; }

    /// <summary>Gets the device serial (UDID) this session is bound to. <para>获取会话绑定的设备序列号（UDID）。</para></summary>
    string Serial { get; }

    /// <summary>
    /// Verifies the device is reachable through the multiplexer.
    /// <para>确认设备可经由复用器到达。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the verification. <para>取消校验。</para></param>
    /// <returns>The usbmuxd device identifier for the device. <para>该设备的 usbmuxd 设备标识。</para></returns>
    /// <exception cref="IOException">The device is not reachable. <para>设备不可达时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    Task<int> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the device information from lockdownd; works without pairing for the basic values.
    /// <para>从 lockdownd 读取设备信息；基本取值无需配对即可读取。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The device information. <para>设备信息。</para></returns>
    /// <exception cref="IOException">The device or lockdown exchange failed. <para>设备或 lockdown 交换失败时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    Task<AppleDeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a lockdown service and returns the connected stream, TLS-upgraded when the device
    /// requires it. Pairing is performed automatically when a record is missing.
    /// <para>启动 lockdown 服务并返回已连接流；设备要求时已完成 TLS 升级。缺少配对记录时自动配对。</para>
    /// </summary>
    /// <param name="serviceName">The lockdown service name. <para>lockdown 服务名。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The service stream. <para>服务流。</para></returns>
    /// <exception cref="IOException">The service cannot be started, including the untrusted-device case. <para>服务无法启动（含设备未信任）时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    Stream OpenService(string serviceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a raw stream to a device TCP port (for example an SSH daemon); pairing is not required.
    /// <para>打开到设备 TCP 端口的原始流（例如 SSH 守护进程）；无需配对。</para>
    /// </summary>
    /// <param name="port">The device TCP port. <para>设备 TCP 端口。</para></param>
    /// <param name="cancellationToken">Cancels the connect. <para>取消连接。</para></param>
    /// <returns>The device byte stream. <para>设备字节流。</para></returns>
    /// <exception cref="IOException">The port was refused or the device is unreachable. <para>端口被拒或设备不可达时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    Stream OpenDevicePort(int port, CancellationToken cancellationToken = default);
}