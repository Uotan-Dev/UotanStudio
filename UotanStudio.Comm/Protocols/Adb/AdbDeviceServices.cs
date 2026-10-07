using System.Text;
using UotanStudio.Comm.Contracts;

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
internal sealed class AdbDeviceServices(AdbConnection connection) : IAdbDeviceServices
{
    private readonly AdbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Runs a one-shot service and returns all output as text.
    /// <para>运行一次性服务并将全部输出作为文本返回。</para>
    /// </summary>
    /// <param name="service">The service destination string (e.g. "remount:"). 服务目标字符串（如 "remount:"）。</param>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    /// <returns>The service output text. 服务的输出文本。</returns>
    /// <remarks>The read waits use the stream's synchronous wait, because this façade exposes a blocking API and
    /// its payloads stay small; data-plane transfers go through <see cref="IAdbSyncClient"/> instead.
    /// <para>读取等待使用流的同步等待，因为该门面暴露阻塞 API 且负载很小；数据面传输改走 <see cref="IAdbSyncClient"/>。</para>
    /// </remarks>
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
    /// Reboots the device, optionally into a mode (bootloader, recovery, ...).
    /// <para>重启设备，可选进入指定模式（bootloader、recovery 等）。</para>
    /// </summary>
    /// <param name="mode">The reboot target mode, or null for a plain reboot. 重启目标模式，null 表示普通重启。</param>
    public void Reboot(string? mode = null)
    {
        var service = string.IsNullOrEmpty(mode) ? "reboot:" : $"reboot:{mode}";
        using var stream = _connection.OpenProtocolStream(service);
    }

    /// <summary>
    /// Remounts the filesystem read-write (requires a userdebug build).
    /// <para>重新挂载为可读写（需要 userdebug 构建）。</para>
    /// </summary>
    public string Remount() => RunService("remount:");

    /// <summary>
    /// Reads a system property via the shell. The key is validated against the property-name
    /// character set before interpolation, so a crafted key can never introduce shell
    /// metacharacters into the <c>getprop</c> command line.
    /// <para>通过 shell 读取系统属性。属性名先按属性名字符集校验再插值，
    /// 构造的属性名无法向 <c>getprop</c> 命令行注入 shell 元字符。</para>
    /// </summary>
    /// <remarks>This is the blocking form of the shell v2 read: it parks the calling thread on
    /// <see cref="AdbShellClient.ExecuteAsync"/> because the interface exposes a synchronous property read.
    /// <para>本方法是 shell v2 读取的阻塞形式：因为接口暴露同步的属性读取，它会把调用线程停在
    /// <see cref="AdbShellClient.ExecuteAsync"/> 上。</para>
    /// </remarks>
    /// <param name="key">The property key; letters, digits and <c>.</c> <c>_</c> <c>-</c> <c>@</c> <c>:</c> only.
    /// 属性名；仅允许字母、数字以及 <c>.</c> <c>_</c> <c>-</c> <c>@</c> <c>:</c>。</param>
    /// <returns>The property value, trimmed. 去除首尾空白的属性值。</returns>
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