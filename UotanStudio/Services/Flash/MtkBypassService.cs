using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>一次 Boot ROM 绕过尝试所需的显式授权、家族与负载选择；本记录自身不构成任何授权。</para>
/// The explicit permissions, exploit family and payload selection one bypass attempt needs; the record itself
/// grants nothing.
/// </summary>
/// <remarks>
/// <para>Both permission switches default to <see langword="false"/> exactly like the communication layer's own
/// options, so a caller that forgets to set one is refused instead of silently authorising an exploit. No field is
/// inferred from the enumerated device, and no ordinary download-agent startup path ever builds this record.</para>
/// <para>两个授权开关与通信层自身的选项一样默认为 <see langword="false"/>，因此忘记设置的调用方会被拒绝，而不会被
/// 悄悄授权运行漏洞。任何字段都不按枚举到的设备推断，普通下载代理启动路径也绝不构造本记录。</para>
/// </remarks>
internal sealed record MtkBypassRequest
{
    /// <summary>Gets the master switch for the exploit subsystem.
    /// <para>获取漏洞子系统的主开关。</para></summary>
    /// <value><see langword="false"/> by default. <para>默认为 <see langword="false"/>。</para></value>
    public bool AllowExploitBypass { get; init; }

    /// <summary>Gets whether the caller authorises the exploit's unsafe security-control and volatile memory
    /// writes. <para>获取调用方是否授权该漏洞不安全的控制寄存器与易失内存写入。</para></summary>
    /// <value>
    /// <see langword="false"/> by default; it maps to the session's volatile memory write permission, because
    /// every implemented family primes registers or overwrites a descriptor before uploading.
    /// <para>默认为 <see langword="false"/>；它对应会话的易失内存写入许可，因为每个已实现家族在上传前都会预热寄存器
    /// 或覆写描述符。</para>
    /// </value>
    public bool AllowUnsafeVolatileWrite { get; init; }

    /// <summary>Gets the exploit family to run, or <see langword="null"/> to ask for the single family the chip
    /// table documents. <para>获取要运行的漏洞家族，或为 <see langword="null"/> 以请求芯片表唯一记录的家族。</para></summary>
    /// <value>
    /// <see langword="null"/> by default. A chip with more than one selectable family is refused unless the caller
    /// names one, so this never becomes a preference-order decision taken by the host.
    /// <para>默认为 <see langword="null"/>。有多个可选家族的芯片会被拒绝，除非调用方指名，因此这绝不会变成宿主按
    /// 优先顺序做出的决定。</para>
    /// </value>
    public MtkExploitFamily? Family { get; init; }

    /// <summary>Gets which vendored stage-one payload the caller named.
    /// <para>获取调用方指名的再分发阶段一负载。</para></summary>
    /// <value>There is no default; the selection must be written down by the caller.
    /// <para>没有默认值；该选择必须由调用方写明。</para></value>
    public required MtkExploitPayloadSource Payload { get; init; }

    /// <summary>Gets the host-side ceiling for the payload after padding and any signature suffix.
    /// <para>获取补齐与签名后缀之后的负载主机侧上限。</para></summary>
    /// <value>4 through 1048576, a multiple of four, defaulting to 8192.
    /// <para>4 至 1048576 之间的四字节倍数，默认 8192。</para></value>
    public int MaximumPayloadLength { get; init; } = 8192;

    /// <summary>Gets the cooperative deadline for each exchange in the bypass sequence.
    /// <para>获取绕过序列中每次交换的协作式截止时间。</para></summary>
    /// <value>1 through 86400000 milliseconds, defaulting to 10000.
    /// <para>1 至 86400000 毫秒，默认 10000。</para></value>
    public int TimeoutMs { get; init; } = 10000;
}

