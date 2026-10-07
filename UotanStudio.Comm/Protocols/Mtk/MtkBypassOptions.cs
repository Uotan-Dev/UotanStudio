using UotanStudio.Comm.Protocols.Mtk.Exploit;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Explicit host-side permission and payload selection for one MediaTek Boot ROM or preloader bypass attempt.
/// <para>单次联发科 Boot ROM 或 preloader 绕过尝试的显式主机侧授权与负载选择。</para>
/// </summary>
/// <remarks>
/// <para>A bypass is never a fallback. The reference client reaches an exploit only after the caller asked for it:
/// its command layer instantiates a driver from the caller-supplied type name
/// (<c>mtkclient/Library/pltools.py</c>, lines 47-57) and its bypass entry point is a distinct command
/// (<c>mtkclient/Library/mtk_class.py</c>, lines 194-213). This record keeps the same discipline: both permission
/// flags default to <see langword="false"/>, no field is inferred from the enumerated device, and a missing or
/// ambiguous selection fails before a byte is written.</para>
/// <para>绕过永远不是回退路径。参考实现只有在调用方主动请求后才会进入漏洞：命令层按调用方给出的类型名实例化驱动
/// （<c>mtkclient/Library/pltools.py</c> 第 47-57 行），绕过入口是一条独立的命令
/// （<c>mtkclient/Library/mtk_class.py</c> 第 194-213 行）。本记录保持同样的纪律：两个授权标志默认都是
/// <see langword="false"/>，任何字段都不按枚举到的设备推断，选择缺失或有歧义时在写出任何字节之前失败。</para>
/// </remarks>
public sealed record MtkBypassOptions
{
    /// <summary>Gets the master switch for the exploit subsystem.
    /// <para>获取漏洞子系统的主开关。</para></summary>
    /// <value>
    /// <see langword="false"/> by default. A session refuses every bypass call while it is
    /// <see langword="false"/>, and nothing in the normal startup path ever sets it.
    /// <para>默认为 <see langword="false"/>。该值为 <see langword="false"/> 时会话拒绝所有绕过调用，正常启动路径也绝不
    /// 设置它。</para>
    /// </value>
    public bool AllowExploitBypass { get; init; }

    /// <summary>Gets whether the caller accepts that the exploit writes words into volatile target memory.
    /// <para>获取调用方是否接受该漏洞向目标易失内存写入字。</para></summary>
    /// <value>
    /// <see langword="false"/> by default. Every documented family primes registers or overwrites a descriptor
    /// before uploading, so the write must be asked for separately from the master switch.
    /// <para>默认为 <see langword="false"/>。所有已记录的家族在上传前都会预热寄存器或覆写描述符，因此写入必须在主开关
    /// 之外单独请求。</para>
    /// </value>
    public bool AllowVolatileMemoryWrite { get; init; }

    /// <summary>Gets whether the Kamakiri driver may brute-force the Boot ROM control index (var1) when the chip
    /// table records none for the queried hardware.
    /// <para>获取当芯片表未为所查询硬件记录 Boot ROM 控制索引（var1）时，Kamakiri 驱动是否可以暴力枚举该索引。</para></summary>
    /// <value>
    /// <see langword="false"/> by default: a chip without a recorded var1 is refused with
    /// <see cref="NotSupportedException"/> before any byte is written. Only the Kamakiri family honours this flag,
    /// because only its reference driver enumerates the bounded candidate range 0..255
    /// (<c>mtkclient/Library/Exploit/kamakiri.py</c>, lines 50-101); every other family still requires its recorded
    /// registers.
    /// <para>默认为 <see langword="false"/>：未记录 var1 的芯片在写出任何字节之前会以 <see cref="NotSupportedException"/>
    /// 被拒绝。只有 Kamakiri 家族理会该标志，因为只有它的参考驱动会枚举有界候选范围 0..255
    /// （<c>mtkclient/Library/Exploit/kamakiri.py</c> 第 50-101 行）；其余家族仍要求各自记录的寄存器。</para>
    /// </value>
    public bool AllowUnknownVar1Brute { get; init; }

