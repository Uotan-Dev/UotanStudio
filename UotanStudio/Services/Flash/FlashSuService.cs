using System.Globalization;
using System.Text;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>ADB root probe and explicitly selected block-device flashing.
/// <para>ADB root 探测与显式选择的块设备刷写。</para></summary>
public static class FlashSuService
{
    /// <summary>Checks whether the selected ADB target can execute a noninteractive root shell.
    /// <para>检查所选 ADB 目标能否执行非交互式 root shell。</para></summary>
    /// <param name="device">The ADB target to probe. <para>要探测的 ADB 目标。</para></param>
    /// <param name="ct">Cancellation for the probe. <para>探测过程的取消令牌。</para></param>
    /// <returns>True when a noninteractive root shell succeeds; false otherwise.
    /// <para>非交互式 root shell 执行成功时为 true，否则为 false。</para></returns>
    public static async Task<bool> HasSuAsync(DetectedDevice device, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Adb || device.State != "device") return false;
        try
        {
            return await App.DeviceMonitor.RunAdbAsync(device, async adb =>
            {
                var result = await adb.ExecuteCommandAsync("su -c id", ct).ConfigureAwait(false);
                return result.ExitCode == 0 &&
                    Encoding.UTF8.GetString(result.Stdout).Contains("uid=0(", StringComparison.Ordinal);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Pushes a selected image and writes it only to a validated partition node.
    /// <para>推送所选镜像，并仅写入经过校验的分区节点。</para></summary>
    /// <param name="device">The online ADB target to flash. <para>要刷写的在线 ADB 目标。</para></param>
    /// <param name="partition">The by-name partition node to write. <para>要写入的 by-name 分区节点。</para></param>
    /// <param name="path">Local image file to push and write. <para>要推送并写入的本地镜像文件。</para></param>
    /// <param name="ct">Cancellation for push, write and cleanup. <para>推送、写入与清理的取消令牌。</para></param>
    /// <returns>A single-line status for the completed write. <para>完成写入后的单行状态文本。</para></returns>
    public static async Task<string> FlashAsync(DetectedDevice device, string partition, string path, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Adb || device.State != "device" ||
            partition.Length is < 1 or > 128 ||
            !partition.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new InvalidOperationException("ADB root flashing requires an online device and a valid partition.");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new FileNotFoundException("Image is missing or empty.", path);
        return await App.DeviceMonitor.RunAdbAsync(device, async adb =>
        {
            var root = await adb.ExecuteCommandAsync("su -c id", ct).ConfigureAwait(false);
            if (root.ExitCode != 0 || !Encoding.UTF8.GetString(root.Stdout).Contains("uid=0(", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Noninteractive su access is required.");
            var target = "/dev/block/bootdevice/by-name/" + partition;
            var check = await adb.ExecuteCommandAsync($"su -c 'test -b {target}'", ct).ConfigureAwait(false);
            if (check.ExitCode != 0) throw new InvalidOperationException($"Block device not found: {target}");
            var capacity = await adb.ExecuteCommandAsync($"su -c 'blockdev --getsize64 {target}'", ct).ConfigureAwait(false);
            if (capacity.ExitCode != 0 ||
                !long.TryParse(Encoding.UTF8.GetString(capacity.Stdout).Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
                bytes < new FileInfo(path).Length)
                throw new InvalidOperationException($"Cannot verify that {target} has enough capacity.");
            var remote = "/data/local/tmp/uotan-" + Guid.NewGuid().ToString("N") + ".img";
            try
            {
                ct.ThrowIfCancellationRequested();
                await using (var sync = adb.CreateSyncClient())
                    await sync.PushAsync(path, remote, cancellationToken: ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var result = await adb.ExecuteCommandAsync($"su -c 'dd if={remote} of={target} bs=1048576 conv=fsync'", ct)
                    .ConfigureAwait(false);
                if (result.ExitCode != 0)
                    throw new IOException($"Root dd failed: {Encoding.UTF8.GetString(result.Stderr).Trim()}");
                return $"ADB su dd {partition}: OK";
            }
            finally
            {
                try { await adb.ExecuteCommandAsync($"rm -f {remote}", CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }, ct).ConfigureAwait(false);
    }
}