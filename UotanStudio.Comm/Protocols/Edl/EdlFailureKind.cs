namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Typed classification of an EDL command failure, replacing faulted booleans and message-text
/// parsing at every consumer boundary.
/// <para>EDL 命令失败的类型化分类，在所有消费者边界取代 faulted 布尔值与消息文本解析。</para>
/// The distinction between "the device rejected the command", "the response was lost after bytes
/// were already transferred", and "the host cancelled before acknowledgement" determines whether a
/// plan may be retried; consumers must branch on this value, not on exception text.
/// <para>“设备拒绝命令”“字节已传输后响应丢失”与“主机在确认前取消”的区分决定计划能否重试；
/// 消费者必须依据本值分支，而不是依据异常文本。</para>
/// </summary>
public enum EdlFailureKind
{
    /// <summary>
    /// No failure was observed.
    /// <para>未观测到失败。</para>
    /// </summary>
    None = 0,

    /// <summary>
    /// The device explicitly rejected the command (NAK or protocol-level failure response) and its
    /// storage state is unchanged by that command.
    /// <para>设备明确拒绝该命令（NAK 或协议层失败响应），该命令未改变其存储状态。</para>
    /// </summary>
    Rejected,

    /// <summary>
    /// A bounded wait for a response expired before any response arrived, and nothing had been
    /// transferred yet.
    /// <para>在任何响应到达之前有界的等待超时，且尚未传输任何数据。</para>
    /// </summary>
    Timeout,

    /// <summary>
    /// The transport failed or disconnected; the device-side state is unknown.
    /// <para>传输失败或断开；设备端状态未知。</para>
    /// </summary>
    ConnectionLost,

    /// <summary>
    /// The host cancelled before any command bytes reached the device.
    /// <para>主机在任何命令字节到达设备之前取消。</para>
    /// </summary>
    Cancelled,

    /// <summary>
    /// The host cancelled after command or payload bytes had already been written to the device;
    /// the device-side outcome is unknown and a reconnect is required. Never evidence that the
    /// device received nothing.
    /// <para>主机在命令或载荷字节已写入设备之后取消；设备端结果未知，必须重连。绝不能当作
    /// “设备未收到数据”的证据。</para>
    /// </summary>
    CancelledAfterTransfer,

    /// <summary>
    /// A complete payload was transferred but the final acknowledgement was lost, malformed, or
    /// timed out; the device may have committed the write. A reconnect is required.
    /// <para>完整载荷已传输，但最终确认丢失、格式错误或超时；设备可能已提交写入。必须重连。</para>
    /// </summary>
    CompletionLost,

    /// <summary>
    /// A response violated the protocol shape (raw-mode confusion, oversized or unparsable XML).
    /// <para>响应违反协议形态（原始模式混淆、超长或无法解析的 XML）。</para>
    /// </summary>
    ProtocolViolation,

    /// <summary>
    /// The negotiated capability snapshot does not report the command as supported, so it was
    /// refused before any byte reached the wire.
    /// <para>协商得到的能力快照未报告支持该命令，因此在任何字节上线之前被拒绝。</para>
    /// </summary>
    CapabilityNotReported,

    /// <summary>
    /// An identity, hash, geometry, or slot binding contradicted the plan.
    /// <para>身份、哈希、几何或槽位绑定与计划矛盾。</para>
    /// </summary>
    ResourceMismatch,

    /// <summary>
    /// The device-side state is unknown and the failure cannot be attributed to a more specific
    /// kind; consumers must reconnect rather than retry.
    /// <para>设备端状态未知，且无法归入更具体的类别；消费者必须重连而不是重试。</para>
    /// </summary>
    ExecutionUnknown
}

/// <summary>
/// Maps exceptions and observed transfer stages to <see cref="EdlFailureKind"/> values.
/// <para>将异常与观测到的传输阶段映射为 <see cref="EdlFailureKind"/> 值。</para>
/// </summary>
public static class EdlFailureClassification
{
    /// <summary>
    /// Classifies an exception thrown by an EDL operation.
    /// <para>对 EDL 操作抛出的异常进行分类。</para>
    /// </summary>
    /// <param name="error">The observed exception. <para>观测到的异常。</para></param>
    /// <returns>
    /// The typed failure kind: the kind already attached to an <c>EdlException</c> when present,
    /// otherwise one derived from the exception type and EDL error code.
    /// <para>类型化的失败种类：已附加在 <c>EdlException</c> 上的种类优先，否则由异常类型与 EDL
    /// 错误码推导。</para>
    /// </returns>
    public static EdlFailureKind Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is EdlException { FailureKind: { } attached })
        {
            return attached;
        }

        return error switch
        {
            OperationCanceledException => EdlFailureKind.Cancelled,
            TimeoutException => EdlFailureKind.Timeout,
            EdlException { ErrorCode: EdlErrorCode.ConnectionLost } => EdlFailureKind.ConnectionLost,
            EdlException
            {
                ErrorCode: EdlErrorCode.TransportTimeout or EdlErrorCode.ReadPortTimeout
                or EdlErrorCode.WritePortTimeout
            } => EdlFailureKind.Timeout,
            EdlException { ErrorCode: EdlErrorCode.NotSupported } => EdlFailureKind.CapabilityNotReported,
            EdlException { ErrorCode: EdlErrorCode.ProgrammerMismatch } => EdlFailureKind.ResourceMismatch,
            EdlException { ErrorCode: EdlErrorCode.FirehoseProtocolError } => EdlFailureKind.ProtocolViolation,
            _ => EdlFailureKind.ExecutionUnknown
        };
    }

    /// <summary>Classifies a failure observed around a raw transfer command.
    /// <para>对围绕原始传输命令观测到的失败进行分类。</para></summary>
    /// <param name="error">The observed exception.<para>观测到的异常。</para></param>
    /// <param name="transferStarted">Whether the device had entered raw mode and any payload bytes may have reached it.
    /// <para>设备是否已进入原始模式、是否可能有载荷字节已到达。</para></param>
    /// <param name="payloadSent">Whether the complete payload was written to the device.
    /// <para>完整载荷是否已写入设备。</para></param>
    /// <returns><see cref="EdlFailureKind.CompletionLost"/> when a complete payload waited in vain for the final acknowledgement, <see cref="EdlFailureKind.CancelledAfterTransfer"/> for cancellation once raw transfer had begun, <see cref="EdlFailureKind.ExecutionUnknown"/> for other failures after raw transfer began, and the plain classification otherwise.
    /// <para>完整载荷等待最终确认失败时返回 <see cref="EdlFailureKind.CompletionLost"/>；原始传输 开始后取消返回 <see cref="EdlFailureKind.CancelledAfterTransfer"/>；原始传输开始后的其他失败 返回 <see cref="EdlFailureKind.ExecutionUnknown"/>；其余按普通分类。</para></returns>
    public static EdlFailureKind ClassifyTransfer(Exception error, bool transferStarted, bool payloadSent)
    {
        if (payloadSent)
        {
            return EdlFailureKind.CompletionLost;
        }

        if (transferStarted)
        {
            return error is OperationCanceledException
                ? EdlFailureKind.CancelledAfterTransfer
                : EdlFailureKind.ExecutionUnknown;
        }

        return Classify(error);
    }
}