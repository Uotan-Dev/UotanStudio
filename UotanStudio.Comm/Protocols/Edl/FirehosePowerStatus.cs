namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Describes observed power-command evidence, not successful boot into an operating system.
/// <para>描述所观察到的电源命令证据，并不表示已成功引导进入操作系统。</para>
/// </summary>
public enum FirehosePowerStatus
{
    /// <summary>
    /// No power command has been attempted on this protocol instance.
    /// <para>该协议实例尚未尝试发送任何电源命令。</para>
    /// </summary>
    NotRequested,
    /// <summary>
    /// The power command is being sent or its response is pending.
    /// <para>电源命令正在发送中，或其响应尚未到达。</para>
    /// </summary>
    AwaitingResponse,
    /// <summary>
    /// The target returned a normal-mode NAK.
    /// <para>目标在正常模式下返回了 NAK。</para>
    /// </summary>
    Rejected,
    /// <summary>
    /// The target acknowledged the command; its subsequent boot state is unverified.
    /// <para>目标已确认该命令；其后续引导状态未经校验。</para>
    /// </summary>
    Acknowledged,
    /// <summary>
    /// The target acknowledged the command and the transport later reported disconnected.
    /// <para>目标已确认该命令，且传输层随后报告已断开。</para>
    /// </summary>
    AcknowledgedAndDisconnected,
    /// <summary>
    /// The command outcome is uncertain because no valid ACK or NAK was received.
    /// <para>由于未收到有效的 ACK 或 NAK，命令结果不确定。</para>
    /// </summary>
    Unknown
}