using System.Text;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Executes one declared shell command on an ADB target. The whitelist is the named tool surface that
/// exposes this service; callers reach it only through the confirmed <c>Device.Shell.Command</c> tool
/// or node, so a raw shell is never opened without explicit consent.
/// </summary>
public static class DeviceShellCommandService
{
    /// <summary>Runs the command over the exact routed ADB target and returns its UTF-8 output.</summary>
    public static Task<string> ExecuteAsync(DetectedDevice device, string command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (device.Protocol != DeviceProtocolKind.Adb)
        {
            throw new ArgumentException("An ADB target is required.", nameof(device));
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return App.DeviceMonitor.RunAdb(device, session =>
            {
                using var stream = session.OpenServiceStream("shell:" + command);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return Encoding.UTF8.GetString(memory.ToArray());
            }, ct);
        }, ct);
    }
}