/// <summary>
/// <para>一次已授权绕过的宿主页报告：会话上报的安全状态、所用负载的身份，以及是否可以继续显式下载代理上传。</para>
/// The host-side report of one authorised bypass: the security state the session reported, the identity of the
/// payload that was used, and whether an explicit download-agent upload may continue on the same session.
/// </summary>
/// <remarks>
/// <para>The report carries no payload bytes, no fuse values and no key material. <see cref="PayloadIdentity"/>
/// is the reference file name and <see cref="PayloadSha256"/> its digest, so an operator can confirm which
/// vendored blob reached the target without the host copying the blob anywhere.</para>
/// <para>本报告不携带任何负载字节、熔丝值或密钥材料。<see cref="PayloadIdentity"/> 是参考文件名，
/// <see cref="PayloadSha256"/> 是其摘要，因此操作者可以确认哪个再分发数据块到达了目标，而宿主不会把这些字节复制到
/// 任何其他地方。</para>
/// </remarks>
/// <param name="Result"><para>会话上报的绕过结果。</para>The bypass outcome the session reported.</param>
/// <param name="PayloadIdentity"><para>所用负载的文件名身份；家族不上传负载时为 null。</para>
/// The file name identity of the payload that was used, or null when the family uploads no payload.</param>
/// <param name="PayloadSha256"><para>该再分发负载的 SHA-256 十六进制文本；无负载时为 null。</para>
/// The hexadecimal SHA-256 of that vendored payload, or null when no payload was uploaded.</param>
/// <param name="DaUploadHandoffReady"><para>会话是否处于可由调用方显式继续下载代理上传的阶段。</para>
/// Whether the session is in the stage where the caller may continue with an explicit download-agent upload.</param>
/// <param name="SessionState"><para>绕过结束后会话所处的阶段。</para>The session stage after the bypass finished.</param>
internal sealed record MtkBypassHandoff(MtkBypassResult Result, string? PayloadIdentity, string? PayloadSha256,
    bool DaUploadHandoffReady, MtkSessionState SessionState)
{
    /// <summary>Gets a single-language summary of what the bypass observed.
    /// <para>获取本次绕过所观测到内容的单语言摘要。</para></summary>
    /// <value>
    /// The family, chip, payload identity, the reported security word transition and the handoff decision. It
    /// never claims that a physical device was tested.
    /// <para>家族、芯片、负载身份、上报的安全字变化与交接决定。它从不声称已测试过物理设备。</para>
    /// </value>
    public string Summary
    {
        get
        {
            var payload = PayloadIdentity is { } name
                ? $"payload {name} ({Result.PayloadLength} byte(s), SHA-256 {PayloadSha256})"
                : "no payload uploaded";
            return $"{Result.Family} on {Result.ChipName} (0x{Result.HardwareCode:X4}) at " +
                $"0x{Result.LandingAddress:X8} used {payload}; target configuration " +
                $"0x{Result.TargetConfigurationBefore:X8} to 0x{Result.TargetConfigurationAfter:X8}, " +
                $"signature checks open: {Result.SignatureChecksOpen}; download-agent upload handoff " +
                $"{(DaUploadHandoffReady ? "ready" : "not ready")} in stage {SessionState}.";
        }
    }
}

