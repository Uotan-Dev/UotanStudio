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
    /// <summary>
    /// Source label embedding the probed host and port, e.g. <c>adb-server@127.0.0.1:5037</c>.
    /// <para>内嵌所探测主机与端口的源标签，例如 <c>adb-server@127.0.0.1:5037</c>。</para>
    /// </summary>
    /// <value>The stable name of this discovery source. <para>该发现源的稳定名称。</para></value>
    public string Name => $"adb-server@{host}:{port}";

    /// <summary>
    /// Lists the adb server's targets and projects each into a <c>server</c>-routed endpoint.
    /// <para>列出 adb server 的目标，并把每个目标投影为 <c>server</c> 路由端点。</para>
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
    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!await DebugBridgeRoute.IsListeningAsync(host, port, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var targets = await Task.Run(() => new AdbServerServices(string.Empty, host, port).ListTargets(),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return targets.Select(target => new DeviceEndpoint
        {
            Source = Name,
            Transport = "adb-server",
            Address = target.Serial,
            Serial = target.Serial,
            DisplayName = target.Serial,
            Protocol = "adb",
            Route = "server",
            State = target.State
        }).ToArray();
    }
}