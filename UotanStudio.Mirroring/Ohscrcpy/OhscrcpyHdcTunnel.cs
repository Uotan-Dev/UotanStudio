using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Tcp;

namespace UotanStudio.Mirroring;

/// <summary>
/// Opens the ohscrcpy device port through a Comm-owned HDC forward tunnel. The loopback socket, the
/// free-port choice and the forward teardown stay in Comm; this class only adapts that tunnel to the
/// mirroring client.
/// <para>经 Comm 持有的 HDC 转发通道打开 ohscrcpy 设备端口。回环套接字、空闲端口选择与转发清理都留在 Comm，
/// 本类只把该通道适配给投屏客户端。</para>
/// </summary>
internal sealed class OhscrcpyHdcTunnel : IOhscrcpyTunnel
{
    private readonly HdcForwardTunnel _tunnel;

    /// <summary>
    /// Creates a tunnel over an already connected HDC session.
    /// <para>在已连接的 HDC 会话上创建通道。</para>
    /// </summary>
    /// <param name="session">Connected HDC session owning the forward or server channel.
    /// <para>持有转发或 server 通道的已连接 HDC 会话。</para></param>
    /// <param name="connectKey">Target connect key used by the server-backed route.
    /// <para>server 路由使用的目标连接键。</para></param>
    /// <param name="requestedLocalPort">Preferred loopback port; zero picks a free port.
    /// <para>首选回环端口；为零时自动选择空闲端口。</para></param>
    /// <param name="timeoutMs">Budget for registering the forward and connecting the loopback socket.
    /// <para>登记转发与连接回环套接字的预算。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    internal OhscrcpyHdcTunnel(HdcSession session, string connectKey, int requestedLocalPort = 0, int timeoutMs = 20_000)
    {
        _tunnel = new HdcForwardTunnel(session, connectKey, OhscrcpyConstants.DevicePort,
            requestedLocalPort, timeoutMs);
    }

    /// <summary>Registers the forward when needed and returns the connected loopback stream.
    /// <para>按需登记转发并返回已连接的回环流。</para></summary>
    /// <param name="cancellationToken">Cancels the forward setup and connection retries.
    /// <para>取消转发建立与连接重试。</para></param>
    /// <returns>The connected stream. <para>已连接的流。</para></returns>
    public Stream Open(CancellationToken cancellationToken = default) => _tunnel.Open(cancellationToken);

    /// <summary>Closes the loopback client and removes the forward.
    /// <para>关闭回环客户端并移除转发。</para></summary>
    public void Dispose() => _tunnel.Dispose();
}