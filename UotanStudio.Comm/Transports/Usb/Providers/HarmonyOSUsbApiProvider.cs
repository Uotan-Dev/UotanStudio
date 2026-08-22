using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;
using static UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS.HarmonyOSUsbDDK;

namespace UotanStudio.Comm.Transports.Usb.Providers;

internal sealed class HarmonyOSUsbApiProvider : UsbApiProviderBase
{
    // The HarmonyOS USB DDK backend is opt-in. HarmonyOS cannot be detected reliably
    // through os-release style file probes (the platform's runtime differs), so the
    // provider stays hidden unless explicitly enabled with FIRMWAREKIT_USB_ENABLE_HARMONY=1.
    // <para>HarmonyOS USB DDK 后端为显式开启项。HarmonyOS 无法通过 os-release 类文件探测可靠
    // 识别（平台运行时存在差异），因此除非设置 FIRMWAREKIT_USB_ENABLE_HARMONY=1 显式开启，
    // 该提供器保持隐藏。</para>
    internal const string EnableEnvironmentVariable = "FIRMWAREKIT_USB_ENABLE_HARMONY";

    private static readonly Lazy<bool> DdkAvailable =
        new(() =>
        {
            if (!IsExplicitlyEnabled()) return false;

            try
            {
                var ret = OH_Usb_Init();
                if (ret == USB_DDK_NO_ERROR)
                {
                    OH_Usb_Release();
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }, isThreadSafe: true);

    private static bool IsExplicitlyEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnableEnvironmentVariable);
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public const string ApiNameConst = "harmony";

    public override string ApiName => ApiNameConst;

    public override UsbApiKind ApiKind => UsbApiKind.HarmonyOS;

    public override bool IsSupportedOnCurrentPlatform => DdkAvailable.Value;

    protected override List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter)
        => SafeEnumerate(() => HarmonyOSUsbFinder.FindDevice(filter), "HarmonyOS USB DDK");

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
            SupportsNativeAsyncIo = false,
            SupportsNativeHotPlugMonitoring = false,
            RequiresExternalRuntime = false,
            // Reset re-initializes the DDK session (OH_Usb_Release + OH_Usb_Init + claim):
            // interface handles and memmaps are rebuilt, so the caller must re-open.
            // <para>Reset 会重新初始化 DDK 会话（OH_Usb_Release + OH_Usb_Init + claim）：
            // 接口句柄与内存映射被重建，调用方必须重新打开。</para>
            ResetReenumeratesDevice = true,
            Notes = "HarmonyOS USB DDK backend via libusb_ndk.z.so P/Invoke; pure C# implementation with no external bridge required. Opt-in: set FIRMWAREKIT_USB_ENABLE_HARMONY=1 to enable; must run within DriverExtensionAbility lifecycle with ohos.permission.ACCESS_DDK_USB."
            // <para>基于 libusb_ndk.z.so P/Invoke 的 HarmonyOS USB DDK 后端；纯 C# 实现，无需外部桥接。
            // 需设置 FIRMWAREKIT_USB_ENABLE_HARMONY=1 显式开启；必须在 DriverExtensionAbility 生命周期内
            // 运行，并持有 ohos.permission.ACCESS_DDK_USB 权限。</para>
        };
    }
}
