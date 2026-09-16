using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

namespace UotanStudio.Comm.Transports.Usb.Providers;

internal sealed class LibUsbApiProvider : UsbApiProviderBase
{
    private static readonly Lazy<bool> RuntimeAvailable =
        new(() => LibUsbFinder.IsRuntimeAvailable(out _), isThreadSafe: true);

    public const string ApiNameConst = "libusb";

    public override string ApiName => ApiNameConst;

    public override UsbApiKind ApiKind => UsbApiKind.LibUsbDotNet;

    public override bool IsSupportedOnCurrentPlatform => RuntimeAvailable.Value;

    protected override List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter)
        => SafeEnumerate(() => LibUsbFinder.FindDevice(filter), "LibUsb");

    public override UsbApiCapabilities GetCapabilities()
    {
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
            SupportsNativeHotPlugMonitoring = false,
            RequiresExternalRuntime = true,
            ResetReenumeratesDevice = true,
            Notes = "LibUsbDotNet requires the native libusb runtime; async access uses the upstream libusb async API where available and hot-plug monitoring is polling-based. The runtime is bundled per-RID in the package, or an explicit library path can be supplied via UsbCommunicationLayer.SetLibusbLibraryPath."
        };
    }
}