using System.Text;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Executes one declared shell command on an ADB target.<para>在 ADB 目标上执行一条已声明的 Shell 命令。</para></summary>
public static class DeviceShellCommandService
{
    /// <summary>Runs the command over the exact routed ADB target and returns its UTF-8 output.<para>在精确路由的 ADB 目标上运行命令并返回其 UTF-8 输出。</para></summary>
    /// <param name="device">Target ADB device.<para>目标 ADB 设备。</para></param>
    /// <param name="command">Declared shell command.<para>已声明的 Shell 命令。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The command output decoded as UTF-8.<para>以 UTF-8 解码的命令输出。</para></returns>
    public static async Task<string> ExecuteAsync(DetectedDevice device, string command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (device.Protocol != DeviceProtocolKind.Adb)
        {
            throw new ArgumentException("An ADB target is required.", nameof(device));
        }

        ct.ThrowIfCancellationRequested();
        return await App.DeviceMonitor.RunAdbAsync(device, async session =>
        {
            await using var stream = session.OpenServiceStream("shell:" + command);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(memory.ToArray());
        }, ct).ConfigureAwait(false);
    }
}