using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;
using static UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS.HarmonyOSUsbDDK;

namespace UotanStudio.Comm.Transports.Usb.Providers;

internal sealed class HarmonyOSUsbApiProvider : UsbApiProviderBase
{
    internal const string EnableEnvironmentVariable = "FIRMWAREKIT_USB_ENABLE_HARMONY";

    private static readonly Lazy<bool> DdkAvailable =
        new(() =>
        {
            if (!IsExplicitlyEnabled())
            {
                return false;
            }

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
            ResetReenumeratesDevice = true,
            Notes = "HarmonyOS USB DDK backend via libusb_ndk.z.so P/Invoke; pure C# implementation with no external bridge required. Opt-in: set FIRMWAREKIT_USB_ENABLE_HARMONY=1 to enable; must run within DriverExtensionAbility lifecycle with ohos.permission.ACCESS_DDK_USB."
        };
    }
}