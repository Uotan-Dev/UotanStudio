using System.Text;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Runs a booted recovery's destructive wipe entry points over ADB.
/// <para>通过 ADB 运行已启动 recovery 的破坏性清除入口点。</para></summary>
public static class RecoveryCleanService
{
    /// <summary>Maps a clean mode to the recovery command and executes it, returning the recovery output.
    /// <para>把清除模式映射为 recovery 命令并执行，返回 recovery 的输出。</para></summary>
    /// <param name="device">The ADB recovery target device.<para>作为 ADB recovery 目标的设备。</para></param>
    /// <param name="mode">The clean mode to run.<para>要运行的清除模式。</para></param>
    /// <param name="ct">Cancellation for the command exchange.<para>用于命令交换的取消令牌。</para></param>
    /// <returns>The recovery command output text.<para>recovery 命令的输出文本。</para></returns>
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