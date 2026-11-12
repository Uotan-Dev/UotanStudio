namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Defines explicit Sahara startup requirements for one EDL session.
/// <para>定义单个 EDL 会话的显式 Sahara 启动要求。</para></summary>
public sealed record EdlInitializationOptions
{
    /// <summary>Gets the optional pre-signed VIP packet sequence.
    /// <para>获取可选的预签名 VIP 包序列。</para></summary>
    /// <value>The profile installed before authentication and configure, or null for ordinary Firehose.
    /// <para>在认证与 configure 前安装的配置；普通 Firehose 为 null。</para></value>
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

    /// <summary>Gets the storage media the session configures before its first storage command.
    /// <para>获取会话在发出第一条存储命令之前所配置的存储介质。</para>
    /// </summary>
    /// <remarks>
    /// A programmer that acknowledges any memory name still opens the media it was told to use, and a target
    /// whose media is eMMC and told UFS (or the reverse) typically stops answering at its first storage
    /// command. Declaring the media the package was built for keeps that first command on the correct device.
    /// <para>对任意 memory 名称都返回确认的 programmer 仍会打开被告知的介质；介质为 eMMC 却被要求 UFS（或反之）
    /// 的目标通常会在第一条存储命令处停止应答。声明包所针对的介质可使首条存储命令落在正确的设备上。</para>
    /// </remarks>
    /// <value>The media to configure, or <see langword="null"/> to keep the configuration's current default.
    /// <para>要配置的介质；为 <see langword="null"/> 时保持配置当前的默认值。</para></value>
    public FirehoseStorageType? StorageType { get; init; }

    /// <summary>Gets whether a command-mode Sahara target with no programmer image may switch to
    /// the streaming DLOAD backend instead of failing the handshake.
    /// <para>获取在没有 programmer 镜像时，命令模式 Sahara 目标可否切换到流式 DLOAD 后端，而不是
    /// 让握手失败。</para></summary>
    /// <value>False by default, so an image-less handshake keeps raising the programmer-image error;
    /// when true and the device HELLO reports <see cref="SaharaMode.Command"/>, initialization drives
    /// the Sahara exec switch and exposes a streaming backend rather than configuring Firehose.
    /// <para>默认为 false，无镜像握手仍抛出 programmer 镜像错误；为 true 且设备 HELLO 报告
    /// <see cref="SaharaMode.Command"/> 时，初始化经 Sahara exec 切换并暴露流式后端，而非配置
    /// Firehose。</para></value>
    public bool AllowStreamDload { get; init; }

    /// <summary>Gets whether command headers for read, write and erase may proceed without waiting for a
    /// target acknowledgement, for the small class of
    /// Qualcomm loaders that never ACK reads or writes.
    /// <para>获取读、写与 erase 命令头是否可在不等待目标确认的情况下继续，用于从不确认读写的小众 Qualcomm loader。</para></summary>
    /// <value>False by default, preserving strict response waiting. When true it is copied to
    /// <see cref="FirehoseConfiguration.SkipResponseOnErase"/> and
    /// <see cref="FirehoseConfiguration.SkipResponseOnReadWrite"/> during initialization; the raw data and
    /// the final completion of a read or write are still consumed, and no timeout is shortened.
    /// <para>默认为 false，保持严格等待响应。为 true 时在初始化期间被复制到
    /// <see cref="FirehoseConfiguration.SkipResponseOnErase"/> 与
    /// <see cref="FirehoseConfiguration.SkipResponseOnReadWrite"/>；读写的原始数据与最终完成仍会被消费，
    /// 且不会缩短任何超时。</para></value>
    public bool SkipResponseOnReadWrite { get; init; }

    /// <summary>Gets the optional signature offered when the programmer refuses commands until vendor
    /// authentication completes.
    /// <para>获取 programmer 在厂商认证完成前拒绝命令时提交的可选签名。</para></summary>
    /// <remarks>
    /// The payload is a list of consecutive 256-byte signatures tried in order, so one file may carry several
    /// candidates; the host reads the file and the protocol never touches the file system. No signature is sent
    /// unless the target itself asked for authentication, so declaring one on an ordinary programmer costs nothing.
    /// <para>负载是按顺序尝试的连续 256 字节签名列表，因此一个文件可以携带多个候选；文件由宿主读取，协议不接触
    /// 文件系统。只有目标自身要求认证时才会提交签名，因此在普通 programmer 上声明签名不会产生任何代价。</para>
    /// </remarks>
    /// <value>The signature blocks, or <see langword="null"/> when none was supplied.
    /// <para>签名块；未提供时为 <see langword="null"/>。</para></value>
    public ReadOnlyMemory<byte>? VendorAuthSignature { get; init; }
}