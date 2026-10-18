using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.Mtk.Sla;

/// <summary>
/// One immutable observation of an MTK authorization flow: which flow, which stage, which kind of
/// responder served it, the SHA-256 digest of the challenge when one was read, and the outcome.
/// <para>一条不可变的 MTK 授权流程观测记录：流程、阶段、响应提供方种类、读到挑战时的 SHA-256 摘要
/// （若读到），以及结果。</para>
/// The challenge is only ever stored as a digest: raw challenge bytes, private keys, tokens and
/// signed responses must never enter evidence, because evidence is serialised and may outlive the
/// responder lifetime. "Device required authentication" and "authentication response accepted"
/// are recorded as two separate entries; neither implies the other.
/// <para>挑战只以摘要形式存储：原始挑战字节、私钥、token 与已签名响应绝不可进入证据，
/// 因为证据会被序列化且生命周期可能超过响应方。"设备要求认证"与"认证响应被接受"
/// 记为两条独立证据，彼此不互相蕴含。</para>
/// </summary>
public sealed record MtkSlaEvidence
{
    /// <summary>
    /// Gets the authorization flow this observation belongs to.
    /// <para>获取本观测所属的授权流程。</para>
    /// </summary>
    public required MtkSlaFlowKind Flow { get; init; }

    /// <summary>
    /// Gets the observation point inside the flow.
    /// <para>获取流程内部的观测点。</para>
    /// </summary>
    public required MtkSlaEvidenceStage Stage { get; init; }

    /// <summary>
    /// Gets which class of responder served the flow, or <see cref="MtkSlaResponderKind.None"/> when
    /// the flow failed before a responder was consulted.
    /// <para>获取服务该流程的响应提供方种类；流程在咨询响应方之前即失败时为
    /// <see cref="MtkSlaResponderKind.None"/>。</para>
    /// </summary>
    public required MtkSlaResponderKind ResponderKind { get; init; }

    /// <summary>
    /// Gets the lowercase hexadecimal SHA-256 digest of the challenge bytes, when a challenge had
    /// been read at this observation point.
    /// <para>获取观测点已读到挑战时的小写十六进制 SHA-256 摘要；未读到挑战时为 <see langword="null"/>。</para>
    /// </summary>
    public string? ChallengeSha256Hex { get; init; }

    /// <summary>
    /// Gets the observed outcome for this stage.
    /// <para>获取该阶段的观测结果。</para>
    /// </summary>
    public required CommandOutcomeKind Outcome { get; init; }

    /// <summary>
    /// Gets an optional localization-neutral detail; failure stages carry a
    /// <c>SlaRejected</c>, <c>SlaTimeout</c> or <c>ProviderCancelled</c> classification token.
    /// <para>获取可选的与本地化无关的详情；失败阶段携带 <c>SlaRejected</c>、<c>SlaTimeout</c> 或
    /// <c>ProviderCancelled</c> 分类标记。</para>
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Computes the lowercase hexadecimal SHA-256 digest of challenge bytes without retaining them.
    /// <para>计算挑战字节的小写十六进制 SHA-256 摘要，不保留原始字节。</para>
    /// </summary>
    /// <param name="challenge">The device-supplied challenge bytes, typically short-lived buffers.
    /// <para>设备提供的挑战字节，通常为短暂存在的缓冲区。</para></param>
    /// <returns>
    /// The 64-character lowercase hex digest; never the challenge itself.
    /// <para>64 字符的小写十六进制摘要；绝不是挑战原文。</para>
    /// </returns>
    public static string ComputeChallengeSha256Hex(ReadOnlySpan<byte> challenge) =>
        Sha256.ComputeHexLower(challenge);

    /// <summary>
    /// Projects this observation onto the common evidence model for session and result consumption.
    /// <para>将本观测投影到通用证据模型，供会话与结果消费。</para>
    /// </summary>
    /// <returns>
    /// A protocol evidence record whose phase is <c>sla-&lt;flow&gt;-&lt;stage&gt;</c>, whose
    /// <see cref="ProtocolEvidence.ResourceSha256Hex"/> carries only the challenge digest and whose
    /// raw-response field is never populated.
    /// <para>阶段为 <c>sla-&lt;flow&gt;-&lt;stage&gt;</c> 的协议证据记录；其
    /// <see cref="ProtocolEvidence.ResourceSha256Hex"/> 仅携带挑战摘要，原始响应字段永不填充。</para>
    /// </returns>
    public ProtocolEvidence ToProtocolEvidence() => new()
    {
        Protocol = "mtk",
        Phase = $"sla-{ToPhaseName(Flow)}-{ToPhaseName(Stage)}",
        Outcome = Outcome,
        Detail = Detail,
        ResourceSha256Hex = ChallengeSha256Hex
    };

    /// <summary>Classifies an exception raised inside an authorization exchange into the failure stage and its detail token without altering the exception the caller observes.
    /// <para>将授权交换内抛出的异常分类到失败阶段及其详情标记，不改变调用方观察到的异常。</para></summary>
    /// <param name="error">The observed failure.<para>观察到的失败。</para></param>
    /// <param name="deadlineExpired">Whether the cooperative exchange deadline itself fired.
    /// <para>协作式交换截止时间是否已触发。</para></param>
    /// <returns>The failure stage and matching <c>SlaRejected</c>, <c>SlaTimeout</c> or <c>ProviderCancelled</c> classification token.
    /// <para>失败阶段及匹配的 <c>SlaRejected</c>、<c>SlaTimeout</c> 或 <c>ProviderCancelled</c> 分类标记。</para></returns>
    public static (MtkSlaEvidenceStage Stage, string Token) ClassifyFailure(Exception error, bool deadlineExpired)
        => error switch
        {
            TimeoutException => (MtkSlaEvidenceStage.Timeout, "SlaTimeout"),
            OperationCanceledException when !deadlineExpired =>
                (MtkSlaEvidenceStage.ProviderCancelled, "ProviderCancelled"),
            OperationCanceledException => (MtkSlaEvidenceStage.Timeout, "SlaTimeout"),
            _ => (MtkSlaEvidenceStage.Rejected, "SlaRejected")
        };

    private static string ToPhaseName(MtkSlaFlowKind flow) => flow switch
    {
        MtkSlaFlowKind.BootRomSla => "bootrom",
        MtkSlaFlowKind.DaSlaXFlash => "da-xflash",
        MtkSlaFlowKind.DaSlaXml => "da-xml",
        MtkSlaFlowKind.DirectDeviceAuthorization => "daa",
        _ => "boot-resource"
    };

    private static string ToPhaseName(MtkSlaEvidenceStage stage) => stage switch
    {
        MtkSlaEvidenceStage.DeviceRequired => "required",
        MtkSlaEvidenceStage.ChallengeWaived => "waived",
        MtkSlaEvidenceStage.ResponseAccepted => "accepted",
        MtkSlaEvidenceStage.Rejected => "rejected",
        MtkSlaEvidenceStage.Timeout => "timeout",
        _ => "cancelled"
    };
}