#if ANDROID
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Backend.Android;

namespace UotanStudio.Comm.Transports.Usb.Providers;

/// <summary>
/// USB Host provider backed by the Android framework (android.hardware.usb).
/// <para>基于 Android 框架（android.hardware.usb）的 USB Host 提供器。</para>
/// Enabled only on the net10.0-android runtime where the Java runtime bridge is present.
/// <para>仅在存在 Java 运行桥的 net10.0-android 运行时上启用。</para>
/// </summary>
internal sealed class AndroidUsbApiProvider : UsbApiProviderBase
{
    public const string ApiNameConst = "android";

    public override string ApiName => ApiNameConst;

    public override UsbApiKind ApiKind => UsbApiKind.AndroidUsbHost;

    // Compiled into the ANDROID build only, so the platform check is a constant.
    // <para>仅编入 ANDROID 构建，因此平台判定是常量。</para>
    public override bool IsSupportedOnCurrentPlatform => OperatingSystem.IsAndroid();

    protected override List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter)
        => SafeEnumerate(() => AndroidUsbFinder.FindDevice(filter), "AndroidUsb");

    public override UsbApiCapabilities GetCapabilities() => new()
    {
        ApiName = ApiName,
        ApiKind = ApiKind,
        IsSupportedOnCurrentPlatform = IsSupportedOnCurrentPlatform,
        SupportsNativeDiscovery = true,
        SupportsDeviceSessions = true,
        SupportsControlTransfers = true,
        // Android exposes only SET_INTERFACE-style selection via ClaimInterface; there is
        // no arbitrary SET_CONFIGURATION escape hatch on UsbDeviceConnection.
        // <para>Android 仅能通过 ClaimInterface 做接口选择；UsbDeviceConnection 没有
        // 任意 SET_CONFIGURATION 的通道。</para>
        SupportsInterfaceConfigSwitching = false,
        // bulkTransfer is synchronous; async runs on the thread pool like the legacy backend.
        // <para>bulkTransfer 是同步的；异步像旧式后端一样在线程池上运行。</para>
        SupportsNativeAsyncIo = false,
        SupportsNativeHotPlugMonitoring = false,
        RequiresExternalRuntime = false,
        // Reset() is a no-op: UsbDeviceConnection cannot reset the port.
        // <para>Reset() 是无操作：UsbDeviceConnection 无法复位端口。</para>
        ResetReenumeratesDevice = false,
        Notes = "Android USB Host backend (UsbManager/UsbDeviceConnection). Devices must be granted access via the UsbManager permission prompt per device. Hot-plug monitoring is polling-based; a port reset is not available on this backend."
    };
}
#endif