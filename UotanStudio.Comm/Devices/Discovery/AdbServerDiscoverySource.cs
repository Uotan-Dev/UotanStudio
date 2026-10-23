using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Discovers server-owned targets, including offline and unauthorized serials.
/// <para>发现 server 管理的目标，包含离线与未授权的序列号。</para>
/// </summary>
/// <param name="host">
/// Hostname or address of the adb server to probe; defaults to loopback.
/// <para>待探测 adb server 的主机名或地址；默认为本机回环。</para>
/// </param>
/// <param name="port">
/// TCP port of the adb server; defaults to <c>5037</c>.
/// <para>adb server 的 TCP 端口；默认为 <c>5037</c>。</para>
/// </param>
public sealed class AdbServerDiscoverySource(string host = "127.0.0.1", int port = 5037) : IDeviceDiscoverySource
{
    /// <summary>Service type adb keys a transport with after it auto-connects a discovered secure
    /// daemon. <para>adb 自动连接其发现的安全 adbd 服务后用作传输键的服务类型。</para></summary>
    private const string TlsConnectService = "_adb-tls-connect._tcp";

    /// <summary>Service type of a legacy plaintext adb service.
    /// <para>较早明文 adb 服务的类型。</para></summary>
    private const string LegacyAdbService = "_adb._tcp";

    private static readonly string[] ConnectServiceTypes = [TlsConnectService, LegacyAdbService];

    private static readonly IReadOnlyDictionary<string, string> NoAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Source label embedding the probed host and port in the form <c>adb-server@host:port</c>.
    /// <para>以 adb-server@host:port 形式内嵌所探测主机与端口的源标签。</para>
    /// </summary>
    /// <value>The stable name of this discovery source. <para>该发现源的稳定名称。</para></value>
    public string Name => $"adb-server@{host}:{port}";

    /// <summary>Lists the adb server's targets when the fixed route snapshot marks the default endpoint available, and projects each into a <c>server</c>-routed endpoint stamped with the snapshot epoch; DNS-SD-keyed targets carry the endpoint resolved from the server's mDNS table. A target query that fails while the snapshot still marks the server available yields an empty list instead of stale endpoints.
    /// <para>当固定路由快照把默认端点标记为可用时列出 adb server 的目标，并把每个目标投影为盖上快照 epoch 的
    /// <c>server</c> 路由端点；以 DNS-SD 实例名为键的目标附带 server mDNS 表解析出的端点。快照仍标记 server
    /// 可用但目标查询失败时返回空列表，而不是保留过期端点。</para></summary>
    /// <param name="cancellationToken">Cancels the liveness probe and the target query.
    /// <para>取消存活探测与目标查询。</para></param>
    /// <returns>One endpoint per server target, or an empty list when the server is absent or its list cannot be read.
    /// <para>每个 server 目标对应一个端点；server 不存在或列表无法读取时为空列表。</para></returns>
    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!DebugBridgeRoute.UseAdbServer(host, port))
        {
            return [];
        }

        var routeEpoch = DebugBridgeRoute.Current.Epoch;
        IReadOnlyList<AdbTargetInfo> targets;
        try
        {
            targets = await Task.Run(() => new AdbServerServices(string.Empty, host, port).ListTargets(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is IOException or System.Net.Sockets.SocketException)
        {
            return [];
        }
        cancellationToken.ThrowIfCancellationRequested();
        var aliases = await ResolveMdnsAliasesAsync(targets, host, port, cancellationToken).ConfigureAwait(false);
        return [.. targets.Select(target =>
        {
            var alias = aliases.TryGetValue(NormalizeInstance(target.Serial), out var address) ? address : null;
            return new DeviceEndpoint
            {
                Source = Name,
                Transport = "adb-server",
                Address = target.Serial,
                Serial = target.Serial,
                DisplayName = target.Serial,
                Protocol = "adb",
                Route = "server",
                RouteEpoch = routeEpoch,
                State = target.State,
                TargetTransport = target.Transport.Length == 0 && alias is not null ? "TCP"
                    : target.Transport.Length == 0 ? null : target.Transport,
                AliasAddress = alias
            };
        })];
    }

    /// <summary>
    /// Reads the server's mDNS table once, and only when a target is keyed by a DNS-SD instance name.
    /// <para>仅当存在以 DNS-SD 实例名为键的目标时，读取一次 server 的 mDNS 表。</para>
    /// </summary>
    /// <param name="targets">
    /// Server targets whose serials are checked for DNS-SD instance names.
    /// <para>待检查序列号是否为 DNS-SD 实例名的 server 目标。</para>
    /// </param>
    /// <param name="serverHost">
    /// Hostname of the adb server whose mDNS table is queried. <para>查询其 mDNS 表的 adb server 主机名。</para>
    /// </param>
    /// <param name="serverPort">
    /// TCP port of that adb server. <para>该 adb server 的 TCP 端口。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the mDNS query. <para>取消本次 mDNS 查询。</para>
    /// </param>
    /// <returns>
    /// A map from normalized instance names to resolved endpoints, empty when nothing resolves.
    /// <para>规范化实例名到已解析端点的映射；无可解析项时为空。</para>
    /// </returns>
    private static async Task<IReadOnlyDictionary<string, string>> ResolveMdnsAliasesAsync(
        IReadOnlyList<AdbTargetInfo> targets, string serverHost, int serverPort, CancellationToken cancellationToken)
    {
        if (!targets.Any(target => IsMdnsInstanceName(target.Serial)))
        {
            return NoAliases;
        }

        var query = await AdbServerMdns.QueryAsync(serverHost, serverPort, cancellationToken)
            .ConfigureAwait(false);
        if (query.Status != AdbServerMdnsStatus.Answered)
        {
            return NoAliases;
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in query.Entries)
        {
            if (!IsConnectService(entry.ServiceType))
            {
                continue;
            }

            aliases[NormalizeInstance(entry.Instance)] =
                DebugBridgeNetworkAddress.FormatEndpoint(entry.Host, entry.Port);
        }

        return aliases;
    }

    private static bool IsMdnsInstanceName(string serial) =>
        ConnectServiceTypes.Any(serviceType => NormalizeInstance(serial)
            .EndsWith("." + serviceType, StringComparison.OrdinalIgnoreCase));

    private static bool IsConnectService(string serviceType) =>
        ConnectServiceTypes.Any(candidate => string.Equals(
            NormalizeInstance(serviceType), candidate, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeInstance(string instance) => instance.TrimEnd('.');
}