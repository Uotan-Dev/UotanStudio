namespace UotanStudio.Comm.Protocols.Mtk.Sla;

/// <summary>
/// The observation point inside one authorization flow that a piece of evidence refers to.
/// <para>一条证据所指向的授权流程内部观测点。</para>
/// <see cref="DeviceRequired"/> and <see cref="ResponseAccepted"/> are deliberately separate
/// records: the device asking for authentication is not evidence that any response was accepted,
/// and an accepted response is not a blanket grant of later device operations.
/// <para><see cref="DeviceRequired"/> 与 <see cref="ResponseAccepted"/> 刻意分为两条记录：
/// 设备要求认证不是任何响应被接受的证据，响应被接受也不是后续设备操作的总体授权。</para>
/// </summary>
public enum MtkSlaEvidenceStage
{
    /// <summary>The device reported that this flow requires an authorized response.
    /// <para>设备报告该流程需要授权响应。</para></summary>
    DeviceRequired,

    /// <summary>The device released the exchange before a challenge was answered.
    /// <para>设备在挑战被应答之前即免除了本次交换。</para></summary>
    ChallengeWaived,

    /// <summary>The accepted response completed the exchange.
    /// <para>被接受的响应完成了本次交换。</para></summary>
    ResponseAccepted,

    /// <summary>The exchange was rejected: a malformed frame, an invalid length, an unknown status
    /// or a device rejection of the response.
    /// <para>交换被拒绝：帧格式错误、长度无效、状态未知或设备拒绝响应。</para></summary>
    Rejected,

    /// <summary>The cooperative exchange deadline expired; the device-side state is unknown and the
    /// flow must not be replayed on the same transport.
    /// <para>协作式交换截止时间已到；设备端状态未知，且不得在同一传输上重放该流程。</para></summary>
    Timeout,

    /// <summary>The caller cancelled at a cooperative point, including while the provider owned the
    /// challenge; no stale response may be sent afterwards.
    /// <para>调用方在协作点取消，包括响应方持有挑战期间；之后绝不得发送过期响应。</para></summary>
    ProviderCancelled
}