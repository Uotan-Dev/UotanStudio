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
    /// Source label embedding the probed host and port, e.g. <c>adb-server@127.0.0.1:5037</c>.
    /// <para>内嵌所探测主机与端口的源标签，例如 <c>adb-server@127.0.0.1:5037</c>。</para>
    /// </summary>
    /// <value>The stable name of this discovery source. <para>该发现源的稳定名称。</para></value>
    public string Name => $"adb-server@{host}:{port}";

    /// <summary>
    /// Lists the adb server's targets and projects each into a <c>server</c>-routed endpoint carrying the
    /// server-reported link class.
    /// <para>列出 adb server 的目标，并把每个目标投影为携带 server 上报链路类别的 <c>server</c> 路由端点。</para>
    /// A target keyed by a DNS-SD instance name is additionally annotated with the endpoint the server's own
    /// mDNS table resolves it to, so a consumer can fold it together with the <c>host:port</c> target that
    /// reaches the same device.
    /// <para>以 DNS-SD 实例名为键的目标还会附带 server 自身 mDNS 表为其解析出的端点，使消费方能够把它与抵达
    /// 同一设备的 <c>host:port</c> 目标合并。</para>
    /// Returns an empty list when nothing is listening on the configured host and port; the target
    /// query runs on the thread pool and is re-checked for cancellation afterwards.
    /// <para>配置的宿主机与端口无监听时返回空列表；目标查询在线程池上执行，之后会再次检查取消。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the liveness probe and the target query. <para>取消存活探测与目标查询。</para>
    /// </param>
    /// <returns>
    /// One endpoint per server target, or an empty list when the server is absent.
    /// <para>每个 server 目标对应一个端点；server 不存在时为空列表。</para>
    /// </returns>
    /// <remarks>
    /// <para>
    /// 没有链路描述符的目标只有在 mDNS 表为它解析出 IP 端点后才记为 <c>TCP</c>：server 的解析结果本身就是
    /// 链路证据。解析不到时保持空值，界面按 <c>Unknown</c> 呈现，绝不按序列号文本形状推断链路类别。
    /// </para>
    /// <para>
    /// A target without a link descriptor is reported as <c>TCP</c> only after the mDNS table resolves it
    /// to an IP endpoint, since that resolution is itself link evidence; an unresolved name stays unreported
    /// rather than classified from its text shape.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!await DebugBridgeRoute.IsListeningAsync(host, port, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var targets = await Task.Run(() => new AdbServerServices(string.Empty, host, port).ListTargets(),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var aliases = await ResolveMdnsAliasesAsync(targets, host, port, cancellationToken).ConfigureAwait(false);
        return targets.Select(target =>
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
                State = target.State,
                TargetTransport = target.Transport.Length == 0 && alias is not null ? "TCP"
                    : target.Transport.Length == 0 ? null : target.Transport,
                AliasAddress = alias
            };
        }).ToArray();
    }

    /// <summary>
    /// Reads the server's mDNS table once, and only when a target is keyed by a DNS-SD instance name.
    /// <para>仅当存在以 DNS-SD 实例名为键的目标时，读取一次 server 的 mDNS 表。</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 普通仅 USB 的列表会跳过这条额外的 host 命令。无应答或不受支持的 mDNS 表不改变任何已上报事实：
    /// 无法解析的实例名按原样上报，绝不按文本形状猜测。
    /// </para>
    /// <para>
    /// An ordinary USB-only listing skips the extra host command. An unanswered or unsupported mDNS
    /// table changes no reported fact: an instance name that cannot be resolved is reported as-is.
    /// </para>
    /// </remarks>
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