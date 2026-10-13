using System.Text;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Client for one-shot device-level ADB services such as reboot and remount.
/// <para>reboot、remount 等一次性设备级 ADB 服务的客户端。</para>
/// </summary>
/// <param name="connection">An established ADB connection. <para>已建立的 ADB 连接。</para></param>
internal sealed class AdbDeviceServices(AdbConnection connection) : IAdbDeviceServices
{
    private readonly AdbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Runs a one-shot service and returns all output as text.
    /// <para>运行一次性服务并将全部输出作为文本返回。</para>
    /// </summary>
    /// <param name="service">The service destination string, such as <c>remount:</c>. <para>服务目标字符串，如 <c>remount:</c>。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时（毫秒）。</para></param>
    /// <returns>The service output text. <para>服务的输出文本。</para></returns>
    public string RunService(string service, int timeoutMs = 30000)
    {
        using var stream = _connection.OpenProtocolStream(service);
        var sb = new StringBuilder();
        byte[]? chunk;
        while ((chunk = stream.Read(timeoutMs)) is not null)
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reboots the device, optionally into a named mode such as bootloader or recovery.
    /// <para>重启设备，可选进入 bootloader、recovery 等指定模式。</para>
    /// </summary>
    /// <param name="mode">The reboot target mode, or null for a plain reboot. <para>重启目标模式，null 表示普通重启。</para></param>
    public void Reboot(string? mode = null)
    {
        var service = string.IsNullOrEmpty(mode) ? "reboot:" : $"reboot:{mode}";
        using var stream = _connection.OpenProtocolStream(service);
    }

    /// <summary>
    /// Remounts the filesystem read-write (requires a userdebug build).
    /// <para>重新挂载为可读写（需要 userdebug 构建）。</para>
    /// </summary>
    /// <returns>The remount command output. <para>remount 命令的输出文本。</para></returns>
    public string Remount() => RunService("remount:");

    /// <summary>
    /// Reads a system property through the shell after validating the key against
    /// the property-name character set, so a crafted key cannot introduce shell
    /// metacharacters into the command line.
    /// <para>先按属性名字符集校验键再通过 shell 读取系统属性，构造的键无法向命令行注入 shell 元字符。</para>
    /// </summary>
    /// <param name="key">The property key, limited to letters, digits, dot, underscore, hyphen, at sign and colon.
    /// <para>属性名，仅限字母、数字、点、下划线、连字符、@ 与冒号。</para></param>
    /// <returns>The property value, trimmed. <para>去除首尾空白的属性值。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or contains characters outside the
    /// property-name set. <para><paramref name="key"/> 为空或包含属性名字符集以外的字符。</para></exception>
    /// <exception cref="TimeoutException">A read timed out while the shell stream was still open.
    /// <para>shell 流仍打开时读取超时。</para></exception>
    public string GetProp(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-' and not '@' and not ':'))
        {
            throw new ArgumentException("The property key contains characters outside the allowed set.", nameof(key));
        }

        var shell = new AdbShellClient(_connection, $"getprop {key}");
        var result = shell.ExecuteAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(result.Stdout).Trim();
    }
}