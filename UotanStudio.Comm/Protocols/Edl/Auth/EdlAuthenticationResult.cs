using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Edl.Auth;

/// <summary>
/// What the session may do after a vendor authentication run was finalized.
/// <para>厂商认证运行定格后会话允许进行的操作。</para>
/// </summary>
public enum EdlAuthSessionDisposition
{
    /// <summary>
    /// No authentication was required and the session may continue normally.
    /// <para>无需认证，会话可正常继续。</para>
    /// </summary>
    CanContinue,

    /// <summary>
    /// The link is still usable but re-probing or re-configuration is required before further commands.
    /// <para>链路仍可用，但继续命令前需要重新探测或重新配置。</para>
    /// </summary>
    RequiresReconfiguration,

    /// <summary>
    /// The transport or protocol state is uncertain; the session must not send further commands and a
    /// reconnect (possibly a re-enumeration transition) is required.
    /// <para>传输或协议状态不确定；会话不得再发送命令，需要重连（可能伴随重枚举迁移）。</para>
    /// </summary>
    RequiresReconnect,

    /// <summary>
    /// The session is invalidated; only disposal or a fresh session is allowed.
    /// <para>会话已失效；仅允许释放或重新建立会话。</para>
    /// </summary>
    Invalidated
}

/// <summary>
/// The finalized result of the vendor authentication stages of one EDL initialization.
/// <para>单次 EDL 初始化中厂商认证阶段的定格结果。</para>
/// The evidence list holds one <see cref="ProtocolEvidence"/> per attempted stage with its observed
/// outcome; no key material, token bytes or sensitive challenge payloads are ever copied into it.
/// <para>证据列表为每个已尝试阶段保留一条 <see cref="ProtocolEvidence"/> 及其观测结果；
/// 密钥材料、令牌字节与敏感质询载荷绝不复制其中。</para>
/// </summary>
public sealed record EdlAuthenticationResult : CommandExecutionResult
{
    /// <summary>
    /// Gets the last stage the run reached or attempted.
    /// <para>获取本次运行到达或尝试的最后一个阶段。</para>
    /// </summary>
    /// <value>The terminal stage label. <para>终止阶段标签。</para></value>
    public required EdlAuthStage ReachedStage { get; init; }

    /// <summary>
    /// Gets whether authentication was demanded by the selected startup options.
    /// <para>获取所选启动配置是否要求认证。</para>
    /// </summary>
    /// <value><see langword="false"/> when only <see cref="EdlAuthStage.NotRequired"/> applies.
    /// <para>仅适用 <see cref="EdlAuthStage.NotRequired"/> 时为 <see langword="false"/>。</para></value>
    public required bool AuthenticationRequested { get; init; }

    /// <summary>
    /// Gets what the session may do next.
    /// <para>获取会话接下来的允许操作。</para>
    /// </summary>
    /// <value>The disposition; failure paths finalize as <see cref="EdlAuthSessionDisposition.RequiresReconnect"/>
    /// because the Firehose instance is invalidated. <para>处置结论；失败路径因 Firehose 实例被置为失效而定格为
    /// <see cref="EdlAuthSessionDisposition.RequiresReconnect"/>。</para></value>
    public required EdlAuthSessionDisposition Disposition { get; init; }
}