using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using UotanStudio.Comm.Transports.Usb.Backend.MacOS;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;

namespace UotanStudio.Comm.Transports.Usb.Providers;

internal sealed class NativeUsbApiProvider : UsbApiProviderBase
{
    private static readonly Lazy<bool> IsHarmonyOS =
        new(HarmonyOSUsbDDK.IsHarmonyOSPlatform, isThreadSafe: true);

    public const string ApiNameConst = "native";

    public override string ApiName => ApiNameConst;

    public override UsbApiKind ApiKind => UsbApiKind.Native;

    public override bool IsSupportedOnCurrentPlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public override UsbApiCapabilities GetCapabilities()
    {
        var isHarmonyOS = IsHarmonyOS.Value;
        var notes = "Native transport is synchronous; async access is currently adapter-based and hot-plug monitoring is polling-based.";

        if (isHarmonyOS)
        {
            notes = "HarmonyOS detected; use the dedicated 'harmony' provider for USBManager IPC bridge, or this native provider for direct usbfs access.";
        }

        return new UsbApiCapabilities
        {
            ApiName = ApiName,
            ApiKind = ApiKind,
            IsSupportedOnCurrentPlatform = IsSupportedOnCurrentPlatform,
            SupportsNativeDiscovery = true,
            SupportsDeviceSessions = true,
            SupportsControlTransfers = true,
            SupportsInterfaceConfigSwitching = true,
            SupportsNativeAsyncIo = false,
            SupportsNativeHotPlugMonitoring = false,
            RequiresExternalRuntime = false,
            // The native provider spans platform backends whose Reset semantics differ:
            // WinUSB/legacy/macOS are pipe-level (session stays valid), while Linux usbfs
            // and HarmonyOS DDK reset the device/DDK session (must re-open). The top-level
            // flag is therefore the conservative "true" (at least one backend re-enumerates).
            // <para>native 提供器覆盖多个平台后端，其 Reset 语义不同：
            // WinUSB/legacy/macOS 为管道级（会话保持有效），而 Linux usbfs 与 HarmonyOS DDK
            // 重置设备/DDK 会话（必须重新打开）。顶层标志因此取保守的 true
            // （至少一个后端会重新枚举）。</para>
            ResetReenumeratesDevice = true,
            Notes = notes,
            Backends = new[]
            {
                new UsbBackendCapability { BackendName = "winusb", SupportsNativeAsyncIo = true, ResetReenumeratesDevice = false },
                new UsbBackendCapability { BackendName = "winusb-legacy", SupportsNativeAsyncIo = false, ResetReenumeratesDevice = false },
                new UsbBackendCapability { BackendName = "linux-usbfs", SupportsNativeAsyncIo = true, ResetReenumeratesDevice = true },
                new UsbBackendCapability { BackendName = "macos-iokit", SupportsNativeAsyncIo = false, ResetReenumeratesDevice = false },
                new UsbBackendCapability { BackendName = "harmony-ddk", SupportsNativeAsyncIo = false, ResetReenumeratesDevice = true }
            }
        };
    }

    protected override List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WinUSBFinder.FindDevice(filter);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return LinuxUsbFinder.FindDevice(filter);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // The IOKit classic API (IOServiceMatching + IOCreatePlugInInterfaceForService,
            // aligned with Google adb/fastboot usb_osx.cc) is the ONLY macOS backend: it
            // lives in IOKit.framework and is loadable on every macOS release. The former
            // IOUSBHost (IOUSBLib) fallback was removed — its framework main binary is
            // absent on current macOS (only a BridgeSupport stub remains), so every
            // [DllImport(IOUSBHost)] throws DllNotFoundException.
            // <para>IOKit 经典 API（IOServiceMatching + IOCreatePlugInInterfaceForService，
            // 与谷歌 adb/fastboot usb_osx.cc 对齐）是唯一的 macOS 后端：它位于
            // IOKit.framework，在每个 macOS 发行版上均可加载。原 IOUSBHost（IOUSBLib）
            // 回退已移除——当前 macOS 上其框架主二进制缺失（仅剩 BridgeSupport 桩），
            // 故每个 [DllImport(IOUSBHost)] 都抛 DllNotFoundException。</para>
            return IOKitUsbFinder.FindDevice(filter);
        }

        return new List<UsbDevice>();
    }

    /// <summary>
    /// Options-aware backend enumeration: forwards <see cref="UsbSessionOptions"/> into the
    /// WinUSB finder on Windows so timeouts/RAW_IO take effect before a session handle is
    /// created; other platforms ignore the options (Linux usbfs / macOS IOKit do not expose
    /// pipe-level policies).
    /// <para>options 感知的后端枚举：在 Windows 上将 <see cref="UsbSessionOptions"/> 透传给
    /// WinUSB finder，使超时/RAW_IO 在会话句柄创建前生效；其他平台忽略选项
    /// （Linux usbfs / macOS IOKit 不暴露管道级策略）。</para>
    /// </summary>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options. <para>可选的会话调优选项。</para></param>
    /// <returns>The discovered backend devices. <para>发现的后端设备。</para></returns>
    protected override List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter, UsbSessionOptions? options)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WinUSBFinder.FindDevice(filter, options);
        }

        return base.EnumerateBackendDevices(filter, options);
    }
}
