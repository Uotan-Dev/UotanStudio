using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// The evidence an EDL session publishes so a caller can wait for the post-programmer USB
/// re-enumeration and open the replacement endpoint of the same physical device.
/// <para>EDL 会话发布的证据，供调用方等待写入 programmer 后的 USB 重枚举，
/// 并为同一物理设备打开替代端点。</para>
/// Correlation uses the attached physical identity (container id first, usable physical port second)
/// and the released previous endpoint; it never falls back to VID/PID, enumeration order or list
/// position. A session that never received a device endpoint can still publish identity-bearing offers
/// for a caller to complete with <c>with</c> expressions before opening the replacement endpoint.
/// <para>关联使用附加的物理身份（优先容器 ID，其次可用物理端口）与已释放的旧端点；
/// 绝不回退到 VID/PID、枚举顺序或列表位置。从未收到设备端点的会话仍可发布含身份的提议，
/// 由调用方用 <c>with</c> 表达式补全后打开替代端点。</para>
/// </summary>
public sealed record EdlRelinkOffer
{
    /// <summary>
    /// Gets the endpoint the previous (now stale or failed) transport was leased on.
    /// <para>获取旧（已失效或失败）传输所租用的端点。</para>
    /// </summary>
    /// <value>The exact previous endpoint, or <see langword="null"/> when the session had none and
    /// the caller must complete the offer. <para>精确的旧端点；会话没有端点时为 <see langword="null"/>，
    /// 需由调用方补全提议。</para></value>
    public required DeviceEndpoint? PreviousEndpoint { get; init; }

    /// <summary>
    /// Gets the best-evidence physical identity of the device behind the previous endpoint.
    /// <para>获取旧端点背后设备的最优物理身份。</para>
    /// </summary>
    /// <value>The correlated identity, or <see langword="null"/> when no stable evidence existed.
    /// <para>可关联的身份；不存在稳定证据时为 <see langword="null"/>。</para></value>
    public PhysicalDeviceIdentity? Identity { get; init; }

    /// <summary>
    /// Gets the transport generation the previous endpoint was opened on, when the controller stamped one.
    /// <para>获取旧端点打开所在的传输代际（若控制器盖印过）。</para>
    /// </summary>
    /// <value>The observed generation, or <see langword="null"/>. <para>观测到的代际，或 <see langword="null"/>。</para></value>
    public TransportGeneration? ObservedGeneration { get; init; }

    /// <summary>
    /// Gets the last Sahara-reported device identity observed on the old link.
    /// <para>获取旧链路上最近一次 Sahara 上报的设备身份。</para>
    /// </summary>
    /// <value>The legacy Sahara report, or <see langword="null"/> when none was queried successfully.
    /// <para>传统 Sahara 报告；未能成功查询时为 <see langword="null"/>。</para></value>
    public SaharaDeviceIdentity? LastIdentity { get; init; }

    /// <summary>
    /// Gets the recommended cooperative deadline for waiting for the replacement endpoint.
    /// <para>获取等待替代端点建议采用的协作式截止时间。</para>
    /// </summary>
    /// <value>Between 1 millisecond and 24 hours, matching the controller bound.
    /// <para>与控制器约束一致，介于 1 毫秒至 24 小时。</para></value>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the evidence kind the attached identity can correlate with.
    /// <para>获取附加身份可用于关联的证据类型。</para>
    /// </summary>
    /// <value>The correlation evidence, or <see langword="null"/> when the identity is absent.
    /// <para>关联证据；身份缺失时为 <see langword="null"/>。</para></value>
    public ModeTransitionEvidence? Correlation { get; init; }

    /// <summary>
    /// Gets the UTC instant the offer was produced.
    /// <para>获取生成该提议的 UTC 时刻。</para>
    /// </summary>
    /// <value>The creation timestamp. <para>创建时间戳。</para></value>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}