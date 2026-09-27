namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Defines explicit Sahara startup requirements for one EDL session.
/// <para>定义单个 EDL 会话的显式 Sahara 启动要求。</para></summary>
public sealed record EdlInitializationOptions
{
    /// <summary>Gets the optional pre-signed VIP packet sequence.
    /// <para>获取可选的预签名 VIP 包序列。</para></summary>
    /// <value>The profile installed before authentication and configure, or null for ordinary Firehose.
    /// <para>在认证与 configure 前安装的配置；普通 Firehose 为 null。</para></value>
    public FirehoseVipOptions? Vip { get; init; }

    /// <summary>Gets the explicitly selected Xiaomi signature provider, invoked before Firehose configuration.
    /// <para>获取在 Firehose 配置前调用的显式小米签名提供方。</para></summary>
    /// <value>The provider, or null when Xiaomi authentication was not selected.
    /// <para>提供方；未选择小米认证时为 null。</para></value>
    public IEdlXiaomiAuthenticationProvider? XiaomiAuthenticationProvider { get; init; }

    /// <summary>Gets an explicit OnePlus or Nothing project verification profile.
    /// <para>获取显式的 OnePlus 或 Nothing 项目校验配置。</para></summary>
    /// <value>The profile applied after configure but before storage detection, or null.
    /// <para>在 configure 后、存储检测前应用的配置，或 null。</para></value>
    public EdlProjectAuthenticationOptions? ProjectAuthentication { get; init; }

    /// <summary>Gets whether the selected OnePlus programmer requires the parameterless SetNetType command.
    /// <para>获取所选 OnePlus programmer 是否要求无参数 SetNetType 命令。</para></summary>
    /// <value>False by default. When explicitly enabled, the command follows configure and optional demacia,
    /// precedes software-project verification and storage detection, and is not proof of authentication.
    /// Ordinary setprojmodel, Nothing and Xiaomi authentication cannot be combined with this startup path.
    /// <para>默认为 false。显式启用后，命令在 configure 与可选 demacia 之后、
    /// 软件项目校验和存储检测之前执行，不代表认证成功。
    /// 此启动路径不能与普通 setprojmodel、Nothing 或小米认证组合。</para></value>
    public bool SendOnePlusNetType { get; init; }

    /// <summary>Gets the cooperative deadline for each vendor authentication or SetNetType exchange.
    /// <para>获取每次厂商认证或 SetNetType 交换的协作式截止时间。</para></summary>
    /// <value>The deadline in milliseconds, from 1 through 86400000.
    /// <para>以毫秒计的截止时间，范围为 1 至 86400000。</para></value>
    public int AuthenticationTimeoutMs { get; init; } = 60000;

    /// <summary>Gets whether QUD HELLO recovery is enabled.
    /// <para>获取是否启用 QUD HELLO 恢复。</para></summary>
    /// <value>False unless the selected driver profile requires recovery.
    /// <para>除非所选驱动配置要求恢复，否则为 false。</para></value>
    public bool QudHelloRecoveryEnabled { get; init; }

    /// <summary>Gets whether the selected single-image profile accepts pending as completion.
    /// <para>获取所选单镜像配置是否接受 pending 作为完成状态。</para></summary>
    /// <value>False by default; multiple-image transfers are never covered by this exception.
    /// <para>默认为 false；此例外从不涵盖多镜像传输。</para></value>
    public bool AllowSingleFirehosePendingCompletion { get; init; }

    /// <summary>Gets whether legacy Sahara device identity is required before programmer upload.
    /// <para>获取是否要求在上传 programmer 前取得传统 Sahara 设备身份。</para></summary>
    /// <value>False by default; true rejects Firehose-only connections and failed identity queries.
    /// <para>默认为 false；为 true 时拒绝仅 Firehose 连接和失败的身份查询。</para></value>
    public bool QueryIdentityBeforeUpload { get; init; }
}