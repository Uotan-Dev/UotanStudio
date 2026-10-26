namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// One userspace-routed IPv6 tunnel interface carrying raw IPv6 packets for an Apple CoreDevice
/// tunnel; implementations own the platform TUN adapter and are not thread-safe for concurrent
/// receives.
/// <para>承载 Apple CoreDevice 隧道原始 IPv6 报文的用户态路由隧道接口；各实现持有平台 TUN 适配器，
/// 且不支持并发接收。</para>
/// </summary>
internal interface ITunDevice : IDisposable
{
    /// <summary>Gets the operating-system interface name. <para>获取操作系统接口名。</para></summary>
    string Name { get; }

    /// <summary>
    /// Writes one raw IPv6 packet into the interface (host egress towards the device).
    /// <para>向接口写入一个原始 IPv6 报文（主机发往设备的出向流量）。</para>
    /// </summary>
    /// <param name="ipv6Packet">The complete IPv6 packet including its fixed header.
    /// <para>含固定头的完整 IPv6 报文。</para></param>
    void SendPacket(ReadOnlySpan<byte> ipv6Packet);

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for one inbound packet and copies it out.
    /// <para>最多等待 <paramref name="timeoutMs"/> 以取回一个入站报文并拷贝出来。</para>
    /// </summary>
    /// <param name="timeoutMs">The wait budget in milliseconds. <para>等待预算（毫秒）。</para></param>
    /// <returns>The raw IPv6 packet, or <see langword="null"/> when nothing arrived in time.
    /// <para>原始 IPv6 报文；超时未到达为 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The interface failed or was closed.
    /// <para>接口失败或已关闭时抛出。</para></exception>
    byte[]? TryReceivePacket(int timeoutMs);
}

/// <summary>
/// Opens the platform TUN device for an Apple tunnel, dispatching on the host operating system.
/// <para>按宿主操作系统分派，打开 Apple 隧道所需的平台 TUN 设备。</para>
/// </summary>
internal static class TunDeviceFactory
{
    /// <summary>
    /// Opens a TUN device and configures the negotiated client address, prefix and MTU on it.
    /// <para>打开 TUN 设备并在其上配置协商出的本端地址、前缀长度与 MTU。</para>
    /// </summary>
    /// <param name="name">The requested interface name; platforms may truncate it.
    /// <para>请求的接口名；平台可能截断。</para></param>
    /// <param name="clientAddress">The IPv6 address negotiated with the device.
    /// <para>与设备协商出的本端 IPv6 地址。</para></param>
    /// <param name="prefixLength">The on-link prefix length of the address.
    /// <para>地址的链路本地前缀长度。</para></param>
    /// <param name="cancellationToken">Cancels the configuration. <para>取消配置。</para></param>
    /// <param name="mtu">The negotiated interface MTU; zero keeps the platform default.
    /// <para>协商的接口 MTU；零保持平台默认。</para></param>
    /// <returns>The opened device. <para>已打开的设备。</para></returns>
    /// <exception cref="PlatformNotSupportedException">The host OS has no tunnel backend yet.
    /// <para>宿主 OS 尚无隧道后端时抛出。</para></exception>
    /// <exception cref="IOException">The adapter or its configuration failed.
    /// <para>适配器或其配置失败时抛出。</para></exception>
    public static ITunDevice Open(string name, string clientAddress, int prefixLength,
        CancellationToken cancellationToken, int mtu = 0)
    {
        if (OperatingSystem.IsWindows())
        {
            return WintunTunnelDevice.Open(name, clientAddress, prefixLength, mtu);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxTunDevice.Open(name, clientAddress, prefixLength, mtu, cancellationToken);
        }

        return NotSupportedTunDevice.Open();
    }
}

/// <summary>
/// The macOS placeholder backend; utun integration is not implemented yet.
/// <para>macOS 占位后端；utun 集成尚未实现。</para>
/// </summary>
internal sealed class NotSupportedTunDevice : ITunDevice
{
    private NotSupportedTunDevice()
    {
    }

    /// <summary>Gets the placeholder interface name. <para>获取占位接口名。</para></summary>
    public string Name => "utun-unavailable";

    /// <inheritdoc/>
    public void SendPacket(ReadOnlySpan<byte> ipv6Packet) => Throw();

    /// <inheritdoc/>
    public byte[]? TryReceivePacket(int timeoutMs) => Throw();

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private static byte[]? Throw() => throw new PlatformNotSupportedException(
        "utun (macOS) tunnel devices are not implemented yet; the Apple tunnel requires Windows (wintun) or Linux (/dev/net/tun).");

    internal static NotSupportedTunDevice Open() => new();
}