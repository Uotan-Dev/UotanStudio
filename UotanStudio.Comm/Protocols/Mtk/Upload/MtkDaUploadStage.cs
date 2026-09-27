namespace UotanStudio.Comm.Protocols.Mtk.Upload;

/// <summary>
/// Ordered lifecycle stages of bringing up the MTK download agent, from the byte-stream handshake
/// to storage discovery. A stage names what the host has entered, and only a device
/// acknowledgement promotes it; the host never infers progress from elapsed time.
/// <para>启动 MTK 下载代理的有序生命周期阶段，从字节流握手一直到存储发现。阶段标识宿主已进入的位置，
/// 仅有设备确认才会推进它；宿主绝不根据耗时推断进度。</para>
/// </summary>
public enum MtkDaUploadStage
{
    /// <summary>
    /// No stage has been entered; nothing was sent to the device.
    /// <para>尚未进入任何阶段；未向设备发送任何内容。</para>
    /// </summary>
    NotStarted = 0,

    /// <summary>
    /// The complemented-byte handshake has begun.
    /// <para>反码字节握手已开始。</para>
    /// </summary>
    Handshake,

    /// <summary>
    /// Hardware and configuration identity queries (commands FD, FC, D8) have begun.
    /// <para>硬件与配置身份查询（命令 FD、FC、D8）已开始。</para>
    /// </summary>
    ChipInfoRequested,

    /// <summary>
    /// The device requested serial link authentication and the responder exchange has begun.
    /// <para>设备要求串行链路认证，应答交换已开始。</para>
    /// </summary>
    SlaChallenge,

    /// <summary>
    /// A download-agent load request (command D7 or the XFlash load opcode) has begun.
    /// <para>下载代理加载请求（命令 D7 或 XFlash 加载操作码）已开始。</para>
    /// </summary>
    AgentRequest,

    /// <summary>
    /// Agent payload bytes are being streamed to the device.
    /// <para>代理载荷字节正在向设备流式传输。</para>
    /// </summary>
    DataTransferring,

    /// <summary>
    /// The device confirmed the transferred agent checksum.
    /// <para>设备已确认已传输代理的校验和。</para>
    /// </summary>
    ChecksumConfirmed,

    /// <summary>
    /// The agent execution (jump) command has been issued; from here on, cancellation or link loss
    /// leaves the device-side state unknown and unreplayable.
    /// <para>代理执行（跳转）命令已发出；从此处起，取消或链路丢失会使设备端状态未知且不可重放。</para>
    /// </summary>
    AgentStarted,

    /// <summary>
    /// The running agent reported usable storage geometry.
    /// <para>运行中的代理已上报可用存储几何。</para>
    /// </summary>
    StorageDiscovered,

    /// <summary>
    /// The bootstrap run reached its configured terminal success point.
    /// <para>本次启动流程达到了其配置的终态成功点。</para>
    /// </summary>
    Completed
}