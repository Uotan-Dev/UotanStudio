using System.Globalization;
using UotanStudio.Services.Devices;

namespace UotanStudio.Workflow.Nodes;

internal static class HdcNodeSupport
{
    internal static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);

    internal static bool TryResolveDevice(object? deviceValue, string? serial, out DetectedDevice? device,
        out string status)
    {
        device = NodeDataUtil.ToDevice(deviceValue);
        if (device is not null)
        {
            if (device.Protocol != DeviceProtocolKind.Hdc)
            {
                device = null;
                status = "invalid-device";
                return false;
            }

            status = "connected";
            return true;
        }

        if (string.IsNullOrWhiteSpace(serial))
        {
            status = "no-device";
            return false;
        }

        device = App.DeviceMonitor.Scan().FirstOrDefault(candidate =>
            candidate.Protocol == DeviceProtocolKind.Hdc
            && string.Equals(candidate.Serial, serial, StringComparison.Ordinal));
        if (device is null)
        {
            status = "not-found";
            return false;
        }

        status = "connected";
        return true;
    }

    internal static DetectedDevice ResolveDevice(FlowNodeViewModel node)
    {
        var serial = Text(node.ReadInput("Serial"));
        if (!TryResolveDevice(node.ReadInput("Device"), serial, out var device, out var status))
        {
            node.WriteOutput("Status", status);
            var message = DeviceFailureMessage(status, serial);
            throw status == "invalid-device"
                ? new NotSupportedException(message)
                : new InvalidOperationException(message);
        }

        return device!;
    }

    internal static void MarkFailure(FlowNodeViewModel node)
    {
        var status = node.Output.FirstOrDefault(port => string.Equals(port.Title, "Status", StringComparison.Ordinal));
        if (status is { Data: null })
        {
            status.Data = "failed";
        }
    }

    internal static string DeviceFailureMessage(string status, string? serial) => status switch
    {
        "invalid-device" => "The Device input must be an HDC target.",
        "not-found" => $"No HDC target is connected with serial '{serial}'.",
        _ => "The Device or Serial input is required for an HDC operation."
    };

    internal static TimeSpan ValidateWaitTimeout(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > 86400)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds),
                "The Timeout input must be positive and at most 86400 seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}