/// <summary>
/// <para>宿主层的联发科 Boot ROM 绕过入口：只在调用方同时给出两项显式授权时才驱动会话的绕过流程，并把成功后的
/// 会话交回调用方去显式上传下载代理。</para>
/// Host-side entry point for the MediaTek Boot ROM bypass: it drives the session bypass only when the caller
/// supplies both explicit permissions, and hands the post-bypass session back so the caller can upload a
/// download agent explicitly.
/// </summary>
/// <remarks>
/// <para>The reference client reaches an exploit only through a distinct command whose driver type name the caller
/// wrote down (<c>mtkclient/Library/mtk_class.py</c>, lines 194-213 with
/// <c>mtkclient/Library/pltools.py</c>, lines 47-57), so this service keeps the same discipline: it never falls
/// back from an ordinary download-agent startup, never infers a family or payload from the enumerated device, and
/// performs its permission and family checks before the boot identity is even queried. The communication layer
/// repeats the permission checks after the identity query, so an unusable selection is refused there as well; the
/// host gate exists only to keep a refused request from touching the endpoint at all.</para>
/// <para>参考客户端只能通过一条独立命令进入漏洞，且驱动类型名由调用方写明（<c>mtkclient/Library/mtk_class.py</c>
/// 第 194-213 行，配合 <c>mtkclient/Library/pltools.py</c> 第 47-57 行），因此本服务保持同样纪律：绝不从普通下载代理
/// 启动回退，绝不按枚举到的设备推断家族或负载，并在查询启动身份之前就完成授权与家族检查。通信层会在身份查询之后
/// 重复这些授权检查，因此不可用的选择在彼处同样被拒绝；宿主这道门只是为了让被拒绝的请求完全不接触端点。</para>
/// <para>Chip, family and payload resolvability are deliberately left to the session, which owns the reference
/// chip table rules: an undocumented hardware code, a chip whose entry documents several selectable families and
/// a per-chip payload that is not vendored each surface as the session's own <see cref="NotSupportedException"/>
/// and are re-thrown here unchanged after the identity query but before any exploit byte leaves the host.</para>
/// <para>芯片、家族与负载能否解析刻意留给会话决定，因为参考芯片表规则由其持有：未记录的硬件码、条目记录了多个可选
/// 家族的芯片、以及未再分发的按芯片负载，都会以会话自身的 <see cref="NotSupportedException"/> 出现，并在身份查询
/// 之后、任何漏洞字节离开主机之前原样重新抛出。</para>
/// </remarks>
internal static class MtkBypassService
{
    /// <summary>Runs one authorised bypass on a session the device lease already owns.
    /// <para>在设备租约已持有的会话上运行一次已授权的绕过。</para></summary>
    /// <param name="session">A leased MTK session; the caller keeps owning it afterwards.
    /// <para>已租用的联发科会话；之后仍由调用方持有。</para></param>
    /// <param name="request">The caller's explicit permissions and payload selection.
    /// <para>调用方显式的授权与负载选择。</para></param>
    /// <param name="progress">Optional phase progress; the label stays null because a bypass is not a partition
    /// operation. <para>可选的阶段进度；标签保持 null，因为绕过不是分区操作。</para></param>
    /// <param name="cancellationToken">Cancellation for the identity query, the exploit sequence and the
    /// post-exploit re-handshake. <para>身份查询、漏洞序列与绕过后再握手的取消令牌。</para></param>
    /// <returns>The reported security state, the payload identity and the download-agent handoff flag.
    /// <para>上报的安全状态、负载身份与下载代理交接标记。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="request"/> is null.
    /// <para><paramref name="session"/> 或 <paramref name="request"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">Either permission switch is off; no byte is written and the
    /// boot identity is not even queried. <para>任一授权开关未打开；不写出任何字节，也不查询启动身份。</para></exception>
    /// <exception cref="NotSupportedException">The named family is one this driver does not implement, or the
    /// session refuses an unknown chip, a chip entry that lacks a register the selected family reads, an ambiguous
    /// family selection or an unvendored payload.
    /// <para>指名的家族是本驱动未实现的家族，或会话因未知芯片、条目缺少所选家族读取的寄存器、有歧义的家族选择、未再分发的
    /// 负载而拒绝。</para>
    /// </exception>
    internal static async Task<MtkBypassHandoff> RunAsync(MtkSession session, MtkBypassRequest request,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        RequirePermissions(request);
        RequireImplementedFamily(request.Family);
        progress?.Report(new FlashProgress(null, 5,
            "Querying the boot identity of the selected MediaTek endpoint."));
        await EnsureBootIdentityAsync(session, cancellationToken).ConfigureAwait(false);
        var options = new MtkBypassOptions
        {
            AllowExploitBypass = request.AllowExploitBypass,
            AllowVolatileMemoryWrite = request.AllowUnsafeVolatileWrite,
            Family = request.Family,
            Payload = request.Payload,
            MaximumPayloadLength = request.MaximumPayloadLength,
            TimeoutMs = request.TimeoutMs,
        };
        progress?.Report(new FlashProgress(null, 40,
            $"Running the {DescribeFamily(request)} bypass family on the queried chip."));
        var result = await session.RunBypassAsync(options, cancellationToken).ConfigureAwait(false);
        var identity = ResolvePayloadIdentity(result, request);
        var handoffReady = session.State == MtkSessionState.Bootloader && session.DeviceInfo is not null;
        progress?.Report(new FlashProgress(null, 100, handoffReady
            ? "Bypass sequence finished; the session awaits an explicit download-agent upload."
            : "Bypass sequence finished, but the session left the bootloader stage and must be reconnected."));
        return new MtkBypassHandoff(result, identity.Identity, identity.Sha256, handoffReady, session.State);
    }