    /// <summary>Gets whether the bypass entry may auto-run the documented download-agent crash
    /// sequence when the Boot ROM serial-link authentication blocks the path and no MEID was obtained.
    /// <para>获取当 Boot ROM 串行链路认证阻断路径且未取得 MEID 时，绕过入口是否可以自动运行已记载的
    /// 下载代理 crash 序列。</para></summary>
    /// <value>
    /// <see langword="false"/> by default: no crash frame is ever sent without this opt-in. When it is
    /// set and the queried identity reports serial-link authentication with no MEID, the bypass entry
    /// mirrors the reference <c>crasher</c> (<c>mtkclient/Library/mtk_class.py:169</c>) and submits the
    /// crash exchanges; the target then stops answering and the session faults, so the caller must
    /// reconnect and repeat the bypass.
    /// <para>默认为 <see langword="false"/>：没有该选择时绝不会发送任何 crash 帧。该选择置位且查询到的身份
    /// 报告串行链路认证要求、同时没有 MEID 时，绕过入口会对应参考的 <c>crasher</c>
    /// （<c>mtkclient/Library/mtk_class.py:169</c>）提交 crash 交互；随后目标停止应答且会话进入 fault，
    /// 调用方必须重新连接并重复绕过。</para>
    /// </value>
    public bool EnforceCrash { get; init; }

    /// <summary>Gets the exploit family to run.
    /// <para>获取要运行的漏洞家族。</para></summary>
    /// <value>
    /// <see langword="null"/> by default, which asks the driver to pick the single family whose registers the chip
    /// table documents for the queried hardware code; a chip with more than one candidate is refused rather than
    /// resolved by preference order.
    /// <para>默认为 <see langword="null"/>，表示让驱动挑选芯片表为该硬件码唯一记录齐全的家族；芯片存在多个候选时会被拒
    /// 绝，而不是按优先顺序自行决定。</para>
    /// </value>
    public MtkExploitFamily? Family { get; init; }

    /// <summary>Gets which vendored stage-one payload to upload.
    /// <para>获取要上传的哪一个再分发阶段一负载。</para></summary>
    /// <value>
    /// <see cref="MtkExploitPayloadSource.StageOnePatcher"/> by default, matching the reference bypass entry point;
    /// <see cref="MtkExploitPayloadSource.ChipProfilePayload"/> demands that the chip table name a per-chip blob
    /// and that the blob is vendored, with no silent substitution of the generic one.
    /// <para>默认为 <see cref="MtkExploitPayloadSource.StageOnePatcher"/>，与参考绕过入口一致；
    /// <see cref="MtkExploitPayloadSource.ChipProfilePayload"/> 要求芯片表指名按芯片数据块且该数据块已再分发，不会悄悄改
    /// 用通用负载。</para>
    /// </value>
    public MtkExploitPayloadSource Payload { get; init; } = MtkExploitPayloadSource.StageOnePatcher;

    /// <summary>Gets the host-side ceiling for the payload after padding and any signature suffix.
    /// <para>获取补齐与签名后缀之后的负载主机侧上限。</para></summary>
    /// <value>
    /// A positive multiple of four from 4 through 1048576, defaulting to 8192; the reference drivers send blobs of a
    /// few hundred bytes (<c>mtkclient/payloads/</c>), so the ceiling is a guard against a caller-supplied blob of
    /// the wrong shape rather than a transport limit.
    /// <para>4 至 1048576 之间的正四字节倍数，默认 8192；参考驱动发送的数据块只有几百字节
    /// （<c>mtkclient/payloads/</c>），因此该上限是防止调用方传入形状错误的数据块，而不是传输极限。</para>
    /// </value>
    public int MaximumPayloadLength { get; init; } = 8192;

    /// <summary>Gets the cooperative deadline for each exchange in the bypass sequence.
    /// <para>获取绕过序列中每次交换的协作式截止时间。</para></summary>
    /// <value>Milliseconds from 1 through 86400000, defaulting to 10000.
    /// <para>1 至 86400000 毫秒，默认 10000。</para></value>
    public int TimeoutMs { get; init; } = 10000;

    internal void Validate()
    {
        if (!Enum.IsDefined(Payload))
        {
            throw new ArgumentException("The payload selection is not a defined exploit payload source.");
        }

        if (MaximumPayloadLength is < 4 or > 1048576 || MaximumPayloadLength % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadLength));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TimeoutMs, 86400000);
    }
}