using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Client for one-shot device-level HDC services (reboot, remount, getprop, ...).
/// <para>一次性设备级 HDC 服务（reboot、remount、getprop 等）客户端。</para>
/// </summary>
/// <remarks>
/// Initializes a new device services client.
/// <para>初始化新的设备服务客户端。</para>
/// </remarks>
/// <param name="connection">An established HDC connection. 已建立的 HDC 连接。</param>
public sealed class HdcDeviceServices(HdcConnection connection)
{
    private readonly HdcConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Runs a one-shot command via UnityExecute and returns all output as text.
    /// <para>通过 UnityExecute 运行一次性命令并将全部输出作为文本返回。</para>
    /// </summary>
    /// <param name="command">The command to execute. 要执行的命令。</param>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns>The command output text. 命令输出文本。</returns>
    public string RunCommand(string command, int timeoutMs = 30000)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        using var stream = _connection.OpenChannel(HdcCommand.UnityExecute, Encoding.UTF8.GetBytes(command));
        var sb = new StringBuilder();
        byte[]? chunk;
        while ((chunk = stream.Read(timeoutMs)) is not null)
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Lists connected device targets.
    /// <para>列出已连接的设备目标。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns>A string containing device target information.
    /// <para>包含设备目标信息的字符串。</para></returns>
    public string ListTargets(int timeoutMs = 10000)
    {
        using var stream = _connection.OpenChannel(HdcCommand.TargetList);
        var sb = new StringBuilder();
        byte[]? chunk;
        while ((chunk = stream.Read(timeoutMs)) is not null)
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reboots the device, optionally into a specified mode.
    /// <para>重启设备，可选进入指定模式。</para>
    /// </summary>
    /// <param name="mode">
    /// The reboot target mode: <c>null</c> for normal reboot,
    /// <c>"bootloader"</c>, <c>"recovery"</c>, or <c>"updater"</c>.
    /// <para>重启目标模式：<c>null</c> 为正常重启，
    /// <c>"bootloader"</c>、<c>"recovery"</c> 或 <c>"updater"</c>。</para>
    /// </param>
    public void Reboot(string? mode = null)
    {
        var payload = string.IsNullOrEmpty(mode) ? null : Encoding.UTF8.GetBytes(mode);
        using var stream = _connection.OpenChannel(HdcCommand.UnityReboot, payload);
        // The device reboots; the stream may close abruptly, which is expected.
        // 设备随即重启；流可能突然关闭，属预期行为。
        _ = stream.Read(5000);
    }

    /// <summary>
    /// Remounts the filesystem read-write (requires a userdebug build).
    /// <para>重新挂载为可读写（需要 userdebug 构建）。</para>
    /// </summary>
    public void Remount()
    {
        using var stream = _connection.OpenChannel(HdcCommand.UnityRemount);
        _ = stream.Read(10000);
    }

    /// <summary>
    /// Reads a system property via the shell <c>getprop</c> command.
    /// <para>通过 shell <c>getprop</c> 命令读取系统属性。</para>
    /// </summary>
    /// <param name="key">The property key. 属性名。</param>
    /// <returns>The property value, trimmed. 去除首尾空白的属性值。</returns>
    public string GetProp(string key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        var shell = new HdcShellClient(_connection, $"getprop {key}");
        var result = shell.Execute();
        return Encoding.UTF8.GetString(result.Stdout).Trim();
    }

    /// <summary>
    /// Checks whether the HDC server is reachable.
    /// <para>检查 HDC server 是否可达。</para>
    /// </summary>
    /// <returns>The server response text. 服务器响应文本。</returns>
    public string CheckServer()
    {
        using var stream = _connection.OpenChannel(HdcCommand.CheckServer);
        var sb = new StringBuilder();
        byte[]? chunk;
        while ((chunk = stream.Read(5000)) is not null)
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Waits for a device matching the given connect key to become available.
    /// <para>等待匹配给定连接键的设备变为可用。</para>
    /// </summary>
    /// <param name="connectKey">The device connect key (serial or USB address).
    /// <para>设备连接键（序列号或 USB 地址）。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns>The response text. 响应文本。</returns>
    public string WaitFor(string connectKey, int timeoutMs = 30000)
    {
        if (connectKey is null)
        {
            throw new ArgumentNullException(nameof(connectKey));
        }

        using var stream = _connection.OpenChannel(HdcCommand.WaitFor, Encoding.UTF8.GetBytes(connectKey));
        var sb = new StringBuilder();
        byte[]? chunk;
        while ((chunk = stream.Read(timeoutMs)) is not null)
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }

        return sb.ToString();
    }
}
