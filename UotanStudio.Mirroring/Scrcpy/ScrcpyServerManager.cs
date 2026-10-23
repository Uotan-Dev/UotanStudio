using System.Text;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Mirroring;

/// <summary>
/// Deploys and launches the scrcpy server on a device over the self-hosted
/// ADB wire stack (sync push + long-lived shell stream).
/// <para>基于自研 ADB wire 栈（sync push + 长驻 shell 流）在设备上部署并启动 scrcpy 服务端。</para>
/// </summary>
/// <remarks>
/// <para>
/// The server is pushed once to <see cref="ScrcpyConstants.ServerDevicePath"/>
/// and started via <c>CLASSPATH=... app_process</c>. The returned shell stream
/// must stay open for the lifetime of the session — the server exits when the
/// shell stream closes (its stdout is the server log).
/// </para>
/// <para>
/// 服务端一次性推送到 <see cref="ScrcpyConstants.ServerDevicePath"/>，经
/// <c>CLASSPATH=... app_process</c> 启动。返回的 shell 流须在会话生命周期内保持
/// 打开——shell 流关闭时服务端随之退出（其 stdout 即服务端日志）。
/// </para>
/// </remarks>
internal static class ScrcpyServerManager
{
    /// <summary>How long the kill command is given to report before the caller moves on.
    /// <para>结束命令在调用方继续之前被允许上报的时长。</para></summary>
    private const int ReplyWaitMilliseconds = 3000;

    /// <summary>Pushes the scrcpy server jar to the device.
    /// <para>将 scrcpy 服务端 jar 推送到设备。</para></summary>
    /// <param name="session">An established ADB session.<para>已建立的 ADB 会话。</para></param>
    /// <param name="localJarPath">Path of the bundled <c>scrcpy-server</c> jar.
    /// <para>随附 <c>scrcpy-server</c> jar 的本地路径。</para></param>
    /// <param name="cancellationToken">Cancels the push between sync packets.
    /// <para>在 sync 数据包之间取消推送。</para></param>
    /// <returns>A task that completes once the device has accepted the transfer.
    /// <para>设备接受该传输后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="localJarPath"/> is empty.
    /// <para><paramref name="localJarPath"/> 为空。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="localJarPath"/> does not exist.
    /// <para><paramref name="localJarPath"/> 不存在。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    internal static async Task PushServerAsync(IAdbDeviceSession session, string localJarPath,
        CancellationToken cancellationToken = default)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (string.IsNullOrEmpty(localJarPath))
        {
            throw new ArgumentException("Server jar path is required. 服务端 jar 路径不能为空。", nameof(localJarPath));
        }

        await using var sync = session.CreateSyncClient();
        await sync.PushAsync(localJarPath, ScrcpyConstants.ServerDevicePath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the server process and returns its long-lived shell stream.
    /// <para>启动服务端进程并返回其长驻 shell 流。</para>
    /// </summary>
    /// <param name="session">An established ADB session. <para>已建立的 ADB 会话。</para></param>
    /// <param name="options">Server launch options. <para>服务端启动选项。</para></param>
    /// <returns>
    /// The shell stream carrying the server log; keep it open while mirroring.
    /// <para>承载服务端日志的 shell 流；投屏期间保持打开。</para>
    /// </returns>
    internal static IAdbStream StartServer(IAdbDeviceSession session, ScrcpyServerOptions options)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        ArgumentNullException.ThrowIfNull(options);

        return session.OpenStream("shell:" + options.BuildShellCommand());
    }

    /// <summary>
    /// Forces any running scrcpy server process to exit.
    /// <para>强制退出设备上正在运行的 scrcpy 服务端进程。</para>
    /// </summary>
    /// <param name="session">An established ADB session. <para>已建立的 ADB 会话。</para></param>
    /// <param name="cancellationToken">Cancels the reply wait. <para>取消应答等待。</para></param>
    /// <returns>A task that completes once the kill command has finished or its reply wait expired.
    /// <para>结束命令执行完毕，或其应答等待到期后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    internal static async Task StopServerAsync(IAdbDeviceSession session, CancellationToken cancellationToken = default)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReplyWaitMilliseconds);
        await using var stream = session.OpenStream("shell:pkill -f " + ScrcpyConstants.ServerMainClass);
        while (true)
        {
            try
            {
                if (await stream.ReadAsync(timeout.Token).ConfigureAwait(false) is null)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Reads the trailing server log from the shell stream (non-blocking drain).
    /// <para>从 shell 流读取服务端日志尾部（非阻塞排空）。</para>
    /// </summary>
    /// <param name="shellStream">The stream returned by <see cref="StartServer"/>. <para><see cref="StartServer"/> 返回的流。</para></param>
    /// <returns>Log text read so far. <para>当前读到的日志文本。</para></returns>
    internal static string DrainLog(IAdbStream shellStream)
    {
        var sb = new StringBuilder();
        while (shellStream.TryRead(out var chunk))
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }

        return sb.ToString();
    }
}