using Makaretu.Dns;
using System.Collections.Concurrent;

namespace UotanStudio.Comm.Transports.Udp;

/// <summary>
/// Discovers ADB devices on the local network via mDNS, implemented on the NuGet
/// package <c>Makaretu.Dns.Multicast</c> (pure managed .NET multicast DNS) — no
/// external adb binary, no adb server, no proxy. This mirrors the official
/// `adb mdns services` behavior.
/// <para>通过 mDNS 在本地网络发现 ADB 设备：基于 NuGet 包
/// <c>Makaretu.Dns.Multicast</c>（纯托管 .NET 组播 DNS）实现，不依赖外部 adb
/// 二进制、adb server 或代理，对应官方 `adb mdns services` 的行为。</para>
/// </summary>
public static class AdbMdnsDiscovery
{
    /// <summary>
    /// mDNS IPv4 multicast group address.
    /// <para>mDNS IPv4 组播组地址。</para>
    /// </summary>
    public const string MulticastGroup = "224.0.0.251";

    /// <summary>
    /// mDNS port.
    /// <para>mDNS 端口。</para>
    /// </summary>
    public const int MdnsPort = 5353;

    /// <summary>
    /// adbd TLS-connect service type (Android 11+).
    /// <para>adbd TLS 连接服务类型（Android 11+）。</para>
    /// </summary>
    public const string AdbTlsService = "_adb-tls-connect._tcp.local";

    /// <summary>
    /// Legacy adb service type.
    /// <para>旧版 adb 服务类型。</para>
    /// </summary>
    public const string AdbLegacyService = "_adb._tcp.local";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Sends mDNS PTR queries for the adb service types, collects the multicast
    /// answers until the timeout elapses, and returns the discovered devices.
    /// <para>发送 adb 服务类型的 mDNS PTR 查询，在超时前收集组播应答，并返回发现的设备。</para>
    /// </summary>
    /// <param name="timeout">Collection window; defaults to 3 seconds. 收集窗口，默认 3 秒。</param>
    /// <param name="ct">Cancellation token. 取消令牌。</param>
    /// <returns>The discovered devices. 发现的设备列表。</returns>
    public static async Task<IReadOnlyList<MdnsDevice>> DiscoverAdbDevicesAsync(
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var byInstance = new ConcurrentDictionary<string, DeviceAccumulator>(StringComparer.OrdinalIgnoreCase);
        var addressesByHost = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var mdns = new MulticastService();
        using var sd = new ServiceDiscovery(mdns);

        mdns.AnswerReceived += (s, e) => Accumulate(e.Message, byInstance, addressesByHost);
        mdns.NetworkInterfaceDiscovered += (s, e) =>
        {
            sd.QueryServiceInstances(new DomainName(AdbTlsService));
            sd.QueryServiceInstances(new DomainName(AdbLegacyService));
        };

        mdns.Start();

        try
        {
            await Task.Delay(effectiveTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled — return whatever was found so far.
        }

        return BuildDevices(byInstance, addressesByHost);
    }

    /// <summary>
    /// Accumulates discovered records: PTR marks adb instances, SRV carries the
    /// port/host, TXT carries properties, A/AAAA carry addresses.
    /// <para>累积发现的记录：PTR 标记 adb 实例，SRV 携带端口/主机，
    /// TXT 携带属性，A/AAAA 携带地址。</para>
    /// </summary>
    internal static void Accumulate(
        Message message,
        ConcurrentDictionary<string, DeviceAccumulator> byInstance,
        ConcurrentDictionary<string, string> addressesByHost)
    {
        foreach (var record in message.Answers)
        {
            var name = record.Name.ToString();
            switch (record)
            {
                case PTRRecord ptr when IsAdbService(name):
                    byInstance.GetOrAdd(ptr.DomainName.ToString(), _ => new DeviceAccumulator()).IsAdb = true;
                    break;
                case SRVRecord srv:
                    var srvAcc = byInstance.GetOrAdd(name, _ => new DeviceAccumulator());
                    srvAcc.Port = srv.Port;
                    srvAcc.HostName = srv.Target.ToString();
                    break;
                case TXTRecord txt:
                    var txtAcc = byInstance.GetOrAdd(name, _ => new DeviceAccumulator());
                    if (txt.Strings is not null)
                    {
                        foreach (var kv in txt.Strings)
                        {
                            var eq = kv.IndexOf('=');
                            if (eq > 0)
                            {
                                txtAcc.Properties[kv[..eq]] = kv[(eq + 1)..];
                            }
                            else if (kv.Length > 0)
                            {
                                txtAcc.Properties[kv] = string.Empty;
                            }
                        }
                    }

                    break;
                case ARecord aRecord:
                    addressesByHost[name] = aRecord.Address.ToString();
                    break;
                case AAAARecord aaaaRecord:
                    addressesByHost[name] = aaaaRecord.Address.ToString();
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Builds the final device list from the accumulated state, joining each adb
    /// instance with its address (matched via the SRV host or the instance name).
    /// <para>从累积状态构建最终设备列表，将每个 adb 实例与其地址关联
    /// （通过 SRV 主机或实例名匹配）。</para>
    /// </summary>
    internal static IReadOnlyList<MdnsDevice> BuildDevices(
        ConcurrentDictionary<string, DeviceAccumulator> byInstance,
        ConcurrentDictionary<string, string> addressesByHost)
    {
        var devices = new List<MdnsDevice>();
        foreach (var pair in byInstance)
        {
            var acc = pair.Value;
            if (!acc.IsAdb)
            {
                continue;
            }

            var address = acc.HostName is not null && addressesByHost.TryGetValue(acc.HostName, out var byHost)
                ? byHost
                : addressesByHost.TryGetValue(pair.Key, out var byInstanceName)
                    ? byInstanceName
                    : null;

            devices.Add(new MdnsDevice(
                pair.Key,
                acc.HostName,
                acc.Port > 0 ? acc.Port : 5555,
                address is null ? Array.Empty<string>() : [address],
                acc.Properties));
        }

        return devices;
    }

    /// <summary>
    /// Per-instance state accumulated from mDNS records.
    /// <para>从 mDNS 记录累积的每个实例的状态。</para>
    /// </summary>
    internal sealed class DeviceAccumulator
    {
        /// <summary>Whether the instance is an adb service. 该实例是否为 adb 服务。</summary>
        public bool IsAdb { get; set; }

        /// <summary>The SRV port. SRV 端口。</summary>
        public int Port { get; set; }

        /// <summary>The SRV target host name. SRV 目标主机名。</summary>
        public string? HostName { get; set; }

        /// <summary>The TXT-record properties. TXT 记录属性。</summary>
        public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAdbService(string name)
        => name.EndsWith(AdbTlsService, StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(AdbLegacyService, StringComparison.OrdinalIgnoreCase);
}