using System.Text;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Root execution mode resolved for an online ADB target.
/// <para>在线 ADB 目标解析出的 root 执行方式。</para>
/// </summary>
internal enum AdbRootMode
{
    /// <summary>No noninteractive root shell is available.<para>没有可用的非交互式 root shell。</para></summary>
    None,
    /// <summary>The device <c>su</c> helper elevates each command.<para>由设备上的 <c>su</c> 助手提升每条命令。</para></summary>
    Su,
    /// <summary>adbd already runs as uid 0 (for example after the <c>root:</c> service), so commands run unchanged.<para>adbd 已以 uid 0 运行（例如执行过 <c>root:</c> 服务），命令原样执行。</para></summary>
    AdbdRoot,
}

/// <summary>
/// Probes and builds the shell commands that give an ADB target root execution.
/// <para>探测并构建让 ADB 目标以 root 执行的 shell 命令。</para>
/// </summary>
internal static class AdbRootCommand
{
    /// <summary>Per-read wait for identity and block-device probes.
    /// <para>身份与块设备探测的单次读取等待。</para></summary>
    internal const int ProbeReadTimeoutMs = 6_000;

    /// <summary>Per-read wait for a direct write; <c>dd</c> reports nothing until it finishes.
    /// <para>直接写入的单次读取等待；<c>dd</c> 完成前没有输出。</para></summary>
    internal const int WriteReadTimeoutMs = 600_000;

    /// <summary>Reads the identity output and reports whether it is uid 0.
    /// <para>读取身份输出并报告是否为 uid 0。</para></summary>
    /// <param name="stdout">Command stdout; <c>id -u</c> prints <c>0</c> and <c>id</c> prints <c>uid=0(...)</c>.
    /// <para>命令的 stdout；<c>id -u</c> 输出 <c>0</c>，<c>id</c> 输出 <c>uid=0(...)</c>。</para></param>
    /// <returns><see langword="true"/> when the output identifies uid 0.
    /// <para>输出标识 uid 0 时为 <see langword="true"/>。</para></returns>
    internal static bool IsRootIdentity(ReadOnlySpan<byte> stdout)
    {
        var text = Encoding.UTF8.GetString(stdout).Trim();
        return text == "0" || text.Contains("uid=0(", StringComparison.Ordinal);
    }

    /// <summary>Runs a noninteractive identity probe for the root execution mode.
    /// <para>执行非交互式身份探测并返回 root 执行方式。</para></summary>
    /// <param name="adb">Connected ADB session.<para>已连接的 ADB 会话。</para></param>
    /// <param name="cancellationToken">Cancels both probes.<para>取消两次探测。</para></param>
    /// <returns><see cref="AdbRootMode.AdbdRoot"/> or <see cref="AdbRootMode.Su"/> when the probe
    /// succeeds; <see cref="AdbRootMode.None"/> otherwise.
    /// <para>探测成功时为 <see cref="AdbRootMode.AdbdRoot"/> 或 <see cref="AdbRootMode.Su"/>；否则为
    /// <see cref="AdbRootMode.None"/>。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    internal static async Task<AdbRootMode> ProbeAsync(IAdbDeviceSession adb, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adb);
        var direct = await adb.CreateShellClient("id -u").ExecuteAsync(ProbeReadTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (direct.ExitCode == 0 && IsRootIdentity(direct.Stdout))
        {
            return AdbRootMode.AdbdRoot;
        }

        try
        {
            var elevated = await adb.CreateShellClient(Build(AdbRootMode.Su, "id -u"))
                .ExecuteAsync(ProbeReadTimeoutMs, cancellationToken).ConfigureAwait(false);
            return elevated.ExitCode == 0 && IsRootIdentity(elevated.Stdout)
                ? AdbRootMode.Su
                : AdbRootMode.None;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is TimeoutException or IOException or InvalidDataException)
        {
            return AdbRootMode.None;
        }
    }

    /// <summary>Builds the command line sent to the device shell for one root operation.
    /// <para>为一次 root 操作构建发送到设备 shell 的命令行。</para></summary>
    /// <param name="mode">Resolved root mode.<para>解析出的 root 模式。</para></param>
    /// <param name="command">Command to run as root.<para>要以 root 执行的命令。</para></param>
    /// <returns>The command line for the device shell.<para>设备 shell 的命令行。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is empty.
    /// <para><paramref name="command"/> 为空。</para></exception>
    /// <exception cref="InvalidOperationException"><paramref name="mode"/> is <see cref="AdbRootMode.None"/>.
    /// <para><paramref name="mode"/> 为 <see cref="AdbRootMode.None"/>。</para></exception>
    internal static string Build(AdbRootMode mode, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return mode switch
        {
            AdbRootMode.AdbdRoot => command,
            AdbRootMode.Su => "su -c " + Quote(command),
            _ => throw new InvalidOperationException("Root access is not available on this ADB target."),
        };
    }

    /// <summary>Wraps one argument in POSIX single quotes so the device shell passes it unchanged.
    /// <para>用 POSIX 单引号包裹参数，使设备 shell 原样传递。</para></summary>
    /// <param name="value">Argument text.<para>参数文本。</para></param>
    /// <returns>The quoted argument.<para>加引号后的参数。</para></returns>
    internal static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}