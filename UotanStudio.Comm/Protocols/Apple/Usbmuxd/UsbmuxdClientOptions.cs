namespace UotanStudio.Comm.Protocols.Apple.Usbmuxd;

/// <summary>How the usbmuxd endpoint is resolved. <para>usbmuxd 端点的解析方式。</para></summary>
public enum UsbmuxdEndpointKind
{
    /// <summary>Platform default: TCP 127.0.0.1:27015 on Windows, the Unix socket elsewhere; the
    /// <c>USBMUXD_SOCKET_ADDRESS</c> environment variable overrides both.
    /// <para>平台默认：Windows 走 TCP 127.0.0.1:27015，其余平台走 Unix 套接字；环境变量
    /// <c>USBMUXD_SOCKET_ADDRESS</c> 可覆盖两者。</para></summary>
    Auto = 0,

    /// <summary>Connect over TCP to <see cref="UsbmuxdClientOptions.TcpHost"/>:<see cref="UsbmuxdClientOptions.TcpPort"/>. <para>以 TCP 连接 <see cref="UsbmuxdClientOptions.TcpHost"/>:<see cref="UsbmuxdClientOptions.TcpPort"/>。</para></summary>
    Tcp = 1,

    /// <summary>Connect to a Unix domain socket at <see cref="UsbmuxdClientOptions.UnixSocketPath"/>. <para>连接 <see cref="UsbmuxdClientOptions.UnixSocketPath"/> 处的 Unix 域套接字。</para></summary>
    UnixSocket = 2,
}

/// <summary>
/// Endpoint configuration for talking to the host usbmuxd daemon.
/// <para>与主机 usbmuxd 守护进程通信的端点配置。</para>
/// </summary>
/// <param name="Kind">The endpoint resolution mode. <para>端点解析模式。</para></param>
/// <param name="TcpHost">TCP host used by <see cref="UsbmuxdEndpointKind.Tcp"/>. <para><see cref="UsbmuxdEndpointKind.Tcp"/> 模式使用的主机名。</para></param>
/// <param name="TcpPort">TCP port used by <see cref="UsbmuxdEndpointKind.Tcp"/>. <para><see cref="UsbmuxdEndpointKind.Tcp"/> 模式使用的端口。</para></param>
/// <param name="UnixSocketPath">Socket path used by <see cref="UsbmuxdEndpointKind.UnixSocket"/>. <para><see cref="UsbmuxdEndpointKind.UnixSocket"/> 模式使用的套接字路径。</para></param>
public sealed record UsbmuxdClientOptions(
    UsbmuxdEndpointKind Kind = UsbmuxdEndpointKind.Auto,
    string? TcpHost = null,
    int? TcpPort = null,
    string? UnixSocketPath = null)
{
    /// <summary>Gets the default options. <para>获取默认配置。</para></summary>
    public static UsbmuxdClientOptions Default { get; } = new();

    /// <summary>Gets the TCP host; the platform default applies when unset. <para>获取 TCP 主机名；未设置时使用平台默认。</para></summary>
    public string? TcpHost { get; init; } = TcpHost;

    /// <summary>Gets the TCP port; the platform default applies when unset. <para>获取 TCP 端口；未设置时使用平台默认。</para></summary>
    public int? TcpPort { get; init; } = TcpPort;

    /// <summary>Gets the Unix socket path; the platform default applies when unset. <para>获取 Unix 套接字路径；未设置时使用平台默认。</para></summary>
    public string? UnixSocketPath { get; init; } = UnixSocketPath;
}