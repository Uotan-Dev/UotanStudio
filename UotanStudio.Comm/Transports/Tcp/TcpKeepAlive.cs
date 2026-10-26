using System.Net.Sockets;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// Enables TCP keepalive on a connected socket so a peer that vanished without closing its connection is eventually
/// observed as a socket failure instead of leaving an unbounded read blocked forever. This is the liveness backstop for
/// the ADB transports: their reads wait indefinitely for the next frame, because an idle link is healthy and a TLS
/// record layer cannot survive an interrupted read.
/// <para>在已连接的套接字上启用 TCP keepalive，使未关闭连接就消失的对端最终表现为套接字故障，而不是让无限读取永远阻塞。
/// 这是 ADB 传输的存活兜底：空闲链路本身健康、TLS 记录层也无法承受被打断的读取，因此读取会无限等待下一帧。</para>
/// </summary>
internal static class TcpKeepAlive
{
    /// <summary>Idle time before the first probe. <para>首次探测前的空闲时长。</para></summary>
    private const int IdleSeconds = 30;

    /// <summary>Interval between probes of an unanswered peer. <para>对端未应答时的探测间隔。</para></summary>
    private const int IntervalSeconds = 10;

    /// <summary>Unanswered probes after which the socket is reported as failed. <para>套接字被判定失败前允许的无应答探测次数。</para></summary>
    private const int RetryCount = 3;

    /// <summary>
    /// Turns keepalive on and applies the probe cadence where the platform accepts it; platforms that expose only the
    /// on/off switch keep their default cadence. The call is best-effort: keepalive is a liveness aid, never a
    /// precondition for using the connection.
    /// <para>启用 keepalive 并在平台接受时设置探测节奏；只暴露开关的平台保留默认节奏。该调用是尽力而为的：keepalive
    /// 只是存活辅助，绝不是使用连接的前提。</para>
    /// </summary>
    /// <param name="socket">The connected socket. <para>已连接的套接字。</para></param>
    public static void Enable(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception ex) when (IsExpectedPlatformFailure(ex))
        {
            return;
        }

        TrySet(socket, SocketOptionName.TcpKeepAliveTime, IdleSeconds);
        TrySet(socket, SocketOptionName.TcpKeepAliveInterval, IntervalSeconds);
        TrySet(socket, SocketOptionName.TcpKeepAliveRetryCount, RetryCount);
    }

    private static void TrySet(Socket socket, SocketOptionName option, int value)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, option, value);
        }
        catch (Exception ex) when (IsExpectedPlatformFailure(ex))
        {
        }
    }

    private static bool IsExpectedPlatformFailure(Exception exception)
        => exception is SocketException or PlatformNotSupportedException or NotSupportedException
            or ObjectDisposedException;

    /// <summary>
    /// Enables keepalive on a client that just connected, ignoring every platform that does not support the options.
    /// <para>为刚完成连接的客户端启用 keepalive，并忽略不支持这些选项的平台。</para>
    /// </summary>
    /// <param name="client">The connected client. <para>已连接的客户端。</para></param>
    public static void Enable(TcpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Enable(client.Client);
    }
}