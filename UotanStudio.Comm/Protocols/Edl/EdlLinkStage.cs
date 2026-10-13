namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// The Sahara-to-Firehose link stage an EDL session is currently believed to occupy.
/// <para>EDL 会话当前被认定所处的 Sahara 到 Firehose 链路阶段。</para>
/// The stage is host-side bookkeeping derived from observed protocol progress; it never claims the
/// device reached a stage the host did not observe on the wire.
/// <para>该阶段是由已观测协议进度推导的宿主侧登记；从不在宿主未在线上观测到时声称设备到达某阶段。</para>
/// </summary>
public enum EdlLinkStage
{
    /// <summary>
    /// The session has not started initialization.
    /// <para>会话尚未开始初始化。</para>
    /// </summary>
    Idle,

    /// <summary>
    /// Sahara HELLO negotiation is in progress.
    /// <para>Sahara HELLO 协商正在进行。</para>
    /// </summary>
    SaharaConnecting,

    /// <summary>
    /// A programmer image is being uploaded through Sahara READ_DATA.
    /// <para>正在通过 Sahara READ_DATA 上传 programmer 镜像。</para>
    /// </summary>
    ProgrammerUploading,

    /// <summary>
    /// The programmer transfer completed and the device link is expected to re-enumerate into Firehose.
    /// <para>programmer 传输已完成，预期设备链路将重枚举进入 Firehose。</para>
    /// </summary>
    AwaitingReenumeration,

    /// <summary>
    /// Firehose configure and vendor startup exchanges are in progress.
    /// <para>Firehose configure 与厂商启动交换正在进行。</para>
    /// </summary>
    FirehoseVerifying,

    /// <summary>
    /// Firehose is configured and the session is ready for commands.
    /// <para>Firehose 已配置，会话可执行命令。</para>
    /// </summary>
    FirehoseReady,

    /// <summary>
    /// The transport faulted or reconnect became required; old commands must not run on this session.
    /// <para>传输已故障或需要重连；旧命令不得在此会话上继续执行。</para>
    /// </summary>
    Stale,

    /// <summary>
    /// Initialization failed; a relink offer may explain how to recover if endpoint evidence exists.
    /// <para>初始化失败；若存在端点证据，重链提议可说明恢复方式。</para>
    /// </summary>
    Failed
}