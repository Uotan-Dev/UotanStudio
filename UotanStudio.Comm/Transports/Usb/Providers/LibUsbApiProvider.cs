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
            // libusb Reset maps to libusb_reset_device (USBDEVFS_RESET): the device
            // re-enumerates, so the session must be discarded and re-opened.
            // <para>libusb 的 Reset 映射到 libusb_reset_device（USBDEVFS_RESET）：
            // 设备会重新枚举，会话必须丢弃并重新打开。</para>
            ResetReenumeratesDevice = true,
            Notes = "LibUsbDotNet requires the native libusb runtime; async access uses the upstream libusb async API where available and hot-plug monitoring is polling-based. The runtime is bundled per-RID in the package, or an explicit library path can be supplied via UsbCommunicationLayer.SetLibusbLibraryPath."
        };
    }
}