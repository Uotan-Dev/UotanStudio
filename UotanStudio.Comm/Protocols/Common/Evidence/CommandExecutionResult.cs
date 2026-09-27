namespace UotanStudio.Comm.Protocols.Common.Evidence;

/// <summary>
/// Base type for the result of a multi-phase command execution across a protocol session.
/// <para>协议会话上多阶段命令执行结果的基础类型。</para>
/// </summary>
/// <remarks>
/// Concrete protocol results derive from this record and remain append-free: a result carries the
/// evidence list captured during execution, and consumers classify the run from <see cref="Evidence"/>
/// rather than from booleans or message text.
/// <para>具体协议结果从该记录派生且不可追加：结果携带执行期间采集的证据列表，
/// 消费者依据 <see cref="Evidence"/> 而非布尔值或消息文本判定执行状态。</para>
/// </remarks>
public abstract record CommandExecutionResult
{
    /// <summary>
    /// Gets the ordered evidence captured while the command run progressed.
    /// <para>获取命令推进过程中按序采集的证据。</para>
    /// </summary>
    public required IReadOnlyList<ProtocolEvidence> Evidence { get; init; }

    /// <summary>
    /// Gets a value indicating whether every recorded phase completed with a device acknowledgement.
    /// An empty evidence list is not treated as completion.
    /// <para>获取一个值，指示是否每个已记录阶段均以设备确认完成。空证据列表不视为完成。</para>
    /// </summary>
    public bool AllPhasesCompleted =>
        Evidence.Count > 0 &&
        Evidence.All(static evidence => evidence.Outcome == CommandOutcomeKind.Completed);

    /// <summary>
    /// Gets the outcome of the last recorded phase, or <see cref="CommandOutcomeKind.Unknown"/> when
    /// no phase was recorded.
    /// <para>获取最后一个已记录阶段的结果；未记录任何阶段时为 <see cref="CommandOutcomeKind.Unknown"/>。</para>
    /// </summary>
    public CommandOutcomeKind FinalOutcome =>
        Evidence.Count == 0 ? CommandOutcomeKind.Unknown : Evidence[^1].Outcome;
}