    /// <summary>Runs one authorised bypass on an enumerated MTK endpoint under the device monitor's lease.
    /// <para>在设备监视器的租约下，为已枚举出的联发科端点运行一次已授权的绕过。</para></summary>
    /// <param name="endpoint">A discovered MTK endpoint. <para>已发现的联发科端点。</para></param>
    /// <param name="request">The caller's explicit permissions and payload selection.
    /// <para>调用方显式的授权与负载选择。</para></param>
    /// <param name="progress">Optional phase progress. <para>可选的阶段进度。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole transition.
    /// <para>整个转换过程的取消令牌。</para></param>
    /// <returns>The reported security state, the payload identity and the handoff flag.
    /// <para>上报的安全状态、负载身份与交接标记。</para></returns>
    /// <remarks>
    /// The permission and family refusals happen before the lease is taken, so a refused request never opens the
    /// endpoint. <para>授权与家族的拒绝发生在取得租约之前，因此被拒绝的请求绝不会打开该端点。</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Either permission switch is off.
    /// <para>任一授权开关未打开。</para></exception>
    /// <exception cref="NotSupportedException">The requested family is unimplemented.
    /// <para>请求的家族未实现。</para></exception>
    internal static async Task<MtkBypassHandoff> RunOnEndpointAsync(DetectedDevice endpoint,
        MtkBypassRequest request, IProgress<FlashProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        RequirePermissions(request);
        RequireImplementedFamily(request.Family);
        var handoff = await App.DeviceMonitor.RunMtkAsync(endpoint,
            session => RunAsync(session, request, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        return handoff;
    }

    /// <summary>Refuses a request whose explicit permissions are incomplete before any device access.
    /// <para>在任何设备访问之前，拒绝显式授权不完整的请求。</para></summary>
    /// <param name="request">The caller's request. <para>调用方的请求。</para></param>
    /// <exception cref="InvalidOperationException">A permission switch is off. <para>某个授权开关未打开。</para></exception>
    private static void RequirePermissions(MtkBypassRequest request)
    {
        if (!request.AllowExploitBypass)
        {
            throw new InvalidOperationException(
                "The MediaTek exploit bypass subsystem stays closed until the caller grants it explicitly.");
        }

        if (!request.AllowUnsafeVolatileWrite)
        {
            throw new InvalidOperationException(
                "Every implemented MediaTek bypass family primes security-control registers before uploading, so the unsafe volatile write must be granted separately.");
        }
    }

    /// <summary>Refuses a family the exploit runner reports as unimplemented, deferring to that single source of
    /// truth instead of keeping a second family list here.
    /// <para>拒绝漏洞运行器报告为未实现的家族，交由该唯一来源判定，而不是在此维护第二份家族清单。</para></summary>
    /// <param name="family">The caller's family selection, or <see langword="null"/>.
    /// <para>调用方的家族选择，可为 <see langword="null"/>。</para></param>
    /// <exception cref="NotSupportedException">The named family is one <see cref="MtkExploitRunner.IsImplemented"/>
    /// reports as unimplemented.
    /// <para>指名的家族是 <see cref="MtkExploitRunner.IsImplemented"/> 报告为未实现的家族。</para></exception>
    /// <remarks>
    /// <para>The hardware-crypto engines the Amonet and Hashimoto payload paths need now exist in
    /// <see cref="UotanStudio.Comm.Protocols.Mtk.HwCrypto"/>, so <see cref="MtkExploitRunner.IsImplemented"/>
    /// reports every documented family as implemented and this host gate no longer refuses Amonet or Hashimoto. It
    /// defers entirely to the runner so a future family stays refused in exactly one place rather than a duplicated
    /// list. A chip entry still lacking a register the family reads is refused later, by the session itself, after
    /// the boot identity is queried.</para>
    /// <para>Amonet 与 Hashimoto 负载路径所需的硬件加密引擎现在已存在于
    /// <see cref="UotanStudio.Comm.Protocols.Mtk.HwCrypto"/>，因此 <see cref="MtkExploitRunner.IsImplemented"/>
    /// 把每个已记录家族都报告为已实现，本宿主门不再拒绝 Amonet 或 Hashimoto。它完全交由运行器判定，使未来新增的家族只在这一处
    /// 被拒绝，而不是依赖重复清单。芯片条目若仍缺少该家族读取的寄存器，会在查询启动身份之后由会话自身拒绝。</para>
    /// </remarks>
    private static void RequireImplementedFamily(MtkExploitFamily? family)
    {
        if (family is { } named && !MtkExploitRunner.IsImplemented(named))
        {
            throw new NotSupportedException(
                $"The {named} exploit family is documented by the reference client but this driver has no sequence for it, so it is refused instead of substituting another family.");
        }
    }

    /// <summary>Queries the boot identity when the caller handed over a session that has not reported one yet.
    /// <para>调用方交来的会话尚未上报身份时，查询启动身份。</para></summary>
    /// <param name="session">The leased session. <para>已租用的会话。</para></param>
    /// <param name="cancellationToken">Cancellation for the handshake. <para>握手的取消令牌。</para></param>
    /// <remarks>
    /// <para>The bypass entry point needs the hardware code the boot handshake reports, so the identity query is
    /// part of the requested transition rather than a fallback: a caller who asked for a bypass asked for this
    /// query. Every other stage stays untouched, which means an already running download agent is never quietly
    /// re-handshaked.</para>
    /// <para>绕过入口需要启动握手上报的硬件码，因此身份查询属于所请求转换的一部分而不是回退：请求绕过的调用方就是
    /// 请求了这次查询。其他阶段保持不变，意味着已在运行的下载代理绝不会被悄悄重新握手。</para>
    /// </remarks>
    private static async Task EnsureBootIdentityAsync(MtkSession session, CancellationToken cancellationToken)
    {
        if (session.State == MtkSessionState.Created)
        {
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Names the family the request will let the session choose.
    /// <para>命名请求将会让会话选择的家族。</para></summary>
    /// <param name="request">The caller's request. <para>调用方的请求。</para></param>
    /// <returns>The named family, or the text describing a chip-selected family.
    /// <para>指名的家族；若由芯片挑选则返回相应描述文本。</para></returns>
    private static string DescribeFamily(MtkBypassRequest request) =>
        request.Family?.ToString() ?? "the single chip-table documented";

    /// <summary>Resolves which vendored blob the reported run actually used.
    /// <para>解析本次已上报的运行实际使用了哪个再分发数据块。</para></summary>
    /// <param name="result">The outcome the session reported. <para>会话上报的结果。</para></param>
    /// <param name="request">The caller's request, which named the payload source.
    /// <para>命名了负载来源的调用方请求。</para></param>
    /// <returns>The file name and digest, or two nulls when the family uploaded no payload.
    /// <para>文件名与摘要；家族未上传负载时两者为 null。</para></returns>
    private static (string? Identity, string? Sha256) ResolvePayloadIdentity(MtkBypassResult result,
        MtkBypassRequest request)
    {
        if (result.PayloadLength == 0)
        {
            return (null, null);
        }

        var name = ResolvePayloadName(result, request);
        var bytes = request.Payload == MtkExploitPayloadSource.ChipProfilePayload
            ? MtkExploitPayloadCatalog.GetChipPayload(name)
            : MtkExploitPayloadCatalog.GetGenericPayload(request.Payload);
        return bytes is { } vendored
            ? (name, Convert.ToHexString(SHA256.HashData(vendored.ToArray())))
            : (name, null);
    }

    /// <summary>Picks the reference file name behind the caller's payload selection.
    /// <para>挑选调用方负载选择背后的参考文件名。</para></summary>
    /// <param name="result">The outcome the session reported, which carries the hardware code.
    /// <para>会话上报的结果，其中带有硬件码。</para></param>
    /// <param name="request">The caller's request. <para>调用方的请求。</para></param>
    /// <returns>The file name recorded for the payload that ran. <para>为实际运行的负载记录的文件名。</para></returns>
    private static string ResolvePayloadName(MtkBypassResult result, MtkBypassRequest request)
    {
        if (request.Payload != MtkExploitPayloadSource.ChipProfilePayload)
        {
            return MtkExploitPayloadCatalog.GetGenericFileName(request.Payload) ?? "an unnamed generic payload";
        }

        return MtkBromChipTable.Find(result.HardwareCode)?.ChipPayloadName ?? "an unnamed per-chip payload";
    }
}