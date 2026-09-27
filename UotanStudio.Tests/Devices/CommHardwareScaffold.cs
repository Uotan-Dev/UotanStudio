using UotanStudio.Comm;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Shared opt-in gates for read-only real-hardware COMM suites. A test in these suites runs only
/// when its environment variable names a device that is physically attached; missing hardware
/// always yields an explicit skip, never a pass. No test built on this scaffold may flash, erase,
/// reboot, reset, upload an agent/programmer, or otherwise mutate device state.
/// <para>Comm 真机只读测试套件的共享选配门控。仅当环境变量指定的设备实际在场时用例才运行；
/// 缺少硬件一律明确跳过而非通过。基于本脚手架的用例不得刷写、擦除、重启、复位、上传
/// agent/programmer 或以任何方式改变设备状态。</para>
/// </summary>
internal static class CommHardwareScaffold
{
    internal const string EdlDeviceVariable = "UOTAN_TEST_EDL_USB_ID";
    internal const string EdlProgrammerDirectoryVariable = "UOTAN_TEST_EDL_PROGRAMMER_DIR";
    internal const string MtkDeviceVariable = "UOTAN_TEST_MTK_USB_ID";
    internal const string MtkLoaderPathVariable = "UOTAN_TEST_MTK_LOADER_PATH";

    private static readonly DeviceController ControllerValue = DeviceControllerFactory.CreateDefault();

    static CommHardwareScaffold()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("UOTAN_TEST_USB_BACKEND"), "libusb",
            StringComparison.Ordinal))
        {
            return;
        }

        var nativeLibrary = Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dll");
        if (File.Exists(nativeLibrary))
        {
            CommKit.SetLibusbLibraryPath(nativeLibrary);
        }
    }

    internal static DeviceController Controller => ControllerValue;

    internal static void RequireEdlDevice() => RequireDevice(EdlDeviceVariable,
        "attached Qualcomm EDL (Sahara or Firehose) device");

    internal static void RequireMtkDevice() => RequireDevice(MtkDeviceVariable,
        "attached MediaTek BROM/Preloader device");

    internal static string? OptionalEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    internal static async Task<DeviceEndpoint> FindEndpointAsync(string protocol, string idVariable)
    {
        var spec = Environment.GetEnvironmentVariable(idVariable) ?? string.Empty;
        var snapshot = await Controller.RefreshAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        var match = snapshot.Endpoints.FirstOrDefault(candidate =>
            candidate.Protocol == protocol && MatchesSpecification(candidate, spec));
        return match ?? throw new InvalidOperationException(
            $"No '{protocol}' endpoint matching '{spec}' is present; attach the device or unset {idVariable}.");
    }

    private static void RequireDevice(string variable, string description)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            Assert.Skip($"{variable} 未设置；{description} 的只读真机套件为选配 / {variable} is not set; " +
                $"the read-only hardware suite for the {description} is opt-in and skipped.");
        }
    }

    private static bool MatchesSpecification(DeviceEndpoint endpoint, string spec)
    {
        if (spec.Length == 0)
        {
            return false;
        }

        if (spec.Contains(':') && ushort.TryParse(spec[..4], System.Globalization.NumberStyles.HexNumber,
            null, out var vendor) && ushort.TryParse(spec.Split(':', 2)[1], System.Globalization.NumberStyles.HexNumber,
            null, out var product))
        {
            return endpoint.Usb is { } usb && usb.VendorId == vendor && usb.ProductId == product;
        }

        return (endpoint.UsbInfo?.DevicePath?.Contains(spec, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (endpoint.UsbInfo?.DeviceInstanceId?.Contains(spec, StringComparison.OrdinalIgnoreCase) ?? false) ||
            endpoint.Address.Contains(spec, StringComparison.OrdinalIgnoreCase);
    }
}