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
/// <param name="serviceType">The service type the instance was advertised under, when known.
/// 广播该实例的服务类型（已知时）。</param>
/// <param name="lastSeenUtc">When the instance's SRV record was last received, so a re-advertised service
/// outranks a stale one the responder still lists.
/// 该实例的 SRV 记录最后被收到的时刻，使重新广播的服务优先于应答方仍列出的旧服务。</param>
internal sealed class MdnsDevice(
    string instance,
    string? hostName,
    int port,
    IReadOnlyList<string> addresses,
    IReadOnlyDictionary<string, string> properties,
    string? serviceType = null,
    DateTime lastSeenUtc = default)
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
    /// Gets the service TCP port, taken from the SRV record when browsed locally and from the
    /// server-resolved row when read from the adb server's mDNS table.
    /// <para>获取服务 TCP 端口：本地浏览时取自 SRV 记录，读取 adb server 的 mDNS 表时取自 server 已解析的行。</para>
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

    /// <summary>
    /// Gets the service type the instance was advertised under, or <c>null</c> when the source does not
    /// report one.
    /// <para>获取广播该实例的服务类型；来源未报告时为 <c>null</c>。</para>
    /// </summary>
    public string? ServiceType { get; } = serviceType;

    /// <summary>
    /// Gets when this instance's SRV record was last received, or <see cref="DateTime.MinValue"/> when the
    /// source cannot report it (a server mDNS row).
    /// <para>获取该实例 SRV 记录最后被收到的时刻；来源无法报告时（server 的 mDNS 行）为
    /// <see cref="DateTime.MinValue"/>。</para>
    /// </summary>
    public DateTime LastSeenUtc { get; } = lastSeenUtc;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Instance} {string.Join(",", Addresses)}:{Port}";
}