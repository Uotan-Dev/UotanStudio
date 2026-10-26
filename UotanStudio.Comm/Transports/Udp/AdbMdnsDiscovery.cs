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

    /// <summary>
    /// Extra window kept open after the pairing instance resolves, so the connect instance advertised
    /// alongside it is collected in the same session.
    /// <para>配对实例解析后继续保留的额外窗口，使同时广播的连接实例在同一会话中被收集。</para>
    /// </summary>
    private static readonly TimeSpan PairingMatchGrace = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Extra window kept open after a secure connect instance resolves, so the records of the same burst
    /// are collected before the browse returns them.
    /// <para>安全连接实例解析后继续保留的额外窗口，使同一批广播中的记录在浏览返回前被收集。</para>
    /// </summary>
    private static readonly TimeSpan ConnectMatchGrace = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Service type of the Android 11+ secure connect service without the <c>.local</c> suffix, i.e. the
    /// text the adb server uses when it keys an auto-connected transport by DNS-SD instance name.
    /// <para>Android 11+ 安全连接服务的类型名（不含 <c>.local</c>），即 adb server 以 DNS-SD 实例名
    /// 作为自动连接传输键时使用的文本。</para>
    /// </summary>
    internal const string AdbTlsConnectTypeName = "_adb-tls-connect._tcp";

    private const int MaxInstances = 64;
    private const int MaxAddresses = 128;

    /// <summary>Upper bound of DNS questions sent in one unicast round.
    /// <para>单轮单播问询发送的 DNS 问题数上限。</para></summary>
    private const int MaxUnicastQueries = 24;

    /// <summary>Largest datagram accepted from a unicast mDNS answer.
    /// <para>可接受的单播 mDNS 应答数据报上限。</para></summary>
    private const int MaxDatagramBytes = 9000;

    /// <summary>Spacing between unicast query rounds while the device has not answered.
    /// <para>设备未应答时两轮单播问询之间的间隔。</para></summary>
    private static readonly TimeSpan UnicastQueryInterval = TimeSpan.FromMilliseconds(600);

    /// <summary>Per-receive wait before the unicast loop feeds the next query round.
    /// <para>单播循环在进入下一轮问询前对每次接收的等待时长。</para></summary>
    private static readonly TimeSpan UnicastReceivePoll = TimeSpan.FromMilliseconds(150);

    /// <summary>Unicast window used when a multicast browse produced no matching connect instance.
    /// <para>组播浏览未产生匹配的连接实例时使用的单播窗口。</para></summary>
    private static readonly TimeSpan UnicastFallbackWindow = TimeSpan.FromSeconds(1.5);

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
            static accumulator => accumulator.IsAdb, null, null, ct).ConfigureAwait(false);

    /// <summary>Polls the <c>_adb-tls-pairing._tcp</c> service until the instance with the expected name appears (the phone starts advertising after the QR code is scanned) and returns its pairing endpoint, or <c>null</c> when the deadline passes without a match.
    /// <para>轮询 <c>_adb-tls-pairing._tcp</c> 服务，直到出现期望名称的实例（手机 扫码后开始广播）并返回其配对端点；超过期限仍无匹配时返回 <c>null</c>。</para></summary>
    /// <param name="instanceName">The expected instance label, i.e. the QR "S" field.
    /// <para>期望的实例标签，即二维码的 "S" 字段。</para></param>
    /// <param name="timeout">Overall wait deadline.<para>整体等待期限。</para></param>
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

    /// <summary>Polls the <c>_adb-tls-connect._tcp</c> service for the supplied window and returns the most recently advertised instance that resolves to the given device IP address (the secure connect port), or <c>null</c> when none resolves.
    /// <para>在给定窗口内浏览 <c>_adb-tls-connect._tcp</c> 服务，返回解析到给定设备 IP 的最近广播实例（安全连接端口）；没有实例解析时为 <c>null</c>。</para></summary>
    /// <remarks>
    /// <para>The browse keeps its window open for a short grace after the first match and then returns the newest
    /// instance: after the daemon replaces its TLS service, a responder can still answer with the previous
    /// instance, and the newest one is the port that accepts the connection.</para>
    /// <para>首次命中后浏览会再保留一小段宽限窗口，然后返回最新的实例：守护进程替换 TLS 服务后，应答方仍可能返回
    /// 上一个实例，而最新实例才是接受连接的端口。</para>
    /// </remarks>
    /// <param name="address">The device IP address discovered during pairing.
    /// <para>配对阶段发现的设备 IP 地址。</para></param>
    /// <param name="timeout">Overall wait deadline.<para>整体等待期限。</para></param>
    /// <param name="ct">Cancellation token. 取消令牌。</param>
    /// <param name="instanceName">The instance label adbd registered, i.e. its <c>persist.adb.wifi.guid</c>; when
    /// given, that exact SRV name is queried and an instance carrying the label counts without an address match.
    /// <para>adbd 注册的实例标签，即其 <c>persist.adb.wifi.guid</c>；给出时会直接查询该 SRV 名称，携带该标签的实例
    /// 无需地址匹配也算命中。</para></param>
    /// <returns>The TLS connect service instance, or <c>null</c> on timeout.
    /// <para>TLS 连接服务实例；超时返回 <c>null</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="address"/> is blank.
    /// <para><paramref name="address"/> 为空。</para></exception>
    public static async Task<MdnsDevice?> WaitForConnectServiceAsync(
        string address, TimeSpan? timeout = null, CancellationToken ct = default, string? instanceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var devices = await BrowseAsync([AdbTlsService], timeout ?? DefaultTimeout,
            static _ => true,
            device => DescribesDevice(device, instanceName, address),
            AdbTlsService, ct, ConnectMatchGrace, true, instanceName).ConfigureAwait(false);
        var match = devices
            .Where(device => DescribesDevice(device, instanceName, address))
            .MaxBy(static device => device.LastSeenUtc);
        if (match is not null)
        {
            return match;
        }

        // Multicast can be filtered on some Wi-Fi links (power save, AP isolation); the device's own responder
        // still answers a unicast DNS-SD query.
        var direct = await QueryUnicastAsync(address, UnicastFallbackWindow, ct, instanceName)
            .ConfigureAwait(false);
        return direct
            .Where(device => device.Addresses.Count == 0 || DescribesDevice(device, instanceName, address))
            .MaxBy(static device => device.LastSeenUtc);
    }

    /// <summary>
    /// Browses the pairing and connect services in one session until the expected pairing instance
    /// resolves, then returns it together with every secure connect instance seen in the same window.
    /// <para>在一次会话中同时浏览配对与连接服务，直到期望的配对实例解析出来，并返回该实例与同一窗口内看到的全部
    /// 安全连接实例。</para>
    /// </summary>
    /// <remarks>
    /// <para>The connect advertisement is normally already on the link before pairing finishes, so
    /// collecting it here removes the post-pairing window in which the phone may have withdrawn it.</para>
    /// <para>连接广播通常在配对结束前就已在链路上，因此在此收集可以避开配对结束后的窗口期——那时手机可能已撤回
    /// 该广播。</para>
    /// </remarks>
    /// <param name="instanceName">The expected pairing instance label, i.e. the QR "S" field.
    /// <para>期望的配对实例标签，即二维码的 “S” 字段。</para></param>
    /// <param name="timeout">Overall wait deadline. <para>整体等待期限。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The pairing instance and the connect candidates seen alongside it.
    /// <para>配对实例及其同时看到的连接候选。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="instanceName"/> is blank.
    /// <para><paramref name="instanceName"/> 为空。</para></exception>
    internal static async Task<AdbPairingSnapshot> WaitForPairingSnapshotAsync(string instanceName,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        var devices = await BrowseAsync([AdbTlsPairingService, AdbTlsService], timeout ?? DefaultTimeout,
                static _ => true, device => MatchesInstanceLabel(device.Instance, instanceName),
                null, ct, PairingMatchGrace).ConfigureAwait(false);
        var pairing = devices.FirstOrDefault(device => IsType(device, AdbTlsPairingService) &&
                                                      MatchesInstanceLabel(device.Instance, instanceName));
        var connect = devices.Where(device => IsType(device, AdbTlsService)).ToArray();
        return new AdbPairingSnapshot(pairing, connect);
    }

    /// <summary>
    /// Browses <c>_adb-tls-connect._tcp</c> for the supplied window and returns every secure connect
    /// instance that resolved during it, ending the window early once <paramref name="earlyExit"/> matches.
    /// <para>在给定窗口内浏览 <c>_adb-tls-connect._tcp</c>，返回窗口内解析出的全部安全连接实例；一旦
    /// <paramref name="earlyExit"/> 命中就提前结束窗口。</para>
    /// </summary>
    /// <param name="timeout">Browse window; a non-positive value performs one query pass.
    /// <para>浏览窗口；非正值只执行一次查询。</para></param>
    /// <param name="earlyExit">Optional match that ends the browse early.
    /// <para>可选的提前结束匹配。</para></param>
    /// <param name="ct">Cancellation token; cancellation propagates.
    /// <para>取消令牌；取消会被传播。</para></param>
    /// <param name="requireAddress">When <see langword="false"/>, an instance whose A/AAAA answer never arrived is
    /// still returned with its SRV port and an empty address list, so the caller can dial that port on the device
    /// address it already knows.
    /// <para>为 <see langword="false"/> 时，A/AAAA 应答始终未到的实例仍会带着 SRV 端口和空地址列表返回，使调用方
    /// 能在已知设备地址上拨该端口。</para></param>
    /// <param name="instanceName">Optional instance label whose exact SRV record is queried alongside the browse,
    /// i.e. the adbd GUID. <para>可选实例标签：除浏览外还会查询其精确 SRV 记录，即 adbd GUID。</para></param>
    /// <returns>The resolved secure connect instances seen in the window.
    /// <para>窗口内解析出的安全连接实例。</para></returns>
    internal static async Task<IReadOnlyList<MdnsDevice>> CollectConnectServicesAsync(TimeSpan timeout,
        Func<MdnsDevice, bool>? earlyExit, CancellationToken ct, bool requireAddress = true,
        string? instanceName = null)
        => await BrowseAsync([AdbTlsService], timeout, static _ => true, earlyExit, AdbTlsService, ct,
            earlyExit is null ? null : ConnectMatchGrace, requireAddress, instanceName).ConfigureAwait(false);

    /// <summary>
    /// Asks the device's own mDNS responder over unicast when multicast is filtered (Wi-Fi power save, AP
    /// isolation or a saturated multicast group): sends DNS-SD questions for the secure connect service directly
    /// to <c>host:5353</c> from an ephemeral port and folds the answers into the same accumulator the multicast
    /// browse uses. An instance whose address never resolves is still returned with its SRV port, so the caller
    /// can dial it on the address it already knows.
    /// <para>组播被过滤时（Wi-Fi 省电、AP 隔离或组播组拥塞）直接以单播询问设备自身的 mDNS 应答方：从临时端口向
    /// <c>host:5353</c> 发送安全连接服务的 DNS-SD 问询，并把应答并入与组播浏览相同的累积器。地址始终未解析出的实例
    /// 仍会带着 SRV 端口返回，使调用方能在已知地址上拨号。</para>
    /// </summary>
    /// <param name="host">The device IPv4 address literal. <para>设备 IPv4 地址字面量。</para></param>
    /// <param name="timeout">Overall query window. <para>整体问询窗口。</para></param>
    /// <param name="ct">Cancels the query. <para>取消问询。</para></param>
    /// <param name="instanceName">The instance label adbd registered, queried as an exact SRV/TXT name so a
    /// responder that lost the PTR announcement still answers. <para>adbd 注册的实例标签，以精确的 SRV/TXT 名称问询，
    /// 使丢失 PTR 广播的应答方仍能作答。</para></param>
    /// <returns>The secure connect instances the device itself reported, possibly without an address.
    /// <para>设备自身应答的安全连接实例，可能不含地址。</para></returns>
    internal static async Task<IReadOnlyList<MdnsDevice>> QueryUnicastAsync(string host, TimeSpan timeout,
        CancellationToken ct = default, string? instanceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            timeout <= TimeSpan.Zero)
        {
            return [];
        }

        var byInstance = new ConcurrentDictionary<string, DeviceAccumulator>(StringComparer.OrdinalIgnoreCase);
        var addressesByHost = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = lifetime.Token;
        var deadline = DateTime.UtcNow + timeout;
        var nextQuery = DateTime.UtcNow;
        Task<UdpReceiveResult>? pending = null;
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            var remote = new IPEndPoint(address, MdnsPort);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= nextQuery)
                {
                    foreach (var query in BuildUnicastQueries(byInstance, instanceName))
                    {
                        await client.SendAsync(query, remote, token).ConfigureAwait(false);
                    }

                    nextQuery = DateTime.UtcNow + UnicastQueryInterval;
                }

                pending ??= client.ReceiveAsync(token).AsTask();
                if (await Task.WhenAny(pending, Task.Delay(UnicastReceivePoll, token)).ConfigureAwait(false)
                    != pending)
                {
                    continue;
                }

                var datagram = await pending.ConfigureAwait(false);
                pending = null;
                AccumulateDatagram(datagram.Buffer, byInstance, addressesByHost);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
            return [];
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (pending is not null)
            {
                try { await pending.ConfigureAwait(false); }
                catch (Exception)
                {
                }
            }
        }

        return BuildDevices(byInstance, addressesByHost, static _ => true, AdbTlsService, requireAddress: false);
    }

    private static IReadOnlyList<byte[]> BuildUnicastQueries(
        ConcurrentDictionary<string, DeviceAccumulator> byInstance, string? instanceName)
    {
        var queries = new List<byte[]> { SerializeQuery(AdbTlsService, DnsType.PTR) };
        if (instanceName is not null)
        {
            // The daemon registers its TLS service under a known instance name, so asking for that exact SRV
            // record reaches the port even when the responder's PTR announcements were missed.
            var instance = $"{instanceName}.{AdbTlsService}";
            queries.Add(SerializeQuery(instance, DnsType.SRV));
            queries.Add(SerializeQuery(instance, DnsType.TXT));
        }

        foreach (var (instance, accumulator) in byInstance)
        {
            if (queries.Count >= MaxUnicastQueries)
            {
                break;
            }

            if (accumulator.Port == 0)
            {
                queries.Add(SerializeQuery(instance, DnsType.SRV));
            }

            if (accumulator.HostName is { } hostName)
            {
                queries.Add(SerializeQuery(hostName, DnsType.A));
                queries.Add(SerializeQuery(hostName, DnsType.AAAA));
            }

            queries.Add(SerializeQuery(instance, DnsType.A));
            queries.Add(SerializeQuery(instance, DnsType.AAAA));
        }

        return queries;
    }

    private static byte[] SerializeQuery(string name, DnsType type)
    {
        var message = new Message { Id = 0, QR = false };
        message.Questions.Add(new Question
        {
            Name = new DomainName(name),
            Type = type,
            Class = DnsClass.IN,
        });
        using var stream = new MemoryStream();
        message.Write(new WireWriter(stream));
        return stream.ToArray();
    }

    private static void AccumulateDatagram(byte[] datagram,
        ConcurrentDictionary<string, DeviceAccumulator> byInstance,
        ConcurrentDictionary<string, string> addressesByHost)
    {
        if (datagram.Length is 0 or > MaxDatagramBytes)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(datagram, writable: false);
            var message = new Message();
            message.Read(new WireReader(stream));
            Accumulate(message, byInstance, addressesByHost);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException
            or IndexOutOfRangeException)
        {
        }
    }

    /// <summary>
    /// Compares the service type a device was advertised under with a browse type name, ignoring a
    /// trailing dot and the <c>.local</c> domain suffix.
    /// <para>把设备广播的服务类型与浏览类型名比较，忽略结尾点号与 <c>.local</c> 域后缀。</para>
    /// </summary>
    /// <param name="device">The discovered device. <para>已发现的设备。</para></param>
    /// <param name="serviceType">The browse type name. <para>浏览用的类型名。</para></param>
    /// <returns>Whether the device was advertised under that service type.
    /// <para>设备以该服务类型广播时为 <see langword="true"/>。</para></returns>
    internal static bool IsType(MdnsDevice device, string serviceType) =>
        device.ServiceType is not null &&
        string.Equals(StripLocalSuffix(device.ServiceType), StripLocalSuffix(serviceType),
            StringComparison.OrdinalIgnoreCase);

    private static string StripLocalSuffix(string serviceType)
    {
        var trimmed = NormalizeServiceType(serviceType);
        return trimmed.EndsWith(LocalSuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^LocalSuffix.Length]
            : trimmed;
    }

    internal static async Task<MdnsDevice?> WaitForServiceAsync(
        string serviceType, Func<MdnsDevice, bool> predicate, TimeSpan? timeout, CancellationToken ct)
    {
        var devices = await BrowseAsync([serviceType], timeout ?? DefaultTimeout,
            static _ => true, predicate, serviceType, ct).ConfigureAwait(false);
        return devices.Where(predicate).MaxBy(static device => device.LastSeenUtc);
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
    /// <param name="requiredServiceType">When set, only instances advertised under this service type are built;
    /// <c>null</c> keeps every accumulated type. <para>设置时只构建该服务类型广播的实例；<c>null</c> 保留所有已累积类型。</para></param>
    /// <param name="ct">Cancellation token; cancellation propagates. <para>取消令牌；取消会被传播。</para></param>
    /// <param name="matchGrace">Extra window kept open after <paramref name="earlyExit"/> first matches, so
    /// records announced in the same burst are still collected. <para>提前匹配后继续保留的额外窗口，使同一批广播中的
    /// 记录仍被收集。</para></param>
    /// <param name="requireAddress">Whether an instance without a resolved A/AAAA answer is dropped.
    /// <para>是否丢弃未解析出 A/AAAA 应答的实例。</para></param>
    /// <param name="expectedInstance">Optional instance label whose exact SRV/TXT records are queried in every
    /// round, so the daemon's own registration is reached without waiting for a PTR announcement.
    /// <para>可选实例标签：每轮都查询其精确 SRV/TXT 记录，使守护进程自身的注册不必等待 PTR 广播即可被问到。</para></param>
    /// <returns>The devices known at exit. <para>退出时已知的设备。</para></returns>
    private static async Task<IReadOnlyList<MdnsDevice>> BrowseAsync(
        IReadOnlyList<string> serviceTypes, TimeSpan timeout, Func<DeviceAccumulator, bool> filter,
        Func<MdnsDevice, bool>? earlyExit, string? requiredServiceType, CancellationToken ct,
        TimeSpan? matchGrace = null, bool requireAddress = true, string? expectedInstance = null)
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

            if (expectedInstance is not null)
            {
                // Ask for the exact instance the daemon registered: a responder answers SRV and TXT for a name it
                // owns even when a listener missed the PTR announcement that introduced it.
                var name = new DomainName($"{expectedInstance}.{AdbTlsService}");
                mdns.SendQuery(name, DnsClass.IN, DnsType.SRV);
                mdns.SendQuery(name, DnsClass.IN, DnsType.TXT);
            }
        }

        mdns.AnswerReceived += (_, e) => Accumulate(e.Message, byInstance, addressesByHost);
        mdns.NetworkInterfaceDiscovered += (_, _) => QueryInstances();
        mdns.Start();
        QueryInstances();

        var deadline = DateTime.UtcNow + timeout;
        var nextQuery = DateTime.UtcNow;
        DateTime? matchedAt = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var devices = BuildDevices(byInstance, addressesByHost, filter, requiredServiceType, requireAddress);
            if (earlyExit is not null && devices.Any(earlyExit))
            {
                matchedAt ??= DateTime.UtcNow;
                if (matchGrace is null || DateTime.UtcNow - matchedAt >= matchGrace.Value)
                {
                    return devices;
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
        var serviceType = NormalizeServiceType(name);
        switch (record)
        {
            case PTRRecord ptr when IsPairingService(serviceType):
                if (Track(ptr.DomainName.ToString(), byInstance) is { } pairing)
                {
                    pairing.IsPairing = true;
                    pairing.AddServiceType(AdbTlsPairingService);
                }

                break;
            case PTRRecord ptr when IsAdbService(serviceType):
                if (Track(ptr.DomainName.ToString(), byInstance) is { } adb)
                {
                    adb.IsAdb = true;
                    adb.AddServiceType(serviceType.EndsWith(NormalizeServiceType(AdbTlsService),
                        StringComparison.OrdinalIgnoreCase)
                        ? AdbTlsService
                        : AdbLegacyService);
                }

                break;
            case SRVRecord srv:
                if (Track(name, byInstance) is { } srvAcc)
                {
                    srvAcc.Port = srv.Port;
                    srvAcc.HostName = srv.Target.ToString();
                    srvAcc.LastSeenUtc = DateTime.UtcNow;
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
    /// <para>An instance is reported only once its SRV port is known; a missing port is never defaulted to 5555,
    /// because wireless debugging listens on an ephemeral port and a guessed port turns a discovery miss into a
    /// connection attempt against an unrelated service. An instance whose A/AAAA answer never arrived is dropped
    /// unless <paramref name="requireAddress"/> is false.</para>
    /// <para>只有解析出 SRV 端口后才会上报实例，且缺失的端口绝不会被补成 5555：无线调试监听临时端口，
    /// 猜测端口会把一次发现失败变成对无关服务的连接尝试。A/AAAA 应答始终未到的实例会被丢弃，除非
    /// <paramref name="requireAddress"/> 为 false。</para>
    /// </remarks>
    /// <param name="byInstance">Accumulated per-instance state. <para>已累积的实例状态。</para></param>
    /// <param name="addressesByHost">Accumulated address book. <para>已累积的地址表。</para></param>
    /// <param name="filter">Per-instance gate. <para>实例级筛选。</para></param>
    /// <param name="requiredServiceType">When set, only instances whose PTR record named this service type are
    /// built; <c>null</c> keeps every accumulated type. <para>设置时只构建 PTR 记录名为该服务类型的实例；
    /// <c>null</c> 保留所有已累积类型。</para></param>
    /// <param name="requireAddress">Whether an instance without a resolved address is dropped.
    /// <para>是否丢弃未解析出地址的实例。</para></param>
    /// <returns>The resolved devices. <para>已解析的设备。</para></returns>
    internal static IReadOnlyList<MdnsDevice> BuildDevices(
        ConcurrentDictionary<string, DeviceAccumulator> byInstance,
        ConcurrentDictionary<string, string> addressesByHost,
        Func<DeviceAccumulator, bool> filter,
        string? requiredServiceType = null,
        bool requireAddress = true)
    {
        var devices = new List<MdnsDevice>();
        foreach (var pair in byInstance)
        {
            var acc = pair.Value;
            if (!filter(acc) || acc.Port is < 1 or > 65535 ||
                (requiredServiceType is not null && !acc.HasServiceType(requiredServiceType)))
            {
                continue;
            }

            var address = acc.HostName is not null && addressesByHost.TryGetValue(acc.HostName, out var byHost)
                ? byHost
                : addressesByHost.TryGetValue(pair.Key, out var byInstanceName)
                    ? byInstanceName
                    : null;
            if (address is null && requireAddress)
            {
                continue;
            }

            devices.Add(new MdnsDevice(pair.Key, acc.HostName, acc.Port,
                address is null ? [] : [address], acc.Properties, ResolveServiceType(acc), acc.LastSeenUtc));
        }

        return devices;
    }

    /// <summary>
    /// Per-instance state accumulated from mDNS records.
    /// <para>从 mDNS 记录累积的每个实例的状态。</para>
    /// </summary>
    private static string? ResolveServiceType(DeviceAccumulator accumulator) =>
        accumulator.HasServiceType(AdbTlsService) ? AdbTlsService
        : accumulator.HasServiceType(AdbTlsPairingService) ? AdbTlsPairingService
        : accumulator.HasServiceType(AdbLegacyService) ? AdbLegacyService
        : null;

    internal sealed class DeviceAccumulator
    {
        /// <summary>
        /// Whether the instance is an adb TLS-connect or legacy service.
        /// <para>该实例是否为 adb TLS 连接或早期服务。</para>
        /// </summary>
        public bool IsAdb { get; set; }

        /// <summary>
        /// Whether the instance is an adb TLS-pairing service.
        /// <para>该实例是否为 adb TLS 配对服务。</para>
        /// </summary>
        public bool IsPairing { get; set; }

        private readonly ConcurrentDictionary<string, byte> _serviceTypes =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reports whether a PTR record advertised this instance under the supplied service type.
        /// <para>报告是否有 PTR 记录以给定服务类型广播了此实例。</para></summary>
        /// <param name="serviceType">Service type name, with or without a trailing dot.
        /// <para>服务类型名，可带或不带尾点。</para></param>
        /// <returns><see langword="true"/> when this instance belongs to that type.
        /// <para>该实例属于该类型时为 <see langword="true"/>。</para></returns>
        internal bool HasServiceType(string serviceType) =>
            _serviceTypes.ContainsKey(AdbMdnsDiscovery.NormalizeServiceType(serviceType));

        /// <summary>Records the service type of a PTR record that referenced this instance.
        /// <para>记录引用此实例的 PTR 记录的服务类型。</para></summary>
        /// <param name="serviceType">The PTR record name. <para>PTR 记录名。</para></param>
        internal void AddServiceType(string serviceType) =>
            _serviceTypes.TryAdd(AdbMdnsDiscovery.NormalizeServiceType(serviceType), 0);

        /// <summary>The SRV port; zero until an SRV record resolves it. SRV 端口；解析到 SRV 记录前为 0。</summary>
        public int Port { get; set; }

        /// <summary>When the SRV record was last received; <see cref="DateTime.MinValue"/> before the first
        /// one. A phone that restarts wireless debugging re-advertises on a new port, so the newest record is
        /// the one that answers.
        /// <para>SRV 记录最后被收到的时刻；首条记录之前为 <see cref="DateTime.MinValue"/>。重启无线调试的手机会以
        /// 新端口重新广播，因此最新记录才对应正在应答的服务。</para></summary>
        public DateTime LastSeenUtc { get; set; }

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

    private const string LocalSuffix = ".local";

    private static string NormalizeServiceType(string serviceType) => serviceType.TrimEnd('.');

    /// <summary>
    /// Compares the first label of a fully qualified instance name with an expected label, so a
    /// browsed <c>name._adb-tls-pairing._tcp.local</c>, the bare <c>S</c> field of the QR payload and
    /// one row of the adb server's mDNS table all match the same way.
    /// <para>把完整实例名的首段标签与期望标签比较，使浏览到的 <c>name._adb-tls-pairing._tcp.local</c>、
    /// 二维码的裸 <c>S</c> 字段以及 adb server mDNS 表中的一行都以同一方式匹配。</para>
    /// </summary>
    /// <remarks>An expected label containing a dot is rejected because this implementation does not decode
    /// DNS-SD escaped dots; such a label never matches.
    /// <para>包含点号的期望标签会被拒绝，因为本实现不解码 DNS-SD 转义点号；这种标签永远不会匹配。</para></remarks>
    /// <param name="instance">The instance text, with or without the service suffix.
    /// <para>实例文本，可带或不带服务后缀。</para></param>
    /// <param name="expectedLabel">The expected first label. <para>期望的首段标签。</para></param>
    /// <returns>Whether the labels are equal. <para>标签是否相等。</para></returns>
    internal static bool MatchesInstanceLabel(string instance, string expectedLabel)
    {
        if (string.IsNullOrEmpty(expectedLabel) || expectedLabel.Contains('.'))
        {
            return false;
        }

        var separator = instance.IndexOf('.');
        var label = separator > 0 ? instance[..separator] : instance;
        return string.Equals(label, expectedLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares two host strings as network addresses, treating an IPv4 literal and its IPv4-mapped IPv6
    /// form as the same address.
    /// <para>把两个主机字符串按网络地址比较，IPv4 字面量与其 IPv4 映射的 IPv6 形式视为同一地址。</para>
    /// </summary>
    /// <param name="candidate">First host or address text. <para>第一个主机或地址文本。</para></param>
    /// <param name="address">Second host or address text. <para>第二个主机或地址文本。</para></param>
    /// <returns><see langword="true"/> when both texts name the same host or address.
    /// <para>两个文本指向同一主机或地址时为 <see langword="true"/>。</para></returns>
    internal static bool SameAddress(string candidate, string address)
    {
        if (string.Equals(candidate, address, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(candidate, out var candidateAddress) ||
            !IPAddress.TryParse(address, out var addressValue))
        {
            return false;
        }

        return Normalize(candidateAddress).Equals(Normalize(addressValue));

        static IPAddress Normalize(IPAddress value) =>
            value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;
    }

    /// <summary>
    /// Reports whether a connect instance describes the device identified by the instance name adbd registered (its
    /// GUID) or by the paired address, so a responder that answers under either identity is recognized.
    /// <para>判断连接实例是否描述了 adbd 注册的实例名（其 GUID）或配对地址所标识的设备，使以任一身份应答的应答方都能
    /// 被识别。</para>
    /// </summary>
    /// <param name="device">The connect instance. <para>连接实例。</para></param>
    /// <param name="instanceName">The registered instance label, when known.
    /// <para>已知时已注册的实例标签。</para></param>
    /// <param name="address">The paired device address. <para>已配对设备的地址。</para></param>
    /// <returns>Whether the instance describes that device. <para>该实例是否描述该设备。</para></returns>
    internal static bool DescribesDevice(MdnsDevice device, string? instanceName, string address) =>
        (instanceName is not null && MatchesInstanceLabel(device.Instance, instanceName)) ||
        device.Addresses.Any(candidate => SameAddress(candidate, address));
}

/// <summary>
/// One browse session's pairing result: the resolved pairing instance plus the secure connect
/// instances collected before it resolved.
/// <para>一次浏览会话的配对结果：解析出的配对实例，以及在解析前后收集到的安全连接实例。</para>
/// </summary>
/// <param name="Pairing">The pairing instance, or <c>null</c> when it never appeared.
/// <para>配对实例；始终未出现时为 <c>null</c>。</para></param>
/// <param name="ConnectCandidates">Every secure connect instance seen in the same window.
/// <para>同一窗口内看到的全部安全连接实例。</para></param>
internal sealed record AdbPairingSnapshot(MdnsDevice? Pairing, IReadOnlyList<MdnsDevice> ConnectCandidates);