using System.Globalization;
using System.Text;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>ADB root probe and explicitly selected block-device flashing.</summary>
public static class FlashSuService
{
    /// <summary>Checks whether the selected ADB target can execute a noninteractive root shell.</summary>
    public static async Task<bool> HasSuAsync(DetectedDevice device, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Adb || device.State != "device") return false;
        try
        {
            return await Task.Run(() =>
            {
                return App.DeviceMonitor.RunAdb(device, adb =>
                {
                    var result = adb.ExecuteCommand("su -c id");
                    return result.ExitCode == 0 &&
                        Encoding.UTF8.GetString(result.Stdout).Contains("uid=0(", StringComparison.Ordinal);
                }, ct);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Pushes a selected image and writes it only to a validated partition node.</summary>
    public static async Task<string> FlashAsync(DetectedDevice device, string partition, string path, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Adb || device.State != "device" ||
            partition.Length is < 1 or > 128 ||
            !partition.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new InvalidOperationException("ADB root flashing requires an online device and a valid partition.");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new FileNotFoundException("Image is missing or empty.", path);
        return await Task.Run(() => App.DeviceMonitor.RunAdb(device, adb =>
        {
            var root = adb.ExecuteCommand("su -c id");
            if (root.ExitCode != 0 || !Encoding.UTF8.GetString(root.Stdout).Contains("uid=0(", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Noninteractive su access is required.");
            var target = "/dev/block/bootdevice/by-name/" + partition;
            var check = adb.ExecuteCommand($"su -c 'test -b {target}'");
            if (check.ExitCode != 0) throw new InvalidOperationException($"Block device not found: {target}");
            var capacity = adb.ExecuteCommand($"su -c 'blockdev --getsize64 {target}'");
            if (capacity.ExitCode != 0 ||
                !long.TryParse(Encoding.UTF8.GetString(capacity.Stdout).Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
                bytes < new FileInfo(path).Length)
                throw new InvalidOperationException($"Cannot verify that {target} has enough capacity.");
            var remote = "/data/local/tmp/uotan-" + Guid.NewGuid().ToString("N") + ".img";
            try
            {
                ct.ThrowIfCancellationRequested();
                using (var sync = adb.CreateSyncClient())
                    sync.Push(path, remote);
                ct.ThrowIfCancellationRequested();
                var result = adb.ExecuteCommand($"su -c 'dd if={remote} of={target} bs=1048576 conv=fsync'");
                if (result.ExitCode != 0)
                    throw new IOException($"Root dd failed: {Encoding.UTF8.GetString(result.Stderr).Trim()}");
                return $"ADB su dd {partition}: OK";
            }
            finally
            {
                try { adb.ExecuteCommand($"rm -f {remote}"); }
                catch (Exception) { }
            }
        }, ct), ct).ConfigureAwait(false);
    }
}