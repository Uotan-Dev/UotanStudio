using System.Text;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// Public entry point for the Apple trusted-tunnel plumbing that does not fit on the session
/// facade; hosts register platform native libraries here before starting a tunnel.
/// <para>Apple 受信隧道相关、不适合放在会话门面上的公共入口；宿主在启动隧道前在此注册平台原生库。</para>
/// </summary>
public static class AppleTunnel
{
    /// <summary>
    /// Registers the absolute path of <c>wintun.dll</c> and pre-loads it, taking precedence over the
    /// file next to the application. Call before <see cref="AppleTunnelSession.Start"/> when the
    /// DLL is not deployed next to the executable.
    /// <para>注册 <c>wintun.dll</c> 的绝对路径并预加载，优先于程序目录中的同名文件。当 DLL 未随
    /// 可执行文件部署时，在 <see cref="AppleTunnelSession.Start"/> 之前调用。</para>
    /// </summary>
    /// <param name="libraryPath">The absolute path to <c>wintun.dll</c>.
    /// <para><c>wintun.dll</c> 的绝对路径。</para></param>
    /// <exception cref="ArgumentException">The path is blank. <para>路径为空白时抛出。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist. <para>文件不存在时抛出。</para></exception>
    public static void SetWintunLibraryPath(string libraryPath) =>
        WintunNativeLoader.SetLibraryPath(libraryPath);
}

/// <summary>
/// One established Apple trusted tunnel (iOS 17.4+): owns the CoreDevice tunnel over the
/// <c>CoreDeviceProxy</c> service, the platform TUN device and the RemoteServiceDiscovery client;
/// exposes the tunnel-advertised services and opens shim service streams.
/// <para>一条已建立的 Apple 受信隧道（iOS 17.4+）：持有 CoreDeviceProxy 服务上的 CoreDevice 隧道、
/// 平台 TUN 设备与 RemoteServiceDiscovery 客户端；暴露隧道宣告的服务并打开 shim 服务流。</para>
/// </summary>
public sealed class AppleTunnelSession : IDisposable
{
    private readonly IAppleDeviceSession _session;
    private readonly Lock _lock = new();
    private CoreDeviceTunnel? _tunnel;
    private RemoteServiceDiscoveryClient? _discovery;
    private bool _disposed;

    private AppleTunnelSession(IAppleDeviceSession session, CoreDeviceTunnel tunnel,
        RemoteServiceDiscoveryClient discovery) =>
        (_session, _tunnel, _discovery) = (session, tunnel, discovery);

    /// <summary>Gets the advertised services keyed by name with their TCP port and properties.
    /// <para>获取按名称索引的服务表，含 TCP 端口与属性。</para></summary>
    /// <exception cref="ObjectDisposedException">The session was disposed.
    /// <para>会话已释放时抛出。</para></exception>
    public IReadOnlyDictionary<string, (int Port, IReadOnlyDictionary<string, object?> Properties)> Services
    {
        get
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _discovery!.Services;
            }
        }
    }

    /// <summary>
    /// Starts a tunnel for one device: opens the CoreDeviceProxy lockdown service, performs the
    /// CDTunnel handshake, brings up the platform TUN device (administrator/root rights required)
    /// and completes the RemoteServiceDiscovery handshake over it.
    /// <para>为一台设备启动隧道：打开 CoreDeviceProxy lockdown 服务、执行 CDTunnel 握手、拉起平台
    /// TUN 设备（需要管理员/root 权限）并经其完成 RemoteServiceDiscovery 握手。</para>
    /// </summary>
    /// <param name="session">The connected Apple device session; the caller keeps owning it and it
    /// must outlive the returned instance. <para>已连接的 Apple 设备会话；由调用方持有，且必须比
    /// 返回实例存活更久。</para></param>
    /// <param name="cancellationToken">Cancels the startup. <para>取消启动。</para></param>
    /// <returns>The established tunnel session. <para>已建立的隧道会话。</para></returns>
    /// <exception cref="IOException">The device rejected the tunnel or a platform tunnel device
    /// could not be created. <para>设备拒绝隧道或平台 TUN 设备无法创建时抛出。</para></exception>
    /// <exception cref="PlatformNotSupportedException">The host OS has no TUN backend yet.
    /// <para>宿主 OS 尚无 TUN 后端时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public static AppleTunnelSession Start(IAppleDeviceSession session, CancellationToken cancellationToken = default)
    {
        var stream = session.OpenService(AppleConstants.ServiceCoreDeviceProxy, cancellationToken);
        CoreDeviceTunnel? tunnel = null;
        try
        {
            tunnel = new CoreDeviceTunnel(stream);
            var result = tunnel.Establish(BuildInterfaceName(session.Serial), cancellationToken);
            var discovery = RemoteServiceDiscoveryClient.Connect(
                result.ServerAddress, result.ServerRsdPort, cancellationToken);
            return new AppleTunnelSession(session, tunnel, discovery);
        }
        catch
        {
            if (tunnel is null)
            {
                stream.Dispose();
            }
            else
            {
                tunnel.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Opens a tunnel-advertised service as a raw TCP stream, for example the
    /// <see cref="AppleConstants.ServiceSyslogRelayShimRemote"/> shim.
    /// <para>以原始 TCP 流打开隧道宣告的服务，例如 <see cref="AppleConstants.ServiceSyslogRelayShimRemote"/>
    /// shim。</para>
    /// </summary>
    /// <param name="serviceName">The service name advertised in <see cref="Services"/>.
    /// <para><see cref="Services"/> 中宣告的服务名。</para></param>
    /// <param name="cancellationToken">Cancels the connect. <para>取消连接。</para></param>
    /// <returns>The connected service stream. <para>已连接的服务流。</para></returns>
    /// <exception cref="KeyNotFoundException">The device does not offer the service.
    /// <para>设备未提供该服务时抛出。</para></exception>
    /// <exception cref="IOException">The service port refused the connection.
    /// <para>服务端口拒绝连接时抛出。</para></exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.
    /// <para>会话已释放时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public Stream OpenService(string serviceName, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _discovery!.OpenServiceStream(serviceName, cancellationToken);
        }
    }

    /// <summary>
    /// Releases the RemoteServiceDiscovery client, the packet pumps and the TUN device in that
    /// order; safe to call repeatedly.
    /// <para>按该顺序释放 RemoteServiceDiscovery 客户端、报文泵与 TUN 设备；可安全重复调用。</para>
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _discovery?.Dispose();
            _tunnel?.Dispose();
        }
    }

    private static string BuildInterfaceName(string serial)
    {
        var builder = new StringBuilder("uotan-".Length + serial.Length);
        builder.Append("uotan-");
        foreach (var character in serial)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        return builder.ToString();
    }
}