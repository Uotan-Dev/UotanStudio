using System.Text;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Client for one-shot device-level ADB services (reboot, remount, getprop, ...).
/// <para>一次性设备级 ADB 服务（reboot、remount、getprop 等）客户端。</para>
/// </summary>
/// <remarks>
/// Initializes a new device services client.
/// <para>初始化新的设备服务客户端。</para>
/// </remarks>
/// <param name="connection">An established ADB connection. 已建立的 ADB 连接。</param>
public sealed class AdbDeviceServices(AdbConnection connection)
{
    private readonly AdbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Runs a one-shot service and returns all output as text.
    /// <para>运行一次性服务并将全部输出作为文本返回。</para>
    /// </summary>
    /// <param name="service">The service destination string (e.g. "remount:"). 服务目标字符串（如 "remount:"）。</param>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns>The service output text. 服务的输出文本。</returns>
    public string RunService(string service, int timeoutMs = 30000)
    {
        using var stream = _connection.OpenStream(service);
        var sb = new StringBuilder();
        byte[]? chunk;
        while ((chunk = stream.Read(timeoutMs)) is not null)
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reboots the device, optionally into a mode (bootloader, recovery, ...).
    /// <para>重启设备，可选进入指定模式（bootloader、recovery 等）。</para>
    /// </summary>
    /// <param name="mode">The reboot target mode, or null for a plain reboot. 重启目标模式，null 表示普通重启。</param>
    public void Reboot(string? mode = null)
    {
        var service = string.IsNullOrEmpty(mode) ? "reboot:" : $"reboot:{mode}";
        using var stream = _connection.OpenStream(service);
    }

    /// <summary>
    /// Remounts the filesystem read-write (requires a userdebug build).
    /// <para>重新挂载为可读写（需要 userdebug 构建）。</para>
    /// </summary>
    public string Remount() => RunService("remount:");

    /// <summary>
    /// Reads a system property via the shell.
    /// <para>通过 shell 读取系统属性。</para>
    /// </summary>
    /// <param name="key">The property key. 属性名。</param>
    /// <returns>The property value, trimmed. 去除首尾空白的属性值。</returns>
    public string GetProp(string key)
    {
        var shell = new AdbShellClient(_connection, $"getprop {key}");
        var result = shell.Execute();
        return Encoding.UTF8.GetString(result.Stdout).Trim();
    }
}