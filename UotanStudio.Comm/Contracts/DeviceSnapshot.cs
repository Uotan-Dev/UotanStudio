using System.Collections.Immutable;

namespace UotanStudio.Comm.Contracts;

/// <summary>
/// A point-in-time discovery result. A later refresh invalidates its generation.
/// <para>某一时间点的发现结果。后续刷新会使它的代号失效。</para>
/// </summary>
/// <param name="Generation">
/// Counter identifying this commit; <c>0</c> is the empty snapshot taken before the first refresh
/// and a lease accepts only the live value.
/// <para>标识本次提交的计数器；<c>0</c> 表示尚未刷新过的空快照，租用操作只接受当前生效的值。</para>
/// </param>
/// <param name="Endpoints">
/// Endpoints gathered by that refresh, each already validated for source, non-blank transport and
/// address, and unique identity; empty when nothing was discovered.
/// <para>该次刷新收集到的端点，每个都已校验来源、非空的传输与地址以及唯一标识；未发现任何设备时为空。</para>
/// </param>
public sealed record DeviceSnapshot(long Generation, ImmutableArray<DeviceEndpoint> Endpoints);