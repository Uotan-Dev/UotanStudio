using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk.Upload;

/// <summary>
/// The frozen outcome of one failed download-agent bring-up attempt: how far the run entered and
/// whether the operation may be replayed on a fresh transport without device-side risk.
/// <para>一次失败的下载代理启动尝试的定格结果：本次运行进入到了哪里，以及能否在无设备侧风险的前提下于新传输上重放该操作。</para>
/// From <see cref="MtkDaUploadStage.AgentStarted"/> onward, cancellation, timeout, or link loss
/// leaves the device-side state unknown: the result then reports the attempt as execution-unknown
/// with <see cref="RetrySafe"/> disabled, and only a reconnect is admissible.
/// <para>自 <see cref="MtkDaUploadStage.AgentStarted"/> 起，取消、超时或断链都会使设备端状态未知：
/// 此时结果将本次尝试视为执行未知并禁用 <see cref="RetrySafe"/>，只能重新连接。</para>
/// </summary>
public sealed record MtkDaUploadResult : CommandExecutionResult
{
    /// <summary>
    /// Gets the last stage the run had entered when the failure was observed.
    /// <para>获取观察到失败时本次运行已进入的最后一个阶段。</para>
    /// </summary>
    public required MtkDaUploadStage Reached { get; init; }

    /// <summary>
    /// Gets a value indicating whether the operation may be replayed from the start on a new
    /// transport. Stages up to the checksum confirmation replay safely; once the agent execution
    /// command has been issued, anything other than an explicit device rejection requires a
    /// reconnect instead of a replay.
    /// <para>获取一个值，指示能否在新传输上从头重放该操作。校验和确认之前的阶段可安全重放；
    /// 代理执行命令一旦发出，除设备明确拒绝外的任何结果都要求重新连接而非重放。</para>
    /// </summary>
    public required bool RetrySafe { get; init; }
}