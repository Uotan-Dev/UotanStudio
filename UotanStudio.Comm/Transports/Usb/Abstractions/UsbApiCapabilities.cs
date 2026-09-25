using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Describes the observable capability profile of a USB backend.
/// <para>描述 USB 后端可观察到的能力轮廓。</para>
/// </summary>
internal sealed class UsbApiCapabilities
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
    public UsbApiKind ApiKind { get; set; }

    /// <summary>
    /// Gets or sets whether this backend is available on the current platform.
    /// <para>获取或设置该后端在当前平台是否可用。</para>
    /// </summary>
    public bool IsSupportedOnCurrentPlatform { get; set; }

    /// <summary>
    /// Gets or sets whether device metadata discovery is available natively.
    /// <para>获取或设置是否原生支持设备元数据发现。</para>
    /// </summary>
    public bool SupportsNativeDiscovery { get; set; }

    /// <summary>
    /// Gets or sets whether the backend can open direct device sessions.
    /// <para>获取或设置后端是否可以打开直接设备会话。</para>
    /// </summary>
    public bool SupportsDeviceSessions { get; set; }

    /// <summary>
    /// Gets or sets whether the backend supports USB control transfers.
    /// <para>获取或设置后端是否支持 USB 控制传输。</para>
    /// </summary>
    public bool SupportsControlTransfers { get; set; }

    /// <summary>
    /// Gets or sets whether the backend can switch USB configurations and interface
    /// alternate settings (SET_CONFIGURATION / SET_INTERFACE).
    /// <para>获取或设置后端是否可切换 USB 配置与接口备用设置（SET_CONFIGURATION / SET_INTERFACE）。</para>
    /// Protocol layers that must select an interface alternate setting (e.g. CDC-ACM
    /// data interface, RNDIS) or a non-default configuration can check this capability
    /// before calling <see cref="IUsbDeviceSession.SetInterfaceAltSetting"/>.
    /// <para>必须选择接口备用设置（如 CDC-ACM 数据接口、RNDIS）或非默认配置的协议层，
    /// 可在调用 <see cref="IUsbDeviceSession.SetInterfaceAltSetting"/> 前检查该能力。</para>
    /// </summary>
    public bool SupportsInterfaceConfigSwitching { get; set; }

    /// <summary>
    /// Gets or sets whether asynchronous I/O is implemented natively by the backend.
    /// <para>获取或设置后端是否原生实现异步 I/O。</para>
    /// </summary>
    public bool SupportsNativeAsyncIo { get; set; }

    /// <summary>
    /// Gets or sets whether hot-plug notification is implemented natively.
    /// <para>获取或设置是否原生实现热插拔通知。</para>
    /// </summary>
    public bool SupportsNativeHotPlugMonitoring { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="IUsbDeviceSession.Reset"/> causes the device to
    /// re-enumerate, invalidating the session (the caller must re-open it).
    /// <para>获取或设置 <see cref="IUsbDeviceSession.Reset"/> 是否会导致设备重新枚举并使会话失效
    /// （调用方必须重新打开会话）。</para>
    /// <c>false</c> means a pipe-level reset that keeps the session usable (WinUSB, macOS,
    /// legacy); <c>true</c> means a device-level reset (Linux usbfs, libusb, HarmonyOS DDK).
    /// <para><c>false</c> 表示管道级复位，会话保持可用（WinUSB、macOS、legacy）；
    /// <c>true</c> 表示设备级复位（Linux usbfs、libusb、HarmonyOS DDK）。</para>
    /// </summary>
    public bool ResetReenumeratesDevice { get; set; }

    /// <summary>
    /// Gets or sets whether the backend requires an external runtime library.
    /// <para>获取或设置后端是否依赖外部运行时库。</para>
    /// </summary>
    public bool RequiresExternalRuntime { get; set; }

    /// <summary>
    /// Gets or sets optional notes about the backend profile.
    /// <para>获取或设置后端轮廓的可选说明。</para>
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Gets or sets per-backend capability details for providers that dispatch to multiple
    /// native backends (e.g. the "native" provider across Windows / Linux / macOS).
    /// <para>获取或设置多原生后端提供器（如 Windows / Linux / macOS 的 "native"）的逐后端能力详情。</para>
    /// </summary>
    public IReadOnlyList<UsbBackendCapability>? Backends { get; set; }
}

/// <summary>
/// Describes the capability of a single native backend within a multi-backend provider.
/// <para>描述多后端提供器内部单个原生后端的能力。</para>
/// </summary>
internal sealed class UsbBackendCapability
{
    /// <summary>
    /// Gets or sets the backend tag (matches <see cref="UsbTransferEvent.Backend"/>).
    /// <para>获取或设置后端标签（与 <see cref="UsbTransferEvent.Backend"/> 一致）。</para>
    /// </summary>
    public string BackendName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this backend implements asynchronous I/O natively.
    /// <para>获取或设置该后端是否原生实现异步 I/O。</para>
    /// </summary>
    public bool SupportsNativeAsyncIo { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="IUsbDeviceSession.Reset"/> re-enumerates the device
    /// on this specific backend (see <see cref="UsbApiCapabilities.ResetReenumeratesDevice"/>).
    /// <para>获取或设置该具体后端上 <see cref="IUsbDeviceSession.Reset"/> 是否重新枚举设备
    /// （参见 <see cref="UsbApiCapabilities.ResetReenumeratesDevice"/>）。</para>
    /// </summary>
    public bool ResetReenumeratesDevice { get; set; }
}

/// <summary>
/// Provides an explicit capability description for a USB API provider.
/// <para>为 USB API 提供器提供显式能力描述。</para>
/// </summary>
internal interface IUsbApiCapabilityProvider
{
    /// <summary>
    /// Gets the capability profile.
    /// <para>获取能力轮廓。</para>
    /// </summary>
    /// <returns>The provider capability profile. <para>提供器能力轮廓。</para></returns>
    UsbApiCapabilities GetCapabilities();
}