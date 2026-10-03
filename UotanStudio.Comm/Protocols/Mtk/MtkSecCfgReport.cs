namespace UotanStudio.Comm.Protocols.Mtk.Security;

/// <summary>The secure-boot configuration layout found on the <c>seccfg</c> partition.
/// <para>在 <c>seccfg</c> 分区上发现的安全启动配置布局。</para></summary>
/// <remarks>
/// The reference client selects a layout by header bytes only: the <c>AND_SECCFG_v</c> prefix
/// (first 0xC bytes) means version 3 and the little-endian dword <c>0x4D4D4D4D</c> means version 4,
/// see mtkclient Library/DA/xflash/extension/xflash.py:582-592 and
/// Library/DA/legacy/extension/legacy.py:183-190. An unknown header is rejected rather than guessed.
/// <para>参考客户端仅按头部字节选择布局：前 0xC 字节为 <c>AND_SECCFG_v</c> 表示版本 3，
/// 小端双字 <c>0x4D4D4D4D</c> 表示版本 4，参见 mtkclient Library/DA/xflash/extension/xflash.py:582-592
/// 与 Library/DA/legacy/extension/legacy.py:183-190。未知头部会被拒绝而不会被猜测。</para>
/// </remarks>
public enum MtkSecCfgFormat
{
    /// <summary>The version 3 layout with the <c>AND_SECCFG_v</c> banner (seccfg.py:138-228).
    /// <para>带 <c>AND_SECCFG_v</c> 标头的版本 3 布局（seccfg.py:138-228）。</para></summary>
    Version3,
    /// <summary>The compact version 4 layout of seven dwords plus a trailing hash (seccfg.py:11-102).
    /// <para>由 7 个双字加尾部摘要组成的紧凑版本 4 布局（seccfg.py:11-102）。</para></summary>
    Version4
}

/// <summary>The caller's requested change to the secure-boot lock state.
/// <para>调用方请求的安全启动锁定状态变更。</para></summary>
/// <remarks>
/// The reference client accepts exactly the two flags <c>lock</c> and <c>unlock</c> for the whole
/// seccfg operation and rejects anything else (xflash.py:564-566, legacy.py:165-167). Closing the
/// configuration is the <see cref="Lock"/> flag and opening it is the <see cref="Unlock"/> flag;
/// no other state transition is documented by the evidence and none is offered.
/// <para>参考客户端对整个 seccfg 操作只接受 <c>lock</c> 与 <c>unlock</c> 两个标志，其他值一律拒绝
/// （xflash.py:564-566、legacy.py:165-167）。关闭配置对应 <see cref="Lock"/>，打开对应
/// <see cref="Unlock"/>；证据未记载其他状态转换，因此也不提供。</para>
/// </remarks>
public enum MtkSecCfgLockIntent
{
    /// <summary>Close the configuration: the reference client writes lock state 1 with critical lock state 0.
    /// <para>关闭配置：参考客户端写入锁定状态 1 与关键锁定状态 0。</para></summary>
    Lock,
    /// <summary>Open the configuration: the reference client writes lock state 3 with critical lock state 1.
    /// <para>打开配置：参考客户端写入锁定状态 3 与关键锁定状态 1。</para></summary>
    Unlock
}

