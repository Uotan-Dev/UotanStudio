using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

namespace UotanStudio.Comm.Transports.Usb.Providers;

internal sealed class LibUsbApiProvider : UsbApiProviderBase
{
    private readonly Lock _runtimeGate = new();
    private int _runtimeRevision = -1;
    private bool _runtimeAvailable;

    public const string ApiNameConst = "libusb";

    public override string ApiName => ApiNameConst;

    public override UsbApiKind ApiKind => UsbApiKind.LibUsbDotNet;

    public override bool IsSupportedOnCurrentPlatform => RuntimeAvailable;

    /// <summary>
    /// Gets whether the native runtime is usable, caching the probe result and re-running it
    /// whenever <see cref="LibUsbNativeLoader.Revision"/> changes so a library registered after
    /// an earlier failed probe enables the backend without a process restart.
    /// <para>获取原生运行时是否可用；缓存探测结果，并在
    /// <see cref="LibUsbNativeLoader.Revision"/> 变化时重新探测，使先前探测失败后注册的运行库
    /// 无需重启进程即可启用后端。</para>
    /// </summary>
    private bool RuntimeAvailable
    {
        get
        {
            lock (_runtimeGate)
            {
                var revision = LibUsbNativeLoader.Revision;
                if (revision != _runtimeRevision)
                {
                    _runtimeAvailable = LibUsbFinder.IsRuntimeAvailable(out _);
                    _runtimeRevision = LibUsbNativeLoader.Revision;
                }

                return _runtimeAvailable;
            }
        }
    }

    protected override List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter)
        => SafeEnumerate(() => LibUsbFinder.FindDevice(filter), "LibUsb");

    public override UsbApiCapabilities GetCapabilities()
    {
        var supportsNativeHotPlug = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        return new UsbApiCapabilities
        {
            ApiName = ApiName,
            ApiKind = ApiKind,
            IsSupportedOnCurrentPlatform = IsSupportedOnCurrentPlatform,
            SupportsNativeDiscovery = true,
            SupportsDeviceSessions = true,
            SupportsControlTransfers = true,
            SupportsInterfaceConfigSwitching = true,
            SupportsNativeAsyncIo = true,
            SupportsNativeHotPlugMonitoring = supportsNativeHotPlug,
            RequiresExternalRuntime = true,
            ResetReenumeratesDevice = true,
            Notes = "The backend is enabled only when the platform libusb file is present next to the application or an explicit path is registered via UsbCommunicationLayer.SetLibusbLibraryPath; a libusb installed elsewhere on the system does not enable it. Bulk, interrupt and control transfers use the libusb async transfer API. Hot-plug callbacks are used on Linux and macOS; Windows falls back to polling."
        };
    }
}