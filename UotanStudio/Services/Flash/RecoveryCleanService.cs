using System.Text;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Runs a booted TWRP-style recovery's destructive wipe entry points over ADB.</summary>
public static class RecoveryCleanService
{
    /// <summary>Maps a clean mode to the recovery command and executes it, returning the recovery output.</summary>
    public static async Task<string> CleanAsync(DetectedDevice device, string mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol != DeviceProtocolKind.Adb)
        {
            throw new ArgumentException("An ADB recovery target is required.", nameof(device));
        }
        var command = mode switch
        {
            "factoryreset" => "factory_reset",
            "formatdata" => "format data",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Supported modes: factoryreset, formatdata.")
        };

        return await App.DeviceMonitor.RunAdbAsync(device, async session =>
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = session.OpenServiceStream("shell:twrp " + command);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(memory.ToArray());
        }, ct).ConfigureAwait(false);
    }
}