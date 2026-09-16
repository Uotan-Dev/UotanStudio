using System.Text;
using UotanStudio.Comm.Protocols.Adb;

namespace UotanStudio.Comm.Protocols.Scrcpy;

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
public static class ScrcpyServerManager
{
    /// <summary>
    /// Pushes the scrcpy server jar to the device.
    /// <para>将 scrcpy 服务端 jar 推送到设备。</para>
    /// </summary>
    /// <param name="session">An established ADB session. 已建立的 ADB 会话。</param>
    /// <param name="localJarPath">Path of the bundled <c>scrcpy-server</c> jar. 随附 <c>scrcpy-server</c> jar 的本地路径。</param>
    public static void PushServer(AdbSession session, string localJarPath)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (string.IsNullOrEmpty(localJarPath))
        {
            throw new ArgumentException("Server jar path is required. 服务端 jar 路径不能为空。", nameof(localJarPath));
        }

        using var sync = session.CreateSyncClient();
        sync.Push(localJarPath, ScrcpyConstants.ServerDevicePath);
    }

    /// <summary>
    /// Starts the server process and returns its long-lived shell stream.
    /// <para>启动服务端进程并返回其长驻 shell 流。</para>
    /// </summary>
    /// <param name="session">An established ADB session. 已建立的 ADB 会话。</param>
    /// <param name="options">Server launch options. 服务端启动选项。</param>
    /// <returns>
    /// The shell stream carrying the server log; keep it open while mirroring.
    /// 承载服务端日志的 shell 流；投屏期间保持打开。
    /// </returns>
    public static AdbStream StartServer(AdbSession session, ScrcpyServerOptions options)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        ArgumentNullException.ThrowIfNull(options);

        return session.Connection.OpenStream("shell:" + options.BuildShellCommand());
    }

    /// <summary>
    /// Forces any running scrcpy server process to exit.
    /// <para>强制退出设备上正在运行的 scrcpy 服务端进程。</para>
    /// </summary>
    /// <param name="session">An established ADB session. 已建立的 ADB 会话。</param>
    public static void StopServer(AdbSession session)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        using var stream = session.Connection.OpenStream("shell:pkill -f " + ScrcpyConstants.ServerMainClass);
        while (stream.Read(3000) is not null)
        {
        }
    }

    /// <summary>
    /// Reads the trailing server log from the shell stream (non-blocking drain).
    /// <para>从 shell 流读取服务端日志尾部（非阻塞排空）。</para>
    /// </summary>
    /// <param name="shellStream">The stream returned by <see cref="StartServer"/>. <see cref="StartServer"/> 返回的流。</param>
    /// <returns>Log text read so far. 当前读到的日志文本。</returns>
    public static string DrainLog(AdbStream shellStream)
    {
        var sb = new StringBuilder();
        byte[]? chunk;
        while ((chunk = shellStream.Read(0)) is not null)
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }

        return sb.ToString();
    }
}