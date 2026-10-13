using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Mtk;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Borrow-one-transaction access to the Qualcomm EDL and MediaTek boot protocols for MCP tools.
/// <para>为 MCP 工具提供高通 EDL 与联发科 boot 协议的单次事务访问。</para>
/// The gateway is still the only device accessor: it selects a directly routable endpoint by the
/// protocol its descriptors identify, leases it for one operation, and releases the lease before the
/// call returns, so a tool never holds a USB handle and never reopens an endpoint another host owns.
/// <para>网关仍是唯一的设备访问者：它按描述符识别出的协议挑选可直接路由的端点，为单次操作租用该端点，
/// 并在调用返回前释放租约，因此工具既不持有 USB 句柄，也不会重开其他宿主已占有的端点。</para>
/// A host whose gateway does not own raw device access simply does not implement this interface, and
/// the boot-protocol tools then report that limitation instead of touching the bus.
/// <para>未自行持有裸设备访问的宿主网关不会实现本接口，此时 boot 协议工具只上报该限制，绝不触碰总线。</para>
/// </summary>
internal interface IBootProtocolGateway
{
    /// <summary>
    /// Runs one read transaction against an EDL endpoint.
    /// <para>对一个 EDL 端点执行一次读取事务。</para>
    /// </summary>
    /// <typeparam name="TResult">Value the callback produces from the borrowed session.
    /// <para>回调由借出的会话产出的值类型。</para></typeparam>
    /// <param name="target">EDL serial or USB device key from the inventory; when <see langword="null"/>
    /// or blank the call requires exactly one directly routable EDL endpoint and uses it.
    /// <para>清单中的 EDL 序列号或 USB 设备键；为 <see langword="null"/> 或空白时要求恰好存在一个
    /// 可直接路由的 EDL 端点并使用它。</para></param>
    /// <param name="operation">Callback that reads through the borrowed session and produces the result.
    /// <para>通过借出的会话读取并产出结果的回调。</para></param>
    /// <param name="cancellationToken">Token checked before discovery is refreshed, before the endpoint
    /// is leased and handed to the callback.
    /// <para>在刷新发现结果前、租用端点前以及交给回调前都会检查该令牌。</para></param>
    /// <returns>The value returned by <paramref name="operation"/>.
    /// <para><paramref name="operation"/> 返回的值。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.
    /// <para><paramref name="operation"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException"><paramref name="target"/> is absent and the directly
    /// routable EDL endpoints are not exactly one, the endpoint is already leased, or the leased endpoint
    /// carries no EDL session. <para><paramref name="target"/> 为空且可直接路由的 EDL 端点数量不恰好为一、
    /// 端点已被租用，或所租用的端点未提供 EDL 会话。</para></exception>
    /// <exception cref="IOException">No EDL endpoint matches <paramref name="target"/>, or several do.
    /// <para>没有 EDL 端点匹配 <paramref name="target"/>，或匹配者不止一个。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was requested.
    /// <para><paramref name="cancellationToken"/> 已请求取消。</para></exception>
    Task<TResult> RunEdlAsync<TResult>(string? target,
        Func<EdlSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one read transaction against an MTK boot-protocol endpoint.
    /// <para>对一个联发科 boot 协议端点执行一次读取事务。</para>
    /// </summary>
    /// <typeparam name="TResult">Value the callback produces from the borrowed session.
    /// <para>回调由借出的会话产出的值类型。</para></typeparam>
    /// <param name="target">MTK serial or USB device key from the inventory; when <see langword="null"/>
    /// or blank the call requires exactly one directly routable MTK endpoint and uses it.
    /// <para>清单中的 MTK 序列号或 USB 设备键；为 <see langword="null"/> 或空白时要求恰好存在一个
    /// 可直接路由的 MTK 端点并使用它。</para></param>
    /// <param name="operation">Callback that reads through the borrowed session and produces the result.
    /// <para>通过借出的会话读取并产出结果的回调。</para></param>
    /// <param name="cancellationToken">Token checked before discovery is refreshed, before the endpoint
    /// is leased and handed to the callback.
    /// <para>在刷新发现结果前、租用端点前以及交给回调前都会检查该令牌。</para></param>
    /// <returns>The value returned by <paramref name="operation"/>.
    /// <para><paramref name="operation"/> 返回的值。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.
    /// <para><paramref name="operation"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException"><paramref name="target"/> is absent and the directly
    /// routable MTK endpoints are not exactly one, the endpoint is already leased, or the leased endpoint
    /// carries no MTK session. <para><paramref name="target"/> 为空且可直接路由的 MTK 端点数量不恰好为一、
    /// 端点已被租用，或所租用的端点未提供 MTK 会话。</para></exception>
    /// <exception cref="IOException">No MTK endpoint matches <paramref name="target"/>, or several do.
    /// <para>没有 MTK 端点匹配 <paramref name="target"/>，或匹配者不止一个。</para></exception>
    /// <exception cref="NotSupportedException">The endpoint is logging-only or exposes no claimable
    /// command interface. <para>端点仅为日志接口，或未提供可申请声明的命令接口。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was requested.
    /// <para><paramref name="cancellationToken"/> 已请求取消。</para></exception>
    Task<TResult> RunMtkAsync<TResult>(string? target,
        Func<MtkSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken);
}