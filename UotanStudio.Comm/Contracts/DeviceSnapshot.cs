using System.Collections.Immutable;

namespace UotanStudio.Comm.Contracts;

/// <summary>
/// A point-in-time discovery result. A later refresh invalidates its generation.
/// <para>某一时间点的发现结果。后续刷新会使它的代号失效。</para>
/// </summary>
/// <param name="Generation">
/// Counter identifying this commit; <c>0</c> marks the pre-refresh empty snapshot and leases
/// accept only the live value.
/// <para>标识本次提交的计数器；<c>0</c> 表示尚未刷新的空快照，租用操作只接受当前生效的值。</para>
/// </param>
/// <param name="Endpoints">
/// Endpoints gathered by that refresh, each validated for source, address and identity; empty
/// when none were discovered.
/// <para>该次刷新收集到的端点，均已校验来源、地址与唯一标识；未发现任何设备时为空。</para>
/// </param>
public sealed record DeviceSnapshot(long Generation, ImmutableArray<DeviceEndpoint> Endpoints);