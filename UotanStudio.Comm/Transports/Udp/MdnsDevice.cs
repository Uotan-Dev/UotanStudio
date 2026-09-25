namespace UotanStudio.Comm.Transports.Udp;

/// <summary>
/// A device discovered on the local network via mDNS.
/// <para>通过 mDNS 在本地网络发现的设备。</para>
/// </summary>
/// <remarks>
/// Initializes a new discovered device.
/// <para>初始化一个新的发现设备。</para>
/// </remarks>
/// <param name="instance">The full service instance name. 完整服务实例名。</param>
/// <param name="hostName">The SRV target host name. SRV 目标主机名。</param>
/// <param name="port">The service TCP port. 服务 TCP 端口。</param>
/// <param name="addresses">The resolved IP addresses. 解析出的 IP 地址。</param>
/// <param name="properties">The TXT-record properties. TXT 记录属性。</param>
internal sealed class MdnsDevice(
    string instance,
    string? hostName,
    int port,
    IReadOnlyList<string> addresses,
    IReadOnlyDictionary<string, string> properties)
{
    /// <summary>
    /// Gets the full service instance name, e.g. "ALN-AL00._adb-tls-connect._tcp.local".
    /// <para>获取完整服务实例名，如 "ALN-AL00._adb-tls-connect._tcp.local"。</para>
    /// </summary>
    public string Instance { get; } = instance ?? throw new ArgumentNullException(nameof(instance));

    /// <summary>
    /// Gets the SRV target host name, e.g. "ALN-AL00.local".
    /// <para>获取 SRV 目标主机名，如 "ALN-AL00.local"。</para>
    /// </summary>
    public string? HostName { get; } = hostName;

    /// <summary>
    /// Gets the service TCP port from the SRV record (defaults to 5555).
    /// <para>获取 SRV 记录中的服务 TCP 端口（默认 5555）。</para>
    /// </summary>
    public int Port { get; } = port;

    /// <summary>
    /// Gets the resolved IP addresses for the device.
    /// <para>获取解析出的设备 IP 地址。</para>
    /// </summary>
    public IReadOnlyList<string> Addresses { get; } = addresses ?? throw new ArgumentNullException(nameof(addresses));

    /// <summary>
    /// Gets the TXT-record properties (e.g. "model", "device", "serial").
    /// <para>获取 TXT 记录属性（如 "model"、"device"、"serial"）。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; } = properties ?? throw new ArgumentNullException(nameof(properties));

    /// <inheritdoc />
    public override string ToString() =>
        $"{Instance} {string.Join(",", Addresses)}:{Port}";
}