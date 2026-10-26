using System.Globalization;
using System.Net.Sockets;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Udp;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// One row of the mDNS table a running adb server maintains under
/// <c>host:mdns:services</c>, holding a dial-ready device address and TCP port.
/// <para>正在运行的 adb server 在 <c>host:mdns:services</c> 下维护的 mDNS 表中的一行，
/// 包含可直接拨号的设备地址与 TCP 端口。</para>
/// </summary>
/// <param name="Instance">The DNS-SD instance label advertised by the phone.
/// <para>手机广播的 DNS-SD 实例标签。</para></param>
/// <param name="ServiceType">The service type without a trailing dot.
/// <para>不带尾点的服务类型。</para></param>
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

    /// <summary>The server rejected the command because this build exposes no mDNS table.
    /// <para>server 拒绝了该命令，因为该构建不提供 mDNS 表。</para></summary>
    NotSupported,

    /// <summary>The request failed transiently (stalled, refused or reset socket); a later poll may succeed.
    /// <para>请求暂时性失败（停滞、被拒绝或套接字复位）；后续轮询可能成功。</para></summary>
    TransientFailure,
}

/// <summary>
/// Reads the adb server's own mDNS table over its control socket.
/// <para>通过 adb server 的控制套接字读取其自身的 mDNS 表。</para>
/// </summary>
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
    /// passes; the result also reports whether the server exposes the table at all.
    /// <para>轮询 server 的表，直到某个条目满足 <paramref name="match"/> 或期限到期；结果同时说明 server 是否提供该表。</para>
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
    /// Builds the lookup that maps a DNS-SD connect instance, in bare or full <c>instance.serviceType</c>
    /// form, to the dialable <c>host:port</c> the server reported for it. This is the identity an
    /// auto-connected transport carries as its serial.
    /// <para>构建查找表，把 DNS-SD 连接实例（裸名或完整的 <c>实例.服务类型</c> 形式）映射到 server 为该实例
    /// 报告的可拨号 <c>host:port</c>。自动连接产生的传输正是以该身份作为串号。</para>
    /// </summary>
    /// <param name="entries">Rows reported by <c>host:mdns:services</c>.
    /// <para><c>host:mdns:services</c> 报告的行。</para></param>
    /// <param name="connectServiceTypes">Service types whose rows describe a secure connect service, with or
    /// without a trailing dot. <para>其行描述安全连接服务的服务类型，可带或不带尾点。</para></param>
    /// <returns>An instance-or-serial keyed endpoint map, empty when no row describes a connect service.
    /// <para>按实例或串号键控的端点映射；没有行描述连接服务时为空。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is <c>null</c>.
    /// <para>某个参数为 <c>null</c>。</para></exception>
    internal static IReadOnlyDictionary<string, string> BuildConnectAliases(
        IEnumerable<AdbServerMdnsEntry> entries, IReadOnlyList<string> connectServiceTypes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(connectServiceTypes);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var serviceType = NormalizeAliasText(entry.ServiceType);
            if (!connectServiceTypes.Any(candidate => string.Equals(
                    serviceType, NormalizeAliasText(candidate), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var instance = NormalizeAliasText(entry.Instance);
            var suffix = "." + serviceType;
            if (instance.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                instance = instance[..^suffix.Length];
            }

            var endpoint = DebugBridgeNetworkAddress.FormatEndpoint(entry.Host, entry.Port);
            aliases[instance] = endpoint;
            aliases[string.Concat(instance, ".", serviceType)] = endpoint;
        }

        return aliases;
    }

    /// <summary>
    /// Resolves a server target serial against a connect alias map. The exact serial is tried first, then a
    /// serial ending in <c>.serviceType</c> is retried without that suffix for tables that only carry the
    /// bare instance label.
    /// <para>用连接别名表解析 server 目标串号：先尝试完整串号，再对以 <c>.服务类型</c> 结尾的串号剥离该后缀
    /// 重试，以兼容只携带裸实例标签的表。</para>
    /// </summary>
    /// <param name="aliases">The alias map. <para>别名表。</para></param>
    /// <param name="serial">The target serial reported by <c>host:devices-l</c>.
    /// <para><c>host:devices-l</c> 报告的目标串号。</para></param>
    /// <param name="connectServiceTypes">Service types the map was built from.
    /// <para>构建别名表时使用的服务类型。</para></param>
    /// <returns>The resolved <c>host:port</c>, or <c>null</c> when no row matches.
    /// <para>解析出的 <c>host:port</c>；没有匹配行时为 <c>null</c>。</para></returns>
    internal static string? ResolveConnectAlias(IReadOnlyDictionary<string, string> aliases, string serial,
        IReadOnlyList<string> connectServiceTypes)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(connectServiceTypes);
        if (string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }

        var normalized = NormalizeAliasText(serial);
        if (aliases.TryGetValue(normalized, out var exact))
        {
            return exact;
        }

        foreach (var serviceType in connectServiceTypes)
        {
            var suffix = "." + NormalizeAliasText(serviceType);
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                aliases.TryGetValue(normalized[..^suffix.Length], out var stripped))
            {
                return stripped;
            }
        }

        return null;
    }

    private static string NormalizeAliasText(string value)
    {
        var trimmed = value.TrimEnd('.');
        return trimmed.EndsWith(LocalSuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^LocalSuffix.Length]
            : trimmed;
    }

    /// <summary>
    /// Reports whether a row advertises a browsed service type, comparing names after the trailing
    /// <c>.local</c> suffix is dropped.
    /// <para>判断某行是否广播了所浏览的服务类型，比较前会去掉结尾的 <c>.local</c> 后缀。</para>
    /// </summary>
    /// <param name="entry">The row to test. <para>待测试的行。</para></param>
    /// <param name="browseType">The locally browsed type name. <para>本地浏览的类型名。</para></param>
    /// <returns>Whether the row carries that type. <para>该行是否属于该类型。</para></returns>
    internal static bool IsType(AdbServerMdnsEntry entry, string browseType) =>
        string.Equals(StripLocalSuffix(entry.ServiceType), StripLocalSuffix(browseType),
            StringComparison.OrdinalIgnoreCase);

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
        new(entry.Instance, null, entry.Port, [entry.Host], EmptyProperties, entry.ServiceType);
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
/// <c>false</c> means no mDNS table is exposed.
/// <para>循环期间 server 是否应答了该命令；为 <c>false</c> 表示未暴露 mDNS 表。</para></param>
/// <param name="Entry">The matching row, or <c>null</c> when the deadline passed without
/// one. <para>匹配的行；期限到期仍无匹配时为 <c>null</c>。</para></param>
internal readonly record struct AdbServerMdnsLookup(bool Supported, AdbServerMdnsEntry? Entry);