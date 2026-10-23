using System.Globalization;
using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>ADB root probe and explicitly selected block-device flashing.
/// <para>ADB root 探测与显式选择的块设备刷写。</para></summary>
public static class FlashSuService
{
    /// <summary>Checks whether the selected ADB target can execute a noninteractive root shell.
    /// <para>检查所选 ADB 目标能否执行非交互式 root shell。</para></summary>
    /// <remarks>
    /// <para>直接 shell 已为 uid 0（adbd 以 root 运行）或 <c>su</c> 能非交互提升都算可用；
    /// 每次探测读取都有期限，授权弹窗不会长时间占住设备扫描。探测不会自行发送 <c>root:</c>
    /// 服务，因为重启 adbd 会让直连 USB 租约失效；adbd 已提权时直接识别并复用。</para>
    /// A direct shell that is already uid 0 (adbd running as root) and a noninteractive <c>su</c>
    /// elevation both count as available; every probe read is bounded so a consent prompt cannot
    /// hold a device scan for long. The probe never sends the <c>root:</c> service itself, because
    /// restarting adbd would invalidate a direct-USB lease; an already elevated adbd is detected
    /// and reused instead.
    /// </remarks>
    /// <param name="device">The ADB target to probe. <para>要探测的 ADB 目标。</para></param>
    /// <param name="ct">Cancellation for the probe. <para>探测过程的取消令牌。</para></param>
    /// <returns>True when a noninteractive root shell succeeds; false otherwise.
    /// <para>非交互式 root shell 执行成功时为 true，否则为 false。</para></returns>
    public static async Task<bool> HasSuAsync(DetectedDevice device, CancellationToken ct)
        => await ProbeRootModeAsync(device, ct).ConfigureAwait(false) != AdbRootMode.None;

    /// <summary>Resolves how root commands must be issued on the device.
    /// <para>解析该设备上 root 命令的执行方式。</para></summary>
    /// <param name="device">The ADB target to probe.<para>要探测的 ADB 目标。</para></param>
    /// <param name="ct">Cancellation for the probe.<para>探测过程的取消令牌。</para></param>
    /// <returns>The resolved mode, or <see cref="AdbRootMode.None"/> when unavailable or when the
    /// probe fails without cancellation.<para>解析出的模式；不可用或探测在未取消时失败则为
    /// <see cref="AdbRootMode.None"/>。</para></returns>
    internal static async Task<AdbRootMode> ProbeRootModeAsync(DetectedDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol != DeviceProtocolKind.Adb || device.State != "device")
        {
            return AdbRootMode.None;
        }

        try
        {
            return await App.DeviceMonitor.RunAdbAsync(
                device, adb => AdbRootCommand.ProbeAsync(adb, ct), ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return AdbRootMode.None;
        }
    }

    /// <summary>Pushes a selected image and writes it only to a validated partition node.
    /// <para>推送所选镜像，并仅写入经过校验的分区节点。</para></summary>
    /// <param name="device">The online ADB target to flash. <para>要刷写的在线 ADB 目标。</para></param>
    /// <param name="partition">The by-name partition node to write. <para>要写入的 by-name 分区节点。</para></param>
    /// <param name="path">Local image file to push and write. <para>要推送并写入的本地镜像文件。</para></param>
    /// <param name="ct">Cancellation for push, write and cleanup. <para>推送、写入与清理的取消令牌。</para></param>
    /// <returns>A single-line status for the completed write. <para>完成写入后的单行状态文本。</para></returns>
    public static Task<string> FlashAsync(DetectedDevice device, string partition, string path, CancellationToken ct)
        => FlashCoreAsync(device, partition, path, rootMode: null, ct);

    /// <summary>Writes one image with an already resolved root mode, so a multi-partition batch probes once.
    /// <para>使用已解析的 root 模式写入一个镜像，使多分区批次只探测一次。</para></summary>
    /// <param name="device">The online ADB target to flash. <para>要刷写的在线 ADB 目标。</para></param>
    /// <param name="partition">The by-name partition node to write. <para>要写入的 by-name 分区节点。</para></param>
    /// <param name="path">Local image file to push and write. <para>要推送并写入的本地镜像文件。</para></param>
    /// <param name="rootMode">The mode resolved for this device; <see cref="AdbRootMode.None"/> is rejected.
    /// <para>为该设备解析出的模式；拒绝 <see cref="AdbRootMode.None"/>。</para></param>
    /// <param name="ct">Cancellation for push, write and cleanup. <para>推送、写入与清理的取消令牌。</para></param>
    /// <returns>A single-line status for the completed write. <para>完成写入后的单行状态文本。</para></returns>
    internal static Task<string> FlashAsync(DetectedDevice device, string partition, string path,
        AdbRootMode rootMode, CancellationToken ct)
        => FlashCoreAsync(device, partition, path, rootMode, ct);

    private static async Task<string> FlashCoreAsync(DetectedDevice device, string partition, string path,
        AdbRootMode? rootMode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol != DeviceProtocolKind.Adb || device.State != "device" ||
            partition.Length is < 1 or > 128 ||
            !partition.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException("ADB root flashing requires an online device and a valid partition.");
        }

        var image = new FileInfo(path);
        if (!image.Exists || image.Length == 0)
        {
            throw new FileNotFoundException("Image is missing or empty.", path);
        }

        if (rootMode is AdbRootMode.None)
        {
            throw new UnauthorizedAccessException("Noninteractive root access is required.");
        }

        var imageBytes = image.Length;
        return await App.DeviceMonitor.RunAdbAsync(device, async adb =>
        {
            var mode = rootMode ?? await AdbRootCommand.ProbeAsync(adb, ct).ConfigureAwait(false);
            if (mode == AdbRootMode.None)
            {
                throw new UnauthorizedAccessException("Noninteractive root access is required.");
            }

            var target = "/dev/block/bootdevice/by-name/" + partition;
            var geometry = await RunRootAsync(adb, mode,
                $"test -b {target} && blockdev --getsize64 {target}",
                AdbRootCommand.ProbeReadTimeoutMs, ct).ConfigureAwait(false);
            if (geometry.ExitCode != 0)
            {
                throw new InvalidOperationException($"Block device not found: {target}");
            }

            if (!long.TryParse(Encoding.UTF8.GetString(geometry.Stdout).Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
                bytes < imageBytes)
            {
                throw new InvalidOperationException($"Cannot verify that {target} has enough capacity.");
            }

            var remote = "/data/local/tmp/uotan-" + Guid.NewGuid().ToString("N") + ".img";
            try
            {
                ct.ThrowIfCancellationRequested();
                await using (var sync = adb.CreateSyncClient())
                {
                    await sync.PushAsync(path, remote, cancellationToken: ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                var result = await RunRootAsync(adb, mode,
                    $"dd if={remote} of={target} bs=1048576 conv=fsync",
                    AdbRootCommand.WriteReadTimeoutMs, ct).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw new IOException($"Root dd failed: {Encoding.UTF8.GetString(result.Stderr).Trim()}");
                }

                return $"ADB su dd {partition}: OK";
            }
            finally
            {
                try { await adb.ExecuteCommandAsync($"rm -f {remote}", CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }, ct).ConfigureAwait(false);
    }

    private static ValueTask<ShellResult> RunRootAsync(IAdbDeviceSession adb, AdbRootMode mode,
        string command, int timeoutMs, CancellationToken ct)
        => adb.CreateShellClient(AdbRootCommand.Build(mode, command)).ExecuteAsync(timeoutMs, ct);
}