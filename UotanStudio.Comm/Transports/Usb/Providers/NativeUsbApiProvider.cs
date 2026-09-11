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
            Backends =
            [
                new UsbBackendCapability { BackendName = "winusb", SupportsNativeAsyncIo = true, ResetReenumeratesDevice = false },
                new UsbBackendCapability { BackendName = "winusb-legacy", SupportsNativeAsyncIo = false, ResetReenumeratesDevice = false },
                new UsbBackendCapability { BackendName = "linux-usbfs", SupportsNativeAsyncIo = true, ResetReenumeratesDevice = true },
                // Apple's official IOUSBHost user-space framework: resetWithError:
                // re-enumerates the device (libusb darwin semantics), so the session
                // must be re-opened after a reset.
                // <para>苹果官方 IOUSBHost 用户空间框架：resetWithError: 会使设备重新
                // 枚举（libusb darwin 语义），复位后必须重开会话。</para>
                new UsbBackendCapability { BackendName = "macos-usbhost", SupportsNativeAsyncIo = false, ResetReenumeratesDevice = true },
                new UsbBackendCapability { BackendName = "macos-iokit", SupportsNativeAsyncIo = false, ResetReenumeratesDevice = false },
                new UsbBackendCapability { BackendName = "harmony-ddk", SupportsNativeAsyncIo = false, ResetReenumeratesDevice = true }
            ],
        };
    }

    protected override List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WinUSBFinder.FindDevice(filter, new UsbSessionOptions { MetadataOnly = true });
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return LinuxUsbFinder.FindDevice(filter);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Preferred backend: Apple's official IOUSBHost user-space framework
            // (ObjC classes, driven via the ObjC runtime — no bridge library). The
            // IOUSBHost main binary lives in the dyld shared cache and dlopens
            // successfully on current macOS, so the former "framework is unloadable"
            // note no longer holds. Availability is re-checked on every enumeration
            // (never cached negatively); when the classes are absent the classic
            // IOKit plug-in API (IOServiceMatching + IOCreatePlugInInterfaceForService,
            // aligned with Google adb/fastboot usb_osx.cc) is used as the fallback.
            // <para>首选后端：苹果官方 IOUSBHost 用户空间框架（ObjC 类，经 ObjC 运行时
            // 驱动——无桥接库）。IOUSBHost 主二进制位于 dyld 共享缓存，当前 macOS 上
            // dlopen 成功，故旧有"框架不可加载"的说明不再成立。可用性在每次枚举时
            // 重新检查（绝不缓存负结果）；类不可加载时回退到 IOKit 经典插件 API
            // （IOServiceMatching + IOCreatePlugInInterfaceForService，与谷歌
            // adb/fastboot usb_osx.cc 对齐）。</para>
            var usbHostDevices = UsbHostFinder.FindDevice(filter);
            if (UsbHostFinder.LastUsbHostAvailable)
            {
                // Classes available: empty result means no devices, not absence —
                // do not fall back to IOKit classic in that case.
                // <para>类可用：空结果表示无设备而非缺失——此时不回退 IOKit 经典。</para>
                return usbHostDevices;
            }

            return IOKitUsbFinder.FindDevice(filter);
        }

        return [];
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
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WinUSBFinder.FindDevice(filter, options)
            : base.EnumerateBackendDevices(filter, options);
    }
}