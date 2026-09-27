using UotanStudio.Comm.Devices.Discovery;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// A cross-protocol observation that one physical device surfaced with a different mode.
/// <para>对同一物理设备以不同模式出现的跨协议观测。</para>
/// Transitions are only published when the device identity correlates the old and the new
/// observation (container first, usable physical port second); the event carries the evidence
/// kind so consumers can weigh it, and never correlates by VID/PID or enumeration position.
/// <para>仅当设备身份能够关联新旧观测时（优先容器 ID，其次可用物理端口路径）才会发布迁移；
/// 事件携带关联所依据的证据类型供消费者权衡，绝不按 VID/PID 或枚举位置关联。</para>
/// </summary>
/// <param name="Identity">
/// The correlated physical device identity. <para>已关联的物理设备身份。</para>
/// </param>
/// <param name="SourceMode">
/// The protocol/mode string observed before the transition. <para>迁移前观测到的协议/模式标识。</para>
/// </param>
/// <param name="TargetMode">
/// The protocol/mode string observed after the transition. <para>迁移后观测到的协议/模式标识。</para>
/// </param>
/// <param name="Generation">
/// The transport generation the target observation was taken on. <para>目标观测所在的传输代际。</para>
/// </param>
/// <param name="Correlation">
/// The evidence kind that produced the correlation; both values never fall back to guessing.
/// <para>产生本次关联的证据类型；任何取值都不会回退为猜测。</para>
/// </param>
/// <param name="ObservedAtUtc">
/// The UTC instant the transition was committed. <para>提交本次迁移的 UTC 时刻。</para>
/// </param>
public sealed record ModeTransition(
    PhysicalDeviceIdentity Identity,
    string SourceMode,
    string TargetMode,
    TransportGeneration Generation,
    ModeTransitionEvidence Correlation,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// The evidence kind on which a mode transition correlated its two observations.
/// <para>模式迁移关联新旧观测所依据的证据类型。</para>
/// </summary>
public enum ModeTransitionEvidence
{
    /// <summary>
    /// Correlated by a matching OS container id.
    /// <para>依据匹配的操作系统容器 ID 关联。</para>
    /// </summary>
    Container,

    /// <summary>
    /// Correlated by a matching usable physical port path with no container verdict available.
    /// <para>在无容器判定可用时，依据匹配的可用物理端口路径关联。</para>
    /// </summary>
    PhysicalPort
}