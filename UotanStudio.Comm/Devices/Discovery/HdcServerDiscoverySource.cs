using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Discovers authoritative HDC server connect keys and target states.
/// <para>发现权威的 HDC server 连接键与目标状态。</para>
/// </summary>
/// <param name="host">
/// Hostname or address of the HDC server to probe; defaults to loopback.
/// <para>待探测 HDC server 的主机名或地址；默认为本机回环。</para>
/// </param>
/// <param name="port">
/// Explicit TCP port; <see langword="null"/> selects <see cref="HdcServerClient.DefaultServerPort"/>.
/// <para>显式 TCP 端口；<see langword="null"/> 选择 <see cref="HdcServerClient.DefaultServerPort"/>。</para>
/// </param>
public sealed class HdcServerDiscoverySource(string host = "127.0.0.1", int? port = null) : IDeviceDiscoverySource
{
    private readonly int _port = port ?? HdcServerClient.DefaultServerPort;

    /// <summary>
    /// Source label embedding the probed host and the resolved port.
    /// <para>内嵌所探测主机与已解析端口的源标签。</para>
    /// </summary>
    /// <value>The stable name of this discovery source. <para>该发现源的稳定名称。</para></value>
    public string Name => $"hdc-server@{host}:{_port}";

    /// <summary>Runs <c>list targets -v</c> against the HDC server and projects each parsed target into a <c>server</c>-routed endpoint keyed by its connect key; returns an empty list when the server is marked absent.
    /// <para>对 HDC server 执行 <c>list targets -v</c>，并把每个解析出的目标投影为以连接键标识的 <c>server</c> 路由端点；server 被标记为不存在时返回空列表。</para></summary>
    /// <param name="cancellationToken">Cancels the server query.<para>取消 server 查询。</para></param>
    /// <returns>One endpoint per listed target, or an empty list when the server is marked absent.
    /// <para>每个已列出的目标对应一个端点；server 被标记为不存在时为空列表。</para></returns>
    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!DebugBridgeRoute.UseHdcServer(host, _port))
        {
            return [];
        }

        using var client = new HdcServerClient(host, _port);
        var output = await client.ExecuteAsync(string.Empty, "list targets -v",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. HdcTargetInfo.Parse(Encoding.UTF8.GetString(output)).Select(target => new DeviceEndpoint
        {
            Source = Name,
            Transport = "hdc-server",
            Address = target.ConnectKey,
            Serial = target.ConnectKey,
            DisplayName = target.Name,
            Protocol = "hdc",
            Route = "server",
            State = target.State,
            TargetTransport = target.Transport
        })];
    }
}