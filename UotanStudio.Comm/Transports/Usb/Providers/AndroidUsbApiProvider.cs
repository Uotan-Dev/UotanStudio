using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Providers;

/// <summary>
/// OTG USB backend for Android devices: the libusb provider with Android as the supported target.
/// <para>Android OTG USB 后端：按 Android 目标启用的 libusb 提供器。</para>
/// </summary>
/// <remarks>
/// A libusb runtime must resolve on the device: either a <c>libusb-1.0.so.0</c> / <c>libusb-1.0.so</c>
/// next to the application or an explicitly registered path via
/// <see cref="UsbCommunicationLayer.SetLibusbLibraryPath(string)"/>. Hot-plug events are not exposed by
/// libusb on Android, so the polling monitor (<see cref="Transports.Usb.Core.UsbDeviceMonitor"/>) is used.
/// <para>设备上必须能解析一个 libusb 运行时：复制程序目录中的 <c>libusb-1.0.so.0</c> /
/// <c>libusb-1.0.so</c>，或经 <see cref="UsbCommunicationLayer.SetLibusbLibraryPath(string)"/> 注册显式路径。
/// libusb 在 Android 上不提供热插拔回调，轮询监视器（<see cref="Transports.Usb.Core.UsbDeviceMonitor"/>）作为回退。</para>
/// </remarks>
internal sealed class AndroidUsbApiProvider : LibUsbApiProvider
{
    public new const string ApiNameConst = "android";

    public override string ApiName => ApiNameConst;

    public override bool IsSupportedOnCurrentPlatform => PlatformDetection.IsAndroidOs;

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
            ResetReenumeratesDevice = false,
            Notes = "OTG USB via libusb: enable only when a libusb runtime resolves on the device (e.g. libusb-1.0.so.0 or libusb-1.0.so next to the application, or an explicit path via UsbCommunicationLayer.SetLibusbLibraryPath). Bulk reads and writes, interrupt and control transfers run through the libusb transfer API; hot-plug is reported as unavailable and the polling monitor is used.",
        };
    }
}