/// <summary>The lock-state values documented by the reference implementation.
/// <para>参考实现所记载的锁定状态取值。</para></summary>
/// <remarks>
/// These are the values listed at seccfg.py:65-70. Note that the writer documented at
/// seccfg.py:83-88 closes the configuration with <see cref="Default"/> (1) rather than
/// <see cref="Lock"/> (4) and opens it with <see cref="Unlock"/> (3); the writer's choice is what
/// <see cref="MtkSecCfg"/> reproduces, and any other stored value is reported unchanged.
/// <para>这些取值来自 seccfg.py:65-70。注意 seccfg.py:83-88 的写入器用 <see cref="Default"/> (1)
/// 而非 <see cref="Lock"/> (4) 关闭配置，用 <see cref="Unlock"/> (3) 打开配置；
/// <see cref="MtkSecCfg"/> 复现的是写入器的选择，其余存储值原样上报。</para>
/// </remarks>
public enum MtkSecCfgLockState
{
    /// <summary>The factory default value 1, which the documented writer uses for a closed configuration.
    /// <para>出厂默认值 1，已记载的写入器用其表示已关闭的配置。</para></summary>
    Default = 1,
    /// <summary>The mass-production default value 2.
    /// <para>量产默认值 2。</para></summary>
    MpDefault = 2,
    /// <summary>The unlocked value 3.
    /// <para>已解锁值 3。</para></summary>
    Unlock = 3,
    /// <summary>The locked value 4.
    /// <para>已锁定值 4。</para></summary>
    Lock = 4,
    /// <summary>The verified value 5.
    /// <para>已验证值 5。</para></summary>
    Verified = 5,
    /// <summary>The custom value 6.
    /// <para>自定义值 6。</para></summary>
    Custom = 6
}

/// <summary>The critical lock-state values documented by the reference implementation.
/// <para>参考实现所记载的关键锁定状态取值。</para></summary>
/// <remarks>
/// Documented at seccfg.py:71-72 and written at seccfg.py:85 and :88, where closing the
/// configuration stores 0, a value the table itself does not name.
/// <para>记载于 seccfg.py:71-72，并在 seccfg.py:85 与 :88 写入；关闭配置时存入 0，而该取值本身未在表中命名。</para>
/// </remarks>
public enum MtkSecCfgCriticalLockState
{
    /// <summary>The unlocked value 1.
    /// <para>已解锁值 1。</para></summary>
    Unlock = 1,
    /// <summary>The locked value 2.
    /// <para>已锁定值 2。</para></summary>
    Lock = 2
}

/// <summary>The secure-boot runtime values documented by the reference implementation.
/// <para>参考实现所记载的安全启动运行时取值。</para></summary>
/// <remarks>Documented at seccfg.py:73-74. <para>记载于 seccfg.py:73-74。</para></remarks>
public enum MtkSecCfgRuntimeState
{
    /// <summary>Secure-boot runtime disabled (0).
    /// <para>禁用安全启动运行时（0）。</para></summary>
    Off = 0,
    /// <summary>Secure-boot runtime enabled (1).
    /// <para>启用安全启动运行时（1）。</para></summary>
    On = 1
}

/// <summary>Which transformation protected the version 4 configuration hash.
/// <para>用于保护版本 4 配置摘要的变换方式。</para></summary>
/// <remarks>
/// The reference client probes the stored hash against three transforms in order,
/// software first, then two hardware engine variants (seccfg.py:51-63). Only the software
/// variant is reproducible off-device, so the hardware variants are reported as
/// <see cref="Unresolved"/> unless a caller supplies evidence for them.
/// <para>参考客户端按顺序对存储的摘要试探三种变换：先软件，再两种硬件引擎变体（seccfg.py:51-63）。
/// 只有在设备外可复现的是软件变体，因此在调用方未提供证据时，硬件变体上报为 <see cref="Unresolved"/>。</para>
/// </remarks>
public enum MtkSecCfgHashKind
{
    /// <summary>The software transform reproduced by <see cref="MtkSecCfg"/> matched the stored hash.
    /// <para><see cref="MtkSecCfg"/> 复现的软件变换与存储摘要匹配。</para></summary>
    Software,
    /// <summary>No reproducible transform matched; the target may use a hardware engine.
    /// <para>没有可复现的变换匹配；目标可能使用硬件引擎。</para></summary>
    Unresolved
}

