using UotanStudio.Services.Workspace;

namespace UotanStudio.Services.Devices;

/// <summary>Opens the app's managed shell in the existing terminal dock.</summary>
internal static class DeviceShellService
{
    internal static bool IsAdbReadyState(string state) => state is "device" or "online";

    internal static void RequireServerTarget(DetectedDevice device, bool serverPresent, bool targetReady)
    {
        if (device.Protocol is not (DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc))
            throw new ArgumentException("An ADB or HDC target is required.", nameof(device));
        if (!serverPresent)
            throw new NotSupportedException("An interactive shell requires a running debug bridge server.");
        if (!targetReady)
            throw new IOException("The selected target is not ready on the debug bridge server.");
    }

    internal static void OpenInteractiveShell(DeviceMonitorService monitor, DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol == DeviceProtocolKind.Adb)
        {
            if (!IsAdbReadyState(device.State) || !monitor.ProbeAdb(device))
                throw new IOException("The selected ADB target is not ready.");
        }
        else if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            var present = DeviceMonitorService.UseHdcServerFor(device);
            RequireServerTarget(device, present, present && device.State == "device");
        }
        else
        {
            RequireServerTarget(device, false, false);
        }
        WorkspacePanelService.Instance.OpenDeviceShell(device);
    }
}