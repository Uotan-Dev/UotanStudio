using System.Globalization;
using System.Net.Sockets;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Udp;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// One row of the mDNS table a running adb server maintains for its own browser
/// (<c>host:mdns:services</c>). The server resolves PTR, SRV and address records itself, so the
/// host and port in a row are already dial-ready, which is what <c>adb pair</c> and
/// <c>adb connect</c> consume on the command line.
/// <para>正在运行的 adb server 为其内置 mDNS 浏览器维护的表中的一行（<c>host:mdns:services</c>）。
/// server 自己完成 PTR、SRV 与地址记录的解析，因此行内的主机和端口已可直接拨号，也就是命令行
/// <c>adb pair</c> 与 <c>adb connect</c> 所使用的值。</para>
/// </summary>
/// <param name="Instance">The DNS-SD instance label, e.g. the QR <c>S</c> field advertised by the
/// phone. <para>DNS-SD 实例标签，例如手机广播的二维码 <c>S</c> 字段。</para></param>
/// <param name="ServiceType">The service type without a trailing dot, e.g.
/// <c>_adb-tls-pairing._tcp</c>. <para>不带尾点的服务类型，如 <c>_adb-tls-pairing._tcp</c>。</para></param>
/// <param name="Host">The resolved device IP address. <para>已解析的设备 IP 地址。</para></param>
/// <param name="Port">The resolved service TCP port. <para>已解析的服务 TCP 端口。</para></param>
internal sealed record AdbServerMdnsEntry(string Instance, string ServiceType, string Host, int Port);

/// <summary>
/// Reports how the server answered an mDNS table request.
/// <para>报告 server 对 mDNS 表请求的应答情况。</para>
/// </summary>
internal enum AdbServerMdnsStatus
{
    /// <summary>The server answered and returned its current rows.
    /// <para>server 已应答并返回当前表内容。</para></summary>
    Answered,

    /// <summary>The server rejected the command, i.e. this build has no mDNS browser; the caller
    /// must use another discovery source. <para>server 拒绝了该命令，即该构建没有 mDNS 浏览器；调用方
    /// 必须改用其他发现来源。</para></summary>
    NotSupported,

    /// <summary>The request failed transiently (stalled, refused or reset socket); the caller should
    /// keep polling within its own deadline. <para>请求暂时性失败（停滞、被拒绝或套接字复位）；调用方
    /// 应在自身期限内继续轮询。</para></summary>
    TransientFailure,
}

