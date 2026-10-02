using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Makaretu.Dns;

namespace UotanStudio.Comm.Transports.Udp;

/// <summary>
/// Discovers ADB devices on the local network via mDNS, implemented on the NuGet
/// package <c>Makaretu.Dns.Multicast</c> (pure managed .NET multicast DNS) — no
/// external adb binary, no adb server, no proxy. This mirrors the official
/// `adb mdns services` behavior, covering the Android 11+ TLS pairing/connect
/// services and the legacy adb service.
/// <para>通过 mDNS 在本地网络发现 ADB 设备：基于 NuGet 包
/// <c>Makaretu.Dns.Multicast</c>（纯托管 .NET 组播 DNS）实现，不依赖外部 adb
/// 二进制、adb server 或代理，对应官方 `adb mdns services` 的行为，覆盖
/// Android 11+ 的 TLS 配对/连接服务与较早的 adb 服务。</para>
/// </summary>
/// <remarks>
/// <para>A browse keeps one <see cref="MulticastService"/> alive for its whole deadline and
/// re-queries on a fixed interval. A responder answers a PTR query with the instance record in the
/// answer section and the records needed to dial the service in the additional section, so the
/// missing pieces are fetched with follow-up SRV and address queries rather than by restarting the
/// socket; recreating that socket per poll would re-join the multicast group on every attempt and
/// drop the announcements received in between.</para>
/// <para>一次浏览在整个期限内保留同一个 <see cref="MulticastService"/>，并按固定间隔重新查询。应答方
/// 响应 PTR 查询时把实例记录放在应答段，把真正拨号所需的记录放在附加段，因此缺失部分靠后续的 SRV 与地址
/// 查询补齐，而不是重启套接字；每次轮询都重建套接字会在每轮重新加入组播组，并丢掉期间收到的广播。</para>
/// </remarks>
internal static class AdbMdnsDiscovery
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
    /// adbd TLS-pairing service type (Android 11+). The phone starts advertising it
    /// after a QR pairing code is scanned; the instance name equals the QR "S" field.
    /// <para>adbd TLS 配对服务类型（Android 11+）。手机扫描二维码配对码后开始广播，
    /// 实例名等于二维码的 "S" 字段。</para>
    /// </summary>
    public const string AdbTlsPairingService = "_adb-tls-pairing._tcp.local";

    /// <summary>
    /// Legacy adb service type.
    /// <para>较早 adbd 版本广播的 adb 服务类型。</para>
    /// </summary>
    public const string AdbLegacyService = "_adb._tcp.local";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MatchInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RequeryInterval = TimeSpan.FromSeconds(2);
    private const int MaxInstances = 64;
    private const int MaxAddresses = 128;

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
        => await BrowseAsync([AdbTlsService, AdbLegacyService], timeout ?? DefaultTimeout,
            static accumulator => accumulator.IsAdb, null, ct).ConfigureAwait(false);

    /// <summary>
    /// Polls the <c>_adb-tls-pairing._tcp</c> service until the instance with the
    /// expected name appears (the phone starts advertising after the QR code is
    /// scanned) and returns its pairing endpoint, or <c>null</c> when the deadline
    /// passes without a match.
    /// <para>轮询 <c>_adb-tls-pairing._tcp</c> 服务，直到出现期望名称的实例（手机
    /// 扫码后开始广播）并返回其配对端点；超过期限仍无匹配时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="instanceName">The expected instance label, i.e. the QR "S" field.
    /// <para>期望的实例标签，即二维码的 "S" 字段。</para></param>
    /// <param name="timeout">Overall wait deadline. <para>整体等待期限。</para></param>
    /// <param name="ct">Cancellation token. 取消令牌。</param>
    /// <returns>The pairing service instance, or <c>null</c> on timeout.
    /// <para>配对服务实例；超时返回 <c>null</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="instanceName"/> is blank.
    /// <para><paramref name="instanceName"/> 为空。</para></exception>
    public static async Task<MdnsDevice?> WaitForPairingServiceAsync(
        string instanceName, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        return await WaitForServiceAsync(AdbTlsPairingService,
            device => MatchesInstanceLabel(device.Instance, instanceName), timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls the <c>_adb-tls-connect._tcp</c> service until an instance resolves to
    /// the supplied device IP address and returns it (the secure connect port), or
    /// <c>null</c> when the deadline passes without a match.
    /// <para>轮询 <c>_adb-tls-connect._tcp</c> 服务，直到某个实例解析到给定设备 IP
    /// 并返回该实例（安全连接端口）；超过期限仍无匹配时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="address">The device IP address discovered during pairing.
    /// <para>配对阶段发现的设备 IP 地址。</para></param>
    /// <param name="timeout">Overall wait deadline. <para>整体等待期限。</para></param>
    /// <param name="ct">Cancellation token. 取消令牌。</param>
    /// <returns>The TLS connect service instance, or <c>null</c> on timeout.
    /// <para>TLS 连接服务实例；超时返回 <c>null</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="address"/> is blank.
    /// <para><paramref name="address"/> 为空。</para></exception>
    public static async Task<MdnsDevice?> WaitForConnectServiceAsync(
        string address, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        return await WaitForServiceAsync(AdbTlsService,
            device => device.Addresses.Any(candidate =>
                string.Equals(candidate, address, StringComparison.OrdinalIgnoreCase)), timeout, ct)
            .ConfigureAwait(false);
    }

    internal static async Task<MdnsDevice?> WaitForServiceAsync(
        string serviceType, Func<MdnsDevice, bool> predicate, TimeSpan? timeout, CancellationToken ct)
    {
        var devices = await BrowseAsync([serviceType], timeout ?? DefaultTimeout,
            static _ => true, predicate, ct).ConfigureAwait(false);
        return devices.FirstOrDefault(predicate);
    }

    /// <summary>
    /// Runs one mDNS browse session. With <paramref name="earlyExit"/> the session stops as soon as
    /// a built device satisfies it; without it the whole <paramref name="timeout"/> window is
    /// collected, which is what a device listing needs.
    /// <para>执行一次 mDNS 浏览会话。给出 <paramref name="earlyExit"/> 时，一旦某个已构建设备满足它即结束；
    /// 未给出时收集整个 <paramref name="timeout"/> 窗口，这是设备列表所需的行为。</para>
    /// </summary>
    /// <param name="serviceTypes">Service type names to browse. <para>要浏览的服务类型名。</para></param>
    /// <param name="timeout">Browse window. <para>浏览窗口。</para></param>
    /// <param name="filter">Per-instance gate applied while building devices. <para>构建设备时的实例级筛选。</para></param>
    /// <param name="earlyExit">Optional match that ends the window early. <para>可选的提前匹配。</para></param>
    /// <param name="ct">Cancellation token; cancellation propagates. <para>取消令牌；取消会被传播。</para></param>
    /// <returns>The devices known at exit. <para>退出时已知的设备。</para></returns>
    private static async Task<IReadOnlyList<MdnsDevice>> BrowseAsync(
        IReadOnlyList<string> serviceTypes, TimeSpan timeout, Func<DeviceAccumulator, bool> filter,
        Func<MdnsDevice, bool>? earlyExit, CancellationToken ct)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var byInstance = new ConcurrentDictionary<string, DeviceAccumulator>(StringComparer.OrdinalIgnoreCase);
        var addressesByHost = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var mdns = new MulticastService();
        using var discovery = new ServiceDiscovery(mdns);

        void QueryInstances()
        {
            foreach (var serviceType in serviceTypes)
            {
                discovery.QueryServiceInstances(new DomainName(serviceType));
            }
        }

        mdns.AnswerReceived += (_, e) => Accumulate(e.Message, byInstance, addressesByHost);
        mdns.NetworkInterfaceDiscovered += (_, _) => QueryInstances();
        mdns.Start();
        QueryInstances();

        var deadline = DateTime.UtcNow + timeout;
        var nextQuery = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var devices = BuildDevices(byInstance, addressesByHost, filter);
            if (earlyExit is not null)
            {
                var match = devices.FirstOrDefault(earlyExit);
                if (match is not null)
                {
                    return [match];
                }
            }

            var now = DateTime.UtcNow;
            if (now >= deadline)
            {
                return devices;
            }

            if (now >= nextQuery)
            {
                QueryInstances();
                ResolvePending(mdns, byInstance, addressesByHost);
                nextQuery = now + RequeryInterval;
            }

            var remaining = deadline - now;
            await Task.Delay(remaining > MatchInterval ? MatchInterval : remaining, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks for the records a partially resolved instance is still missing: SRV while the port is
    /// unknown, then A and AAAA for the SRV target and for the instance name, because responders
    /// differ on which name carries the address.
    /// <para>请求尚未完全解析的实例仍缺少的记录：端口未知时先要 SRV，随后向 SRV 目标和实例名要 A 与 AAAA，
    /// 因为不同应答方把地址挂在哪个名字上并不一致。</para>
    /// </summary>
    /// <param name="mdns">The live browse session. <para>活跃的浏览会话。</para></param>
    /// <param name="byInstance">Accumulated per-instance state. <para>已累积的实例状态。</para></param>
    /// <param name="addressesByHost">Accumulated address book. <para>已累积的地址表。</para></param>
    private static void ResolvePending(MulticastService mdns,
        ConcurrentDictionary<string, DeviceAccumulator> byInstance,
        ConcurrentDictionary<string, string> addressesByHost)
    {
        foreach (var pending in byInstance
                     .Where(entry => !IsFullyResolved(entry.Key, entry.Value, addressesByHost)).ToArray())
        {
            if (pending.Value.Port == 0)
            {
                mdns.SendQuery(new DomainName(pending.Key), DnsClass.IN, DnsType.SRV);
                continue;
            }

            if (pending.Value.HostName is { } hostName)
            {
                QueryAddress(mdns, hostName);
            }

            QueryAddress(mdns, pending.Key);
        }
    }

    private static void QueryAddress(MulticastService mdns, string name)
    {
        mdns.SendQuery(new DomainName(name), DnsClass.IN, DnsType.A);
        mdns.SendQuery(new DomainName(name), DnsClass.IN, DnsType.AAAA);
    }

    private static bool IsFullyResolved(string instance, DeviceAccumulator accumulator,
        ConcurrentDictionary<string, string> addressesByHost) =>
        accumulator.Port > 0 && HasAddressFor(instance, accumulator, addressesByHost);

    private static bool HasAddressFor(string instance, DeviceAccumulator accumulator,
        ConcurrentDictionary<string, string> addressesByHost) =>
        (accumulator.HostName is not null && addressesByHost.ContainsKey(accumulator.HostName)) ||
        addressesByHost.ContainsKey(instance);

    /// <summary>
    /// Accumulates discovered records: PTR marks adb instances, SRV carries the
    /// port/host, TXT carries properties, A/AAAA carry addresses.
    /// <para>累积发现的记录：PTR 标记 adb 实例，SRV 携带端口/主机，TXT 携带属性，A/AAAA 携带地址。</para>
    /// </summary>
    /// <remarks>
    /// <para>All three record sections are read. A standards-compliant responder puts the record it
    /// was asked about in the answer section and the rest of the resolution chain in the additional
    /// section, so reading only the answer section yields instance names with no port and no
    /// address.</para>
    /// <para>三个记录段都会被读取。合规的应答方把被查询的记录放在应答段，把解析链的其余部分放在附加段，
    /// 因此只读应答段只会得到没有端口、没有地址的实例名。</para>
    /// </remarks>
    /// <param name="message">The received mDNS message. <para>收到的 mDNS 报文。</para></param>
    /// <param name="byInstance">Accumulated per-instance state. <para>已累积的实例状态。</para></param>
    /// <param name="addressesByHost">Accumulated address book. <para>已累积的地址表。</para></param>
    internal static void Accumulate(
        Message message,
        ConcurrentDictionary<string, DeviceAccumulator> byInstance,
        ConcurrentDictionary<string, string> addressesByHost)
    {
        foreach (var record in message.Answers)
        {
            Accumulate(record, byInstance, addressesByHost);
        }

        foreach (var record in message.AuthorityRecords)
        {
            Accumulate(record, byInstance, addressesByHost);
        }

        foreach (var record in message.AdditionalRecords)
        {
            Accumulate(record, byInstance, addressesByHost);
        }
    }

    private static void Accumulate(
        ResourceRecord record,
        ConcurrentDictionary<string, DeviceAccumulator> byInstance,
        ConcurrentDictionary<string, string> addressesByHost)
    {
        var name = record.Name.ToString();
        switch (record)
        {
            case PTRRecord ptr when IsPairingService(name):
                if (Track(ptr.DomainName.ToString(), byInstance) is { } pairing)
                {
                    pairing.IsPairing = true;
                }

                break;
            case PTRRecord ptr when IsAdbService(name):
                if (Track(ptr.DomainName.ToString(), byInstance) is { } adb)
                {
                    adb.IsAdb = true;
                }

                break;
            case SRVRecord srv:
                if (Track(name, byInstance) is { } srvAcc)
                {
                    srvAcc.Port = srv.Port;
                    srvAcc.HostName = srv.Target.ToString();
                }

                break;
            case TXTRecord txt:
                if (Track(name, byInstance) is { } txtAcc && txt.Strings is not null)
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
                if (addressesByHost.Count < MaxAddresses || addressesByHost.ContainsKey(name))
                {
                    addressesByHost[name] = aRecord.Address.ToString();
                }

                break;
            case AAAARecord aaaaRecord:
                if (addressesByHost.Count < MaxAddresses)
                {
                    addressesByHost.AddOrUpdate(name, aaaaRecord.Address.ToString(),
                        (_, existing) => IsIpv4Literal(existing) ? existing : aaaaRecord.Address.ToString());
                }

                break;
            default:
                break;
        }
    }

    private static DeviceAccumulator? Track(string key,
        ConcurrentDictionary<string, DeviceAccumulator> byInstance) =>
        byInstance.TryGetValue(key, out var known)
            ? known
            : byInstance.Count >= MaxInstances
                ? null
                : byInstance.GetOrAdd(key, _ => new DeviceAccumulator());

    /// <summary>
    /// Reports whether an accumulated address text is an IPv4 literal. A dual-stack phone answers both A
    /// and AAAA in an order that varies per network, and the pairing flow dials the first address it gets,
    /// so an IPv4 answer must never be displaced by an IPv6 one: the adb server rejects a bare IPv6 host
    /// and IPv6-only links are the exception rather than the rule.
    /// <para>判断已累积的地址文本是否为 IPv4 字面量。双栈手机返回 A 与 AAAA 的顺序随网络而变，而配对
    /// 流程拨号使用它拿到的第一个地址，因此 IPv4 答案绝不能被 IPv6 覆盖：adb server 会拒绝裸 IPv6 主机，
    /// 且仅 IPv6 链路属于例外而非普遍情况。</para>
    /// </summary>
    /// <param name="address">The accumulated address text. <para>已累积的地址文本。</para></param>
    /// <returns><para>该文本为 IPv4 字面量时为 <see langword="true"/>。</para>
    /// <see langword="true"/> when the text is an IPv4 literal.</returns>
    private static bool IsIpv4Literal(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>
    /// Builds the final device list from the accumulated state, joining each instance
    /// with its address (matched via the SRV host or the instance name).
    /// <para>从累积状态构建最终设备列表，将每个实例与其地址关联
    /// （通过 SRV 主机或实例名匹配）。</para>
    /// </summary>
    /// <remarks>
    /// <para>An instance is reported only once both its SRV port and its address are known, and a
    /// missing port is never defaulted to 5555: wireless debugging listens on an ephemeral port, so
    /// a guessed port turns a discovery miss into a connection attempt against an unrelated
    /// service.</para>
    /// <para>只有在 SRV 端口和地址都已解析时才会上报实例，且缺失的端口绝不会被补成 5555：无线调试监听临时
    /// 端口，猜测的端口会把一次发现失败变成对无关服务的连接尝试。</para>
    /// </remarks>
    /// <param name="byInstance">Accumulated per-instance state. <para>已累积的实例状态。</para></param>
    /// <param name="addressesByHost">Accumulated address book. <para>已累积的地址表。</para></param>
    /// <param name="filter">Per-instance gate. <para>实例级筛选。</para></param>
    /// <returns>The resolved devices. <para>已解析的设备。</para></returns>
    internal static IReadOnlyList<MdnsDevice> BuildDevices(
        ConcurrentDictionary<string, DeviceAccumulator> byInstance,
        ConcurrentDictionary<string, string> addressesByHost,
        Func<DeviceAccumulator, bool> filter)
    {
        var devices = new List<MdnsDevice>();
        foreach (var pair in byInstance)
        {
            var acc = pair.Value;
            if (!filter(acc) || acc.Port is < 1 or > 65535)
            {
                continue;
            }

            var address = acc.HostName is not null && addressesByHost.TryGetValue(acc.HostName, out var byHost)
                ? byHost
                : addressesByHost.TryGetValue(pair.Key, out var byInstanceName)
                    ? byInstanceName
                    : null;
            if (address is null)
            {
                continue;
            }

            devices.Add(new MdnsDevice(pair.Key, acc.HostName, acc.Port, [address], acc.Properties));
        }

        return devices;
    }

    /// <summary>
    /// Per-instance state accumulated from mDNS records.
    /// <para>从 mDNS 记录累积的每个实例的状态。</para>
    /// </summary>
    internal sealed class DeviceAccumulator
    {
        /// <summary>Whether the instance is an adb TLS-connect or legacy service. 该实例是否为 adb TLS 连接或早期服务。</summary>
        public bool IsAdb { get; set; }

        /// <summary>Whether the instance is an adb TLS-pairing service. 该实例是否为 adb TLS 配对服务。</summary>
        public bool IsPairing { get; set; }

        /// <summary>The SRV port; zero until an SRV record resolves it. SRV 端口；解析到 SRV 记录前为 0。</summary>
        public int Port { get; set; }

        /// <summary>The SRV target host name. SRV 目标主机名。</summary>
        public string? HostName { get; set; }

        /// <summary>The TXT-record properties. TXT 记录属性。</summary>
        public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAdbService(string name)
        => name.EndsWith(AdbTlsService, StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(AdbLegacyService, StringComparison.OrdinalIgnoreCase);

    private static bool IsPairingService(string name)
        => name.EndsWith(AdbTlsPairingService, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compares the first label of a fully qualified instance name with an expected label, so a
    /// browsed <c>name._adb-tls-pairing._tcp.local</c>, the bare <c>S</c> field of the QR payload and
    /// one row of the adb server's mDNS table all match the same way.
    /// <para>把完整实例名的首段标签与期望标签比较，使浏览到的 <c>name._adb-tls-pairing._tcp.local</c>、
    /// 二维码的裸 <c>S</c> 字段以及 adb server mDNS 表中的一行都以同一方式匹配。</para>
    /// </summary>
    /// <param name="instance">The instance text, with or without the service suffix.
    /// <para>实例文本，可带或不带服务后缀。</para></param>
    /// <param name="expectedLabel">The expected first label. <para>期望的首段标签。</para></param>
    /// <returns>Whether the labels are equal. <para>标签是否相等。</para></returns>
    internal static bool MatchesInstanceLabel(string instance, string expectedLabel)
    {
        var label = instance;
        var separator = label.IndexOf('.');
        if (separator > 0)
        {
            label = label[..separator];
        }

        return string.Equals(label, expectedLabel, StringComparison.OrdinalIgnoreCase);
    }
}