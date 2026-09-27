namespace UotanStudio.Comm.Protocols.Common.Evidence;

/// <summary>
/// Describes the observed outcome of a single protocol phase or command exchange.
/// <para>描述单个协议阶段或命令交互的观测结果。</para>
/// The distinctions between "observed", "parsed", "authenticated", and "device confirmed execution"
/// are expressed by attaching evidence at the correct phase; an unknown device state is never
/// collapsed into <see cref="Completed"/>.
/// <para>“观察到”“解析到”“认证通过”“设备确认执行”的区分由在正确阶段挂载证据来表达；
/// 设备端状态未知时绝不折叠为 <see cref="Completed"/>。</para>
/// </summary>
public enum CommandOutcomeKind
{
    /// <summary>
    /// The device-side state for this phase is unknown and must not be assumed benign. This is the
    /// zero value so a default-constructed or deserialized-from-missing outcome fails closed.
    /// <para>该阶段的设备端状态未知，不得假定为无害。其为默认零值，使默认构造或从缺失字段
    /// 反序列化出的结果按失败关闭处理。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The device acknowledged completion for this phase.
    /// <para>设备已确认该阶段完成。</para>
    /// </summary>
    Completed = 1,

    /// <summary>
    /// The device explicitly rejected or failed this phase.
    /// <para>设备明确拒绝或执行失败该阶段。</para>
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// No response arrived within the configured timeout.
    /// <para>在配置的超时内未收到响应。</para>
    /// </summary>
    Timeout = 3,

    /// <summary>
    /// The caller cancelled the operation before a terminal device acknowledgement.
    /// <para>调用方在收到终态设备确认前取消了操作。</para>
    /// </summary>
    Cancelled = 4,

    /// <summary>
    /// The transport was lost during this phase.
    /// <para>该阶段期间传输链路丢失。</para>
    /// </summary>
    ConnectionLost = 5,

    /// <summary>
    /// Bytes or records were transferred, but less than the requested amount.
    /// <para>已传输部分字节或记录，但少于请求数量。</para>
    /// </summary>
    Partial = 6
}