/// <summary>
/// Reads the adb server's own mDNS table over its control socket.
/// <para>通过 adb server 的控制套接字读取其自身的 mDNS 表。</para>
/// </summary>
/// <remarks>
/// <para>The server browser is the authoritative source for a wireless-debugging endpoint while a
/// server listens: it holds one long-lived multicast socket, records the pairing and connect
/// services the phone announces, and refreshes its cache without the caller re-binding UDP 5353.
/// A tool that browses mDNS itself competes for that port and must redo the whole PTR, SRV and
/// address resolution on every poll, so both sources are consulted at once and the first one to see
/// the advertisement is used.</para>
/// <para>只要有 server 在监听，server 内置浏览器就是无线调试端点的权威来源：它持有一条长生命周期的组播
/// 套接字，会记录手机广播的配对与连接服务，并在不要求调用方重新绑定 UDP 5353 的情况下刷新缓存。自行
/// 浏览 mDNS 的实现必须与该端口竞争，且每次轮询都要重做完整的 PTR、SRV 与地址解析，因此两个来源同时
/// 查询，先看到广播的那个被采用。</para>
/// </remarks>
internal static class AdbServerMdns
{
    private const string ServicesCommand = "host:mdns:services";
    private const string UnsupportedMarker = "unknown host service";
    private const string LocalSuffix = ".local";
    private const int MaxRows = 256;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly IReadOnlyDictionary<string, string> EmptyProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Polls the server table until an entry satisfies <paramref name="match"/> or the deadline
    /// passes. The lookup reports whether the server exposes the table at all, so a caller can tell
    /// "nobody advertised" apart from "this server cannot answer"; the pairing flow runs this poll
    /// alongside the local multicast browser and uses whichever source resolves first.
    /// <para>轮询 server 的表，直到某个条目满足 <paramref name="match"/> 或期限到期。结果同时说明 server
    /// 是否提供该表，调用方因此能区分「无人广播」与「该 server 无法应答」；配对流程会把这次轮询与本地组播
    /// 浏览器并行运行，采用先解析成功的一方。</para>
    /// </summary>
    /// <param name="match">Tested against every row of every poll. <para>对每次轮询的每一行执行。</para></param>
    /// <param name="timeout">Overall deadline for the poll loop. <para>轮询循环的整体期限。</para></param>
    /// <param name="cancellationToken">Cancels the loop and each query. <para>取消循环与每次查询。</para></param>
    /// <returns>Whether the server supports the table, plus the matching entry if one
    /// appeared. <para>server 是否支持该表，以及出现的匹配条目。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="match"/> is <c>null</c>.
    /// <para><paramref name="match"/> 为 <c>null</c>。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    internal static async Task<AdbServerMdnsLookup> WaitForAsync(Func<AdbServerMdnsEntry, bool> match,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(match);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var poll = await QueryAsync(DeviceCommunicationConfiguration.ServerHost,
                DeviceCommunicationConfiguration.AdbServerPort, cancellationToken).ConfigureAwait(false);
            if (poll.Status == AdbServerMdnsStatus.NotSupported)
            {
                return new AdbServerMdnsLookup(false, null);
            }

            if (poll.Status == AdbServerMdnsStatus.Answered)
            {
                var entry = poll.Entries.FirstOrDefault(match);
                if (entry is not null)
                {
                    return new AdbServerMdnsLookup(true, entry);
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return new AdbServerMdnsLookup(true, null);
            }

            await Task.Delay(remaining > PollInterval ? PollInterval : remaining, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one <c>host:mdns:services</c> request and parses the reply.
    /// <para>执行一次 <c>host:mdns:services</c> 请求并解析应答。</para>
    /// </summary>
    /// <param name="serverHost">adb server host to ask. <para>要询问的 adb server 主机。</para></param>
    /// <param name="serverPort">adb server port to ask. <para>要询问的 adb server 端口。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消协议交互。</para></param>
    /// <returns>The answer status plus the current rows. <para>应答状态及当前表内容。</para></returns>
    internal static async Task<AdbServerMdnsQuery> QueryAsync(string serverHost, int serverPort,
        CancellationToken cancellationToken)
    {
        string payload;
        try
        {
            payload = await AdbServerServices.RunHostCommandAsync(ServicesCommand,
                serverHost, serverPort,
                QueryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.Message.Contains(UnsupportedMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new AdbServerMdnsQuery(AdbServerMdnsStatus.NotSupported, []);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AdbServerMdnsQuery(AdbServerMdnsStatus.TransientFailure, []);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            return new AdbServerMdnsQuery(AdbServerMdnsStatus.TransientFailure, []);
        }

        return new AdbServerMdnsQuery(AdbServerMdnsStatus.Answered, Parse(payload));
    }

    /// <summary>
    /// Parses the server's tab-separated <c>instance</c> / <c>service type</c> / <c>host:port</c>
    /// rows, dropping malformed input and collapsing the duplicate rows the server reports per bound
    /// interface.
    /// <para>解析 server 以制表符分隔的 <c>实例</c> / <c>服务类型</c> / <c>host:port</c> 行，丢弃格式错误的
    /// 输入，并合并 server 按每个已绑定网卡重复报告的行。</para>
    /// </summary>
    /// <param name="payload">The raw server payload. <para>server 原始应答文本。</para></param>
    /// <returns>The parsed rows in report order. <para>按应答顺序解析出的行。</para></returns>
    internal static IReadOnlyList<AdbServerMdnsEntry> Parse(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return [];
        }

        var entries = new List<AdbServerMdnsEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in payload.Split('\n'))
        {
            if (entries.Count >= MaxRows)
            {
                break;
            }

            var fields = line.Split('\t', StringSplitOptions.TrimEntries);
            if (fields.Length != 3 || fields[0].Length == 0 ||
                !DebugBridgeNetworkAddress.TrySplitEndpoint(fields[2], out var host, out var port))
            {
                continue;
            }

            var serviceType = fields[1].TrimEnd('.');
            if (!seen.Add(string.Concat(fields[0], "\t", serviceType, "\t", host, ":",
                    port.ToString(CultureInfo.InvariantCulture))))
            {
                continue;
            }

            entries.Add(new AdbServerMdnsEntry(fields[0], serviceType, host, port));
        }

        return entries;
    }

    /// <summary>
    /// Reports whether a row advertises the service type a caller browsed for, comparing the
    /// server's <c>_adb-tls-connect._tcp</c> spelling with the local browse constant's
    /// <c>_adb-tls-connect._tcp.local</c>.
    /// <para>判断某一行是否广播了调用方所浏览的服务类型，即把 server 的
    /// <c>_adb-tls-connect._tcp</c> 写法与本地浏览常量的 <c>_adb-tls-connect._tcp.local</c> 作比较。</para>
    /// </summary>
    /// <param name="entry">The row to test. <para>待测试的行。</para></param>
    /// <param name="browseType">The locally browsed type name. <para>本地浏览的类型名。</para></param>
    /// <returns>Whether the row carries that type. <para>该行是否属于该类型。</para></returns>
    internal static bool IsType(AdbServerMdnsEntry entry, string browseType) =>
        string.Equals(entry.ServiceType, StripLocalSuffix(browseType), StringComparison.OrdinalIgnoreCase);

    private static string StripLocalSuffix(string browseType) =>
        browseType.EndsWith(LocalSuffix, StringComparison.OrdinalIgnoreCase)
            ? browseType[..^LocalSuffix.Length]
            : browseType;

    /// <summary>
    /// Wraps one matching entry as the browse result shape the pairing flow already consumes.
    /// <para>把匹配到的条目包装成配对流程已在使用的浏览结果形状。</para>
    /// </summary>
    /// <param name="entry">The server row. <para>server 行。</para></param>
    /// <returns>An equivalent locally browsed device. <para>等价的本地浏览设备。</para></returns>
    internal static MdnsDevice ToDevice(AdbServerMdnsEntry entry) =>
        new(entry.Instance, null, entry.Port, [entry.Host], EmptyProperties);
}

/// <summary>
/// Outcome of one <c>host:mdns:services</c> request.
/// <para>一次 <c>host:mdns:services</c> 请求的结果。</para>
/// </summary>
/// <param name="Status">How the server answered. <para>server 的应答情况。</para></param>
/// <param name="Entries">The current rows. <para>当前的表内容。</para></param>
internal readonly record struct AdbServerMdnsQuery(AdbServerMdnsStatus Status,
    IReadOnlyList<AdbServerMdnsEntry> Entries);

/// <summary>
/// Outcome of a poll loop over the server's mDNS table.
/// <para>对 server 的 mDNS 表进行轮询循环的结果。</para>
/// </summary>
/// <param name="Supported">Whether the server answered the command during the loop;
/// <c>false</c> means the caller must use another discovery source.
/// <para>循环期间 server 是否应答了该命令；为 <c>false</c> 表示调用方必须改用其他发现来源。</para></param>
/// <param name="Entry">The matching row, or <c>null</c> when the deadline passed without
/// one. <para>匹配的行；期限到期仍无匹配时为 <c>null</c>。</para></param>
internal readonly record struct AdbServerMdnsLookup(bool Supported, AdbServerMdnsEntry? Entry);