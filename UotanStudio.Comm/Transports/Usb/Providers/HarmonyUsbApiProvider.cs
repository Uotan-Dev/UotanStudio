using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Providers;

/// <summary>
/// USB backend for HarmonyOS (OpenHarmony) hosts: the libusb provider with the HarmonyOS target.
/// <para>鸿蒙系统主机上的 USB 后端：按鸿蒙系统目标启用的 libusb 提供器。</para>
/// </summary>
/// <remarks>
/// A libusb runtime must be available on the OHOS device (registered path via
/// <see cref="UsbCommunicationLayer.SetLibusbLibraryPath(string)"/> or a resolvable copy). Hot-plug is
/// best-effort; the polling monitor is used when libusb hot-plug callbacks are unavailable.
/// <para>设备上必须提供可解析的 libusb 运行库（经 <see cref="UsbCommunicationLayer.SetLibusbLibraryPath(string)"/>
/// 注册或复制解析）；热插拔为尽力而为，未提供回调时使用轮询监视器。</para>
/// </remarks>
internal sealed class HarmonyUsbApiProvider : LibUsbApiProvider
{
    public new const string ApiNameConst = "harmony";

    public override string ApiName => ApiNameConst;

    public override bool IsSupportedOnCurrentPlatform => PlatformDetection.IsHarmonyOs;

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
            Notes = "HarmonyOS host USB via libusb: enable only when a libusb runtime is available on the device (registered path via UsbCommunicationLayer.SetLibusbLibraryPath or a resolvable copy). The HOT area sends enumeration, open and claim through libusb; hot-plug may be unavailable, in which case the polling monitor is used.",
        };
    }
}