/// <summary>The decoded version 4 secure-boot configuration, with every raw value preserved.
/// <para>已解码的版本 4 安全启动配置，所有原始取值均予保留。</para></summary>
/// <param name="HeaderHex">The exact 28 header bytes as uppercase hex.
/// <para>28 个头部字节的精确大写十六进制表示。</para></param>
/// <param name="Magic">The raw structure magic dword.
/// <para>原始结构魔数双字。</para></param>
/// <param name="Version">The raw configuration version dword.
/// <para>原始配置版本双字。</para></param>
/// <param name="Size">The raw declared structure size dword.
/// <para>原始声明结构大小双字。</para></param>
/// <param name="LockStateRaw">The raw lock-state dword.
/// <para>原始锁定状态双字。</para></param>
/// <param name="CriticalLockStateRaw">The raw critical lock-state dword.
/// <para>原始关键锁定状态双字。</para></param>
/// <param name="RuntimeRaw">The raw secure-boot runtime dword.
/// <para>原始安全启动运行时双字。</para></param>
/// <param name="EndFlag">The raw end marker dword.
/// <para>原始结束标志双字。</para></param>
/// <param name="StoredHashHex">The raw 32 stored hash bytes as uppercase hex.
/// <para>存储的 32 字节摘要的大写十六进制表示。</para></param>
/// <param name="ComputedHashHex">The SHA-256 digest recomputed over the canonical header.
/// <para>对规范头部重新计算的 SHA-256 摘要。</para></param>
/// <param name="HashKind">Which transform was shown to protect the stored hash.
/// <para>已证实用于保护存储摘要的变换方式。</para></param>
public sealed record MtkSecCfgV4Report(
    string HeaderHex,
    uint Magic,
    uint Version,
    uint Size,
    uint LockStateRaw,
    uint CriticalLockStateRaw,
    uint RuntimeRaw,
    uint EndFlag,
    string StoredHashHex,
    string ComputedHashHex,
    MtkSecCfgHashKind HashKind)
{
    /// <summary>Gets the documented lock-state name for the raw value, or null when the value is
    /// not one of the six documented entries.
    /// <para>获取原始取值对应的已记载锁定状态名；取值不属于六个已记载条目时为 null。</para></summary>
    /// <value>
    /// The decoded <see cref="MtkSecCfgLockState"/>, or <see langword="null"/>.
    /// <para>解码后的 <see cref="MtkSecCfgLockState"/>，或 <see langword="null"/>。</para>
    /// </value>
    public MtkSecCfgLockState? LockState => LockStateRaw switch
    {
        1 => MtkSecCfgLockState.Default,
        2 => MtkSecCfgLockState.MpDefault,
        3 => MtkSecCfgLockState.Unlock,
        4 => MtkSecCfgLockState.Lock,
        5 => MtkSecCfgLockState.Verified,
        6 => MtkSecCfgLockState.Custom,
        _ => null
    };

    /// <summary>Gets the documented critical lock-state name for the raw value, or null when the
    /// value is unnamed by the evidence (the documented writer stores 0 when closing).
    /// <para>获取原始取值对应的已记载关键锁定状态名；证据未命名该取值时为 null（已记载的写入器在关闭时存入 0）。</para></summary>
    /// <value>
    /// The decoded <see cref="MtkSecCfgCriticalLockState"/>, or <see langword="null"/>.
    /// <para>解码后的 <see cref="MtkSecCfgCriticalLockState"/>，或 <see langword="null"/>。</para>
    /// </value>
    public MtkSecCfgCriticalLockState? CriticalLockState => CriticalLockStateRaw switch
    {
        1 => MtkSecCfgCriticalLockState.Unlock,
        2 => MtkSecCfgCriticalLockState.Lock,
        _ => null
    };

    /// <summary>Gets the documented secure-boot runtime name for the raw value, or null when the
    /// value is outside the documented 0 and 1.
    /// <para>获取原始取值对应的已记载安全启动运行时名；取值超出记载的 0 与 1 时为 null。</para></summary>
    /// <value>
    /// The decoded <see cref="MtkSecCfgRuntimeState"/>, or <see langword="null"/>.
    /// <para>解码后的 <see cref="MtkSecCfgRuntimeState"/>，或 <see langword="null"/>。</para>
    /// </value>
    public MtkSecCfgRuntimeState? Runtime => RuntimeRaw switch
    {
        0 => MtkSecCfgRuntimeState.Off,
        1 => MtkSecCfgRuntimeState.On,
        _ => null
    };

    /// <summary>Gets whether the documented writer would read this configuration as closed.
    /// <para>获取已记载的写入器是否会把该配置判定为已关闭。</para></summary>
    /// <value>
    /// <see langword="true"/> when the raw lock state is 1, the value the writer uses for a closed
    /// configuration (seccfg.py:79 and :87).
    /// <para>当原始锁定状态为 1（写入器表示已关闭配置的值，seccfg.py:79 与 :87）时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsClosed => LockStateRaw == 1;

    /// <summary>Gets whether the documented writer would read this configuration as open.
    /// <para>获取已记载的写入器是否会把该配置判定为已打开。</para></summary>
    /// <value>
    /// <see langword="true"/> when the raw lock state is 3, the value the writer uses for an open
    /// configuration (seccfg.py:81 and :84).
    /// <para>当原始锁定状态为 3（写入器表示已打开配置的值，seccfg.py:81 与 :84）时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsOpen => LockStateRaw == 3;
}

/// <summary>The decoded version 3 secure-boot configuration header.
/// <para>已解码的版本 3 安全启动配置头部。</para></summary>
/// <remarks>
/// Only the plaintext banner and header dwords documented at seccfg.py:183-199 are decoded. The
/// body behind the header is opaque without the target's SEJ engine, so its length and digest are
/// preserved instead of its bytes; see <see cref="MtkSecCfg"/> for the refusal rationale.
/// <para>仅解码 seccfg.py:183-199 记载的明文标头与头部双字。头部之后的内容在没有目标 SEJ 引擎时不可读，
/// 因此保留其长度与摘要而非字节；拒绝理由见 <see cref="MtkSecCfg"/>。</para>
/// </remarks>
/// <param name="BannerHex">The exact 16 banner bytes as uppercase hex.
/// <para>16 个标头字节的精确大写十六进制表示。</para></param>
/// <param name="Magic">The raw structure magic dword.
/// <para>原始结构魔数双字。</para></param>
/// <param name="Version">The raw configuration version dword.
/// <para>原始配置版本双字。</para></param>
/// <param name="Size">The raw declared structure size dword.
/// <para>原始声明结构大小双字。</para></param>
/// <param name="EncryptionOffset">The raw encryption offset dword.
/// <para>原始加密偏移双字。</para></param>
/// <param name="EncryptionLength">The raw encryption length dword used by the documented writer to
/// mark the state (seccfg.py:192, :233 and :236).
/// <para>原始加密长度双字，已记载的写入器用它标识状态（seccfg.py:192、:233 与 :236）。</para></param>
/// <param name="SoftwareLockTry">The raw single-byte software lock attempt counter.
/// <para>原始单字节软件锁定尝试计数。</para></param>
/// <param name="SoftwareLockDone">The raw single-byte software lock completion counter.
/// <para>原始单字节软件锁定完成计数。</para></param>
/// <param name="PageSize">The raw 16-bit page size.
/// <para>原始 16 位页大小。</para></param>
/// <param name="PageCount">The raw 32-bit page count.
/// <para>原始 32 位页数。</para></param>
/// <param name="EndFlag">The raw end marker dword.
/// <para>原始结束标志双字。</para></param>
/// <param name="BodyLength">The byte length of the opaque body between the header and the end marker.
/// <para>头部与结束标志之间不透明主体的字节长度。</para></param>
/// <param name="BodySha256Hex">The SHA-256 digest of those opaque body bytes.
/// <para>该不透明主体字节的 SHA-256 摘要。</para></param>
public sealed record MtkSecCfgV3Report(
    string BannerHex,
    uint Magic,
    uint Version,
    uint Size,
    uint EncryptionOffset,
    uint EncryptionLength,
    byte SoftwareLockTry,
    byte SoftwareLockDone,
    ushort PageSize,
    uint PageCount,
    uint EndFlag,
    int BodyLength,
    string BodySha256Hex)
{
    /// <summary>Gets the lock classification the documented writer expresses through the
    /// encryption-length dword, or null for any other value.
    /// <para>获取已记载的写入器通过加密长度双字表达的锁定分类；其他取值为 null。</para></summary>
    /// <value>
    /// <see cref="MtkSecCfgLockIntent.Lock"/> for 0x01000000 and
    /// <see cref="MtkSecCfgLockIntent.Unlock"/> for 0x07F20000 (seccfg.py:233 and :236), otherwise
    /// <see langword="null"/>.
    /// <para>0x01000000 对应 <see cref="MtkSecCfgLockIntent.Lock"/>，0x07F20000 对应
    /// <see cref="MtkSecCfgLockIntent.Unlock"/>（seccfg.py:233 与 :236），其余为 <see langword="null"/>。</para>
    /// </value>
    public MtkSecCfgLockIntent? DocumentedState => EncryptionLength switch
    {
        0x01000000 => MtkSecCfgLockIntent.Lock,
        0x07F20000 => MtkSecCfgLockIntent.Unlock,
        _ => null
    };
}

/// <summary>A secure-boot configuration read from a device partition, bound to its location.
/// <para>从设备分区读取的安全启动配置，并绑定其位置。</para></summary>
/// <param name="Format">The layout selected from the image header.
/// <para>依据镜像头部选择的布局。</para></param>
/// <param name="PartitionId">The discovered physical region the image was read from.
/// <para>读取该镜像所用的已发现物理区域。</para></param>
/// <param name="PartitionName">The exact GPT partition name the image was read from.
/// <para>读取该镜像所用的精确 GPT 分区名。</para></param>
/// <param name="PartitionIndex">The zero-based GPT entry index of that partition.
/// <para>该分区的从零开始 GPT 条目索引。</para></param>
/// <param name="ByteOffset">The region-relative byte offset of the read.
/// <para>本次读取相对于区域起点的字节偏移。</para></param>
/// <param name="ByteLength">The byte length read from the partition.
/// <para>从分区读取的字节长度。</para></param>
/// <param name="ImageSha256Hex">The SHA-256 digest of the exact bytes read.
/// <para>实际读取字节的 SHA-256 摘要。</para></param>
/// <param name="Version4">The decoded version 4 report, or null for a version 3 image.
/// <para>版本 4 的解码结果；版本 3 镜像时为 null。</para></param>
/// <param name="Version3">The decoded version 3 report, or null for a version 4 image.
/// <para>版本 3 的解码结果；版本 4 镜像时为 null。</para></param>
public sealed record MtkSecCfgView(
    MtkSecCfgFormat Format,
    uint PartitionId,
    string PartitionName,
    int PartitionIndex,
    long ByteOffset,
    long ByteLength,
    string ImageSha256Hex,
    MtkSecCfgV4Report? Version4,
    MtkSecCfgV3Report? Version3);

/// <summary>The receipt for a secure-boot configuration image written to a partition.
/// <para>安全启动配置镜像写入某分区后的回执。</para></summary>
/// <remarks>
/// The receipt records what the host submitted and where. It does not claim the target accepted the
/// new lock state semantically: the reference client treats a successful write as an acknowledgement
/// only (Library/DA/xflash/extension/xflash.py:598-601), so verifying the result requires a separate
/// read.
/// <para>回执只记录主机提交了什么以及写到哪里，并不声称目标在语义上接受了新的锁定状态：参考客户端把成功
/// 写入仅当作一次确认（Library/DA/xflash/extension/xflash.py:598-601），因此验证结果需要另行读取。</para>
/// </remarks>
/// <param name="PartitionName">The exact GPT partition name written. 被写入的精确 GPT 分区名。</param>
/// <param name="PartitionIndex">The zero-based GPT entry index written.
/// <para>被写入条目的从零开始 GPT 索引。</para></param>
/// <param name="ByteOffset">The region-relative offset of the write.
/// <para>本次写入相对于区域起点的字节偏移。</para></param>
/// <param name="ByteLength">The byte length written. 写入的字节长度。</param>
/// <param name="Format">The layout the submitted image declares. 提交镜像声明的布局。</param>
/// <param name="ImageSha256Hex">The SHA-256 digest of the exact bytes submitted.
/// <para>提交字节的 SHA-256 摘要。</para></param>
public sealed record MtkSecCfgWriteResult(
    string PartitionName,
    int PartitionIndex,
    long ByteOffset,
    long ByteLength,
    MtkSecCfgFormat Format,
    string ImageSha256Hex);