using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Compress;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Executes a SpacemiT flash package against a fastboot session, replicating the vendor
/// flasher's flow: optional BootROM FSBL/U-Boot staging, super-speed switch, partition
/// table write (<c>flash:gpt</c>/<c>flash:mtd</c>) and per-partition image flashing with
/// gzip-chunked oversized images; an oversized image that is itself gzip is decompressed
/// first so the chunked stream carries the same content a single-shot transfer would write.
/// When <see cref="SpacemiTFlashOptions.VerifyAfterFlash"/> is set, each non-sparse partition
/// is additionally read back and compared against the source image by SHA-256, except <c>env</c>
/// (re-persisted by U-Boot, per the vendor verify_board script) and hidden block-device partitions
/// (no device GPT entry to read back).
/// <para>对 fastboot 会话执行 SpacemiT 烧录包，复刻厂商烧录器流程：可选的 BootROM
/// FSBL/U-Boot 暂存拉起、super speed 切换、分区表写入（<c>flash:gpt</c>/<c>flash:mtd</c>）
/// 与逐分区镜像刷写（超大镜像按 gzip 分块传输）；本身即为 gzip 的超大镜像会先解压，
/// 使分块流承载的内容与单次传输写入的内容一致。设置 <see cref="SpacemiTFlashOptions.VerifyAfterFlash"/>
/// 时，每个非 sparse 分区会额外回读并与源镜像做 SHA-256 比对，<c>env</c>（U-Boot 会重存，
/// 厂商 verify_board 脚本同款跳过）与块设备上的 hidden 分区（设备 GPT 无条目可回读）除外。</para>
/// </summary>
/// <remarks>
/// Device contract (verified against SpacemiT U-Boot <c>fb_spacemit.c</c>/<c>fb_mmc.c</c>/<c>fb_blk.c</c>):
/// a download payload starting with the gzip magic is transparently decompressed, and
/// consecutive <c>flash:&lt;same partition&gt;</c> calls with gzip payloads append at the
/// running decompressed offset until the partition name changes or a non-gzip payload
/// arrives; non-gzip sparse payloads are written by the on-device sparse writer; raw
/// payloads always write at the partition start. The device floors the running decompressed
/// append offset to the storage block size and rounds each raw write up to it, so every
/// gzip chunk must decompress to a multiple of that block size (512 bytes on eMMC/SD) and
/// each chunked sequence must start with one raw block write to zero the append offset.
/// <para>设备端契约（已对照 SpacemiT U-Boot <c>fb_spacemit.c</c>/<c>fb_mmc.c</c>/<c>fb_blk.c</c> 验证）：
/// 以 gzip 魔数开头的下载负载会被透明解压；对同一分区连续多次携带 gzip 负载的
/// <c>flash:&lt;分区&gt;</c> 调用会在设备端按解压后的运行偏移追加写入，直到分区名变化或
/// 出现非 gzip 负载；非 gzip 的 sparse 负载由设备端 sparse 写入器处理；raw 负载始终从
/// 分区起始处写入。设备端会把解压后的追加偏移向下取整到存储块大小，并把每次 raw 写入
/// 向上取整到块边界，因此每个 gzip 分块必须解压为该块大小（eMMC/SD 上为 512 字节）的
/// 倍数，且每个分块序列必须以一次 raw 块写入开头以清零追加偏移。</para>
/// <para>Host strategy per the decompiled vendor flasher (<c>goflash.exe</c> <c>flashWithGzip</c>/
/// <c>compressReader</c>/<c>FlashFile</c>/<c>initTransport</c>): the vendor chunks raw images
/// at the device max-download-size verbatim with no expansion budget and no explicit block
/// alignment, compresses every file on non-super-speed USB regardless of size at level 2,
/// skips compression on USB3 super-speed, and sends whatever a member compresses to —
/// relying on two empirical assumptions (max-download-size being block-aligned and images
/// being compressible) whose violation corrupts the image or fails the transfer. This
/// implementation deliberately strengthens that flow: the chunk budget reserves the gzip
/// worst-case expansion and floors to the block size, the final chunk is zero-padded, the
/// append offset is reset before each chunked sequence, an oversized gzip source is
/// decompressed before chunking, and every compressed member is checked against the budget.</para>
/// <para>宿主策略对照反编译的厂商烧录器（<c>goflash.exe</c> 的 <c>flashWithGzip</c>/
/// <c>compressReader</c>/<c>FlashFile</c>/<c>initTransport</c>）：厂商直接以设备
/// max-download-size 作为原始分块尺寸，无膨胀预算、无显式块对齐，非 super-speed USB 下
/// 不论大小一律 level 2 压缩，USB3 super-speed 跳过压缩，压缩成员超出预算时不做检查——
/// 依赖两个经验假设（max-download-size 天然块对齐、镜像可压缩），一旦违反即损坏镜像或
/// 传输失败。本实现有意加强该流程：分块预算预留 gzip 最坏膨胀并向下取整到块大小，末块
/// 补零，分块序列前复位追加偏移，超大 gzip 源先解压再分块，且每个压缩成员都校验不超预算。</para>
/// </remarks>
public sealed class SpacemiTFlasher
{
    private const int SparseHeaderBytes = 4;

    private const int GzipHeaderBytes = 4;

    /// <summary>Storage block size the device floors the chunked-gzip append offset to and rounds
    /// raw writes up to; 512 bytes on eMMC/SD where the vendor gzip chunking applies.
    /// <para>设备端对 gzip 分块追加偏移向下取整、对 raw 写入向上取整所用的存储块大小；
    /// 厂商 gzip 分块适用的 eMMC/SD 上为 512 字节。</para></summary>
    private const int FlashBlockSizeBytes = 512;

    /// <summary>Extra headroom (beyond the gzip worst-case expansion bound) reserved per raw gzip
    /// chunk to absorb any gzip-implementation overhead the conservative bound does not predict.
    /// <para>每个 raw gzip 分块在 gzip 最坏膨胀上限之外额外预留的余量，吸收保守上限未能预测的
    /// 任何 gzip 实现开销。</para></summary>
    private const long GzipChunkSafetyMargin = 4 * 1024;

    /// <summary>Upper bound for a single raw gzip chunk, so a device reporting a huge
    /// max-download-size cannot force an equally huge host allocation.
    /// <para>单个 raw gzip 分块的上限，避免设备上报的巨大 max-download-size 逼出同样巨大的
    /// 宿主端分配。</para></summary>
    private const long MaxGzipChunkBytes = 0x10000000;

    private readonly SpacemiTFlashPackage _package;
    private readonly SpacemiTFlashOptions _options;
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    /// <summary>SHA-256 over the verified bytes of each source image, cached per target so a retried or repeated verify does not re-hash the file. <para>每个源镜像被校验字节的 SHA-256，按目标缓存，使重试或重复的校验不必重新哈希文件。</para></summary>
    private readonly Dictionary<string, byte[]> _sourceDigests = new(StringComparer.Ordinal);

    /// <summary>
    /// Targets whose content the host cannot meaningfully verify by read-back even though the partition is readable:
    /// <c>env</c> is re-persisted by U-Boot with runtime state, so partition bytes never match the staged file — the
    /// vendor <c>verify_board</c> script skips exactly this one. A block-device partition the tables mark hidden is
    /// skipped by the per-target rule in <see cref="FlashPartition"/> instead: it has no device GPT entry, so its
    /// read-back probe answers "no such partition" (and the device rebuilds <c>bootinfo</c>/<c>fsbl</c> boot-area
    /// content itself), while on an MTD table every partition including <c>bootinfo</c> is verified, matching the
    /// vendor NOR flow.
    /// <para>分区可读、但宿主无法通过回读有效校验内容的目标：<c>env</c> 会被 U-Boot 以运行时状态重存，分区字节
    /// 永远不会与暂存文件一致——厂商 <c>verify_board</c> 脚本恰好只跳过它。布局表标记为 hidden 的块设备分区改由
    /// <see cref="FlashPartition"/> 的逐目标规则跳过：它在设备 GPT 中没有条目，回读探测会应答 "no such
    /// partition"（且 <c>bootinfo</c>/<c>fsbl</c> 的 boot 区内容由设备端自行重建）；而 MTD 表上包括
    /// <c>bootinfo</c> 在内的每个分区都会被校验，与厂商 NOR 流程一致。</para>
    /// </summary>
    private static readonly HashSet<string> VerifySkippedTargets = new(StringComparer.Ordinal) { "env" };

    private const string VariableBagTempPrefix = "temp.";

    /// <summary>
    /// Creates a flasher for the given package and options.
    /// <para>为给定烧录包与选项创建烧录器。</para>
    /// </summary>
    /// <param name="package">Loaded flash package. <para>已加载的烧录包。</para></param>
    /// <param name="options">Flash options; the confirmation phrase is validated before any I/O. <para>烧录选项；确认短语在任何 I/O 之前校验。</para></param>
    public SpacemiTFlasher(SpacemiTFlashPackage package, SpacemiTFlashOptions options)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Runs the package script, or only the section <see cref="SpacemiTFlashOptions.Scope"/> selects: the boot-chain
    /// section ends at the last <c>continue</c>, and the storage section never attempts a <c>stage</c> or <c>continue</c>.
    /// The supplied re-open handler is invoked whenever the device re-enumerates (after <c>continue</c> or an accepted
    /// super-speed switch): it must dispose the stale session and return a session for the re-enumerated device, or
    /// <see langword="null"/> when the device never re-appeared.
    /// <para>执行包脚本，或只执行 <see cref="SpacemiTFlashOptions.Scope"/> 选定的段落：引导链段落止于最后一个
    /// <c>continue</c>，存储段落绝不尝试 <c>stage</c> 或 <c>continue</c>。设备重新枚举时（<c>continue</c> 或被接受的
    /// super speed 切换之后）调用传入的重开处理器：它必须释放过期会话并返回重新枚举设备的新会话；设备未重新出现时
    /// 返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="initialSession">Session for the device in its current mode (BootROM FDL or fastboot). <para>当前模式下（BootROM FDL 或 fastboot）设备的会话。</para></param>
    /// <param name="reopenSession">Handler replacing a stale session after re-enumeration. <para>重新枚举后替换过期会话的处理器。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels between actions and between transfer chunks. <para>在动作之间与传输分块之间取消。</para></param>
    /// <exception cref="InvalidOperationException">The options are not confirmed. <para>选项未确认。</para></exception>
    /// <exception cref="FastbootException">An action failed after all retries and was not marked <c>skip_fail</c>. <para>某动作在重试耗尽后失败且未标记 <c>skip_fail</c>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public void Execute(
        FastbootSession initialSession,
        Func<FastbootSession, FastbootSession?> reopenSession,
        IProgress<SpacemiTFlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialSession);
        ArgumentNullException.ThrowIfNull(reopenSession);
        _options.Validate();

        var session = initialSession;
        var actions = _package.Script.Actions;
        var bootChainEnd = _package.Script.BootChainEnd;
        if (_options.Scope == SpacemiTFlashScope.BootChain && bootChainEnd == 0)
        {
            Report(progress, "skip", null, "the package script declares no boot-chain section (no continue action)");
            Report(progress, "done", null, "boot chain finished");
            return;
        }

        if (_options.Scope == SpacemiTFlashScope.Storage &&
            SpacemiTBootStageClassifier.Probe(session) == SpacemiTBootStage.BromFdl)
        {
            throw new FastbootException(FastbootResponseState.Fail,
                "the device is still in BootROM FDL, which cannot write partitions: send the boot chain first, " +
                "then flash the selected partitions");
        }

        for (var index = 0; index < actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = actions[index];
            if (!RunsInScope(action, index, bootChainEnd))
            {
                continue;
            }
            if (SpacemiTConditionEvaluator.ShouldSkip(action.SkipWhen, _variables))
            {
                Report(progress, "skip", null, $"action #{index + 1} skipped by skip_when '{action.SkipWhen}'");
                continue;
            }

            switch (action)
            {
                case SpacemiTGetVarAction getVar:
                    session = ExecuteGetVar(session, reopenSession, getVar, progress, cancellationToken);
                    break;
                case SpacemiTStageAction stage:
                    ExecuteStage(session, stage, progress, cancellationToken);
                    break;
                case SpacemiTContinueAction continueAction:
                    session = ExecuteContinue(session, reopenSession, continueAction, progress, cancellationToken);
                    break;
                case SpacemiTOemAction oem:
                    session = ExecuteOem(session, reopenSession, oem, progress, cancellationToken);
                    break;
                case SpacemiTMultiFlashAction multiFlash:
                    ExecuteMultiFlash(session, multiFlash, progress, cancellationToken);
                    break;
                case SpacemiTFlashPartitionAction flash:
                    ExecuteDirectFlash(session, flash, progress, cancellationToken);
                    break;
                case SpacemiTEraseAction erase:
                    ExecuteErase(session, erase, progress, cancellationToken);
                    break;
                case SpacemiTRebootAction reboot:
                    ExecuteReboot(session, reboot, progress, cancellationToken);
                    break;
            }
        }

        if (_options.RebootAfterFlash && _options.Scope != SpacemiTFlashScope.BootChain)
        {
            RunWithRetry("reboot", 1, skipFail: false, progress, null,
                () =>
                {
                    session.Reboot().ThrowIfFailed();
                    return true;
                });
        }

        Report(progress, "done", null, _options.Scope == SpacemiTFlashScope.BootChain
            ? "boot chain handed over"
            : "flash script finished");
    }

    /// <summary>
    /// Whether an action belongs to the requested scope: the boot-chain scope stops at the last <c>continue</c>, and the
    /// storage scope never attempts a <c>stage</c> or <c>continue</c>, whose only purpose is to hand the board over to the
    /// U-Boot gadget already running.
    /// <para>判断动作是否属于所请求的范围：引导链范围在最后一个 <c>continue</c> 处结束；存储范围绝不尝试 <c>stage</c> 或
    /// <c>continue</c>，因为它们唯一的作用是把板卡交给已经在运行的 U-Boot gadget。</para>
    /// </summary>
    /// <param name="action">The action under consideration. <para>待判断的动作。</para></param>
    /// <param name="index">Its position in the script. <para>它在脚本中的位置。</para></param>
    /// <param name="bootChainEnd">Exclusive end index of the boot-chain section. <para>引导链段落的排他结束下标。</para></param>
    /// <returns><see langword="true"/> when the action runs in the current scope.
    /// <para>该动作在当前范围内执行时为 <see langword="true"/>。</para></returns>
    private bool RunsInScope(SpacemiTFlashAction action, int index, int bootChainEnd) => _options.Scope switch
    {
        SpacemiTFlashScope.BootChain => index < bootChainEnd,
        SpacemiTFlashScope.Storage => action is not (SpacemiTStageAction or SpacemiTContinueAction),
        _ => true
    };

    /// <remarks>
    /// Each retry re-establishes the device connection, mirroring the vendor flasher which
    /// re-spawns a fastboot process per attempt: a getvar that is waiting for the gadget to
    /// come up after a re-enumeration must not retry forever on a stale handle.
    /// <para>每次重试都会重建设备连接，对应厂商烧录器每次尝试重新拉起一个 fastboot 进程的做法：
    /// 在重新枚举后等待 gadget 就绪的 getvar 不能在过期句柄上无限重试。</para>
    /// </remarks>
    private FastbootSession ExecuteGetVar(
        FastbootSession session,
        Func<FastbootSession, FastbootSession?> reopenSession,
        SpacemiTGetVarAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? value = null;
        var attemptNumber = 0;
        var ok = RunWithRetry($"getvar {action.Variable}", action.Retry, action.SkipFail, progress, action.Variable, () =>
        {
            attemptNumber++;
            if (attemptNumber > 1)
            {
                var fresh = reopenSession(session);
                if (fresh is not null)
                {
                    session = fresh;
                }
            }

            var response = SendWithTimeout(session, FastbootConstants.CommandGetVar + action.Variable, action.Timeout, cancellationToken);
            if (response.State != FastbootResponseState.Success)
            {
                throw new FastbootException(response.State, $"getvar {action.Variable} failed: {response.Text}");
            }

            value = response.Info.Trim();
            return true;
        });

        if (ok && action.BagKey is { } key && value is not null)
        {
            _variables[key] = value;
            var alias = key.StartsWith(VariableBagTempPrefix, StringComparison.Ordinal)
                ? key[VariableBagTempPrefix.Length..]
                : VariableBagTempPrefix + key;
            if (alias.Length > 0)
            {
                _variables[alias] = value;
            }

            Report(progress, "action", action.Variable, $"getvar {action.Variable} = '{value}'");
        }

        return session;
    }

    private void ExecuteStage(
        FastbootSession session,
        SpacemiTStageAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var file = RequireFile(action.File, action.File);
        RunWithRetry($"stage {action.File}", action.Retry, action.SkipFail, progress, null, () =>
        {
            using var stream = File.OpenRead(file);
            Report(progress, "stage", null, $"staging {action.File} ({stream.Length} bytes)");
            session.DownloadData(stream, stream.Length, CreateByteProgress(progress, "stage", null, stream.Length), action.Timeout, cancellationToken)
                .ThrowIfFailed();
            return true;
        });
    }

    /// <remarks>
    /// <c>continue</c> makes the device leave fastboot and drop the USB link, so it may never answer
    /// OKAY and can even reset the transport; a missing or failed response is the expected outcome
    /// and is treated as success rather than aborting the run.
    /// <para><c>continue</c> 会使设备离开 fastboot 并断开 USB 链路，因此设备可能永远不会应答 OKAY，
    /// 甚至可能复位传输；应答缺失或失败是预期结果，按成功处理而不是中止整个流程。</para>
    /// </remarks>
    private FastbootSession ExecuteContinue(
        FastbootSession session,
        Func<FastbootSession, FastbootSession?> reopenSession,
        SpacemiTContinueAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "action", null, "continue: device leaves fastboot, waiting for re-enumeration");
        try
        {
            SendWithTimeout(session, FastbootConstants.CommandContinue, action.Timeout, cancellationToken);
        }
        catch (IOException e)
        {
            Report(progress, "action", null, $"continue: transport dropped ({e.Message}); device left fastboot as expected");
        }

        return ReopenAfterReEnumeration(session, reopenSession, progress, cancellationToken, allowSkip: false);
    }

    /// <remarks>
    /// The super-speed switch makes the device drop the current link and re-enumerate at USB3;
    /// the OKAY may never be seen, exactly like <c>continue</c>. A non-OKAY answer is treated as
    /// expected and the re-enumerated device is always awaited, so the next action does not run on
    /// a dead handle. Other OEM commands keep their strict success check.
    /// <para>super speed 切换会使设备断开当前链路并以 USB3 重新枚举；与 <c>continue</c> 一样可能
    /// 永远看不到 OKAY。非 OKAY 应答按预期处理，并始终等待重新枚举出的设备，使下一个动作不会跑在
    /// 失效句柄上。其他 OEM 命令保持严格的成功校验。</para>
    /// </remarks>
    private FastbootSession ExecuteOem(
        FastbootSession session,
        Func<FastbootSession, FastbootSession?> reopenSession,
        SpacemiTOemAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var isSuperSpeed = action.Args.Equals(SpacemiTConstants.OemSuperSpeed, StringComparison.Ordinal);

        var ok = RunWithRetry($"oem {action.Args}", action.Retry, action.SkipFail, progress, null, () =>
        {
            var response = SendWithTimeout(session, FastbootConstants.CommandOem + action.Args, action.Timeout, cancellationToken);
            if (response.State != FastbootResponseState.Success && !isSuperSpeed)
            {
                throw new FastbootException(response.State, $"oem {action.Args} failed: {response.Text}");
            }

            return true;
        });

        if (isSuperSpeed && (ok || action.SkipFail))
        {
            Report(progress, "action", null, "super-speed requested, waiting for re-enumeration");
            return ReopenAfterReEnumeration(session, reopenSession, progress, cancellationToken, allowSkip: action.SkipFail);
        }

        return session;
    }

    private FastbootSession ReopenAfterReEnumeration(
        FastbootSession session,
        Func<FastbootSession, FastbootSession?> reopenSession,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken,
        bool allowSkip)
    {
        if (_options.ReEnumerateSettleMs > 0)
        {
            Thread.Sleep(_options.ReEnumerateSettleMs);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fresh = reopenSession(session);
        if (fresh is null)
        {
            if (allowSkip)
            {
                Report(progress, "skip", null, "device did not re-enumerate; continuing with the previous session");
                return session;
            }

            throw new FastbootException(FastbootResponseState.Fail,
                "device did not re-enumerate after leaving fastboot");
        }

        return fresh;
    }

    private void ExecuteMultiFlash(
        FastbootSession session,
        SpacemiTMultiFlashAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var template in action.RelatePartition)
        {
            var rawSize = ResolveTemplate(template);
            if (rawSize is null)
            {
                Report(progress, "skip", null, $"partition template '{template}' has no device answer");
                continue;
            }

            if (SpacemiTFlashPackage.IsAbsentStorageAnswer(rawSize))
            {
                Report(progress, "skip", null, $"storage answer '{rawSize}' marks this storage kind absent");
                continue;
            }

            var tablePath = _package.ResolvePartitionTable(rawSize);
            if (tablePath is null)
            {
                if (action.SkipFail)
                {
                    Report(progress, "skip", null, $"no partition table matches '{rawSize}'");
                    continue;
                }

                throw new FastbootException(FastbootResponseState.Fail,
                    $"no matching partition file for device answer '{rawSize}'");
            }

            var table = SpacemiTPartitionTable.Load(tablePath);
            Report(progress, "flash-table", null,
                $"flashing {Path.GetFileName(tablePath)} (format {table.Format})");
            FlashTable(session, table, tablePath, action, progress, cancellationToken);
        }
    }

    private void FlashTable(
        FastbootSession session,
        SpacemiTPartitionTable table,
        string tablePath,
        SpacemiTFlashAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var targets = CollectTargets(table, progress, cancellationToken);
        if (targets.Count == 0)
        {
            Report(progress, "skip", null, "no target partitions in this table");
            return;
        }

        RunWithRetry($"flash {table.Format}", action.Retry, action.SkipFail, progress, null, () =>
        {
            using var stream = File.OpenRead(tablePath);
            session.DownloadData(stream, stream.Length, null, action.Timeout, cancellationToken).ThrowIfFailed();
            session.ExecuteCommand(FastbootConstants.CommandFlash + table.Format).ThrowIfFailed();
            return true;
        });

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlashPartition(session, target, table.Format, action.Retry, action.Timeout, progress, cancellationToken);
        }
    }

    /// <remarks>
    /// A vendor regex image marker (<c>/…/</c>) is resolved against every file's package-relative
    /// path: zero matches keeps the "missing from the package" failure, and two or more matches fail
    /// the run because an ambiguous target must never be flashed silently. An entry whose
    /// <c>volume_images</c> object maps NAND volumes to images is flashed one volume at a time as the
    /// synthesized target <c>&lt;partition&gt;-&lt;volume&gt;</c>, matching the device flow that
    /// writes a volume with <c>fastboot flash &lt;partition&gt;-&lt;volume&gt;</c>. An entry the
    /// tables leave without any image is flashed from <see cref="SpacemiTFlashOptions.PartitionImageOverrides"/>
    /// when the run carries one, and skipped otherwise — the table write alone created that partition.
    /// <para>厂商的正则镜像标记（<c>/…/</c>）按包内每个文件的包相对路径解析：零命中保持"missing from
    /// the package"失败语义；命中两个及以上时整个运行失败，因为歧义目标绝不能被静默刷写。条目的
    /// <c>volume_images</c> 对象把 NAND 卷映射到镜像时，逐卷按合成目标
    /// <c>&lt;分区&gt;-&lt;卷&gt;</c> 刷写，对应设备端以 <c>fastboot flash &lt;分区&gt;-&lt;卷&gt;</c>
    /// 写卷的流程。布局表完全未给镜像的条目在运行携带
    /// <see cref="SpacemiTFlashOptions.PartitionImageOverrides"/> 时以覆盖文件刷写，否则跳过——仅写表
    /// 时该分区只是被创建。</para>
    /// </remarks>
    private List<(SpacemiTPartitionEntry Entry, string TargetName, string ImagePath, long Length)> CollectTargets(
        SpacemiTPartitionTable table,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var targets = new List<(SpacemiTPartitionEntry, string, string, long)>();
        foreach (var entry in table.Partitions)
        {
            if (entry.Images.Count > 0)
            {
                if (!RunsForTarget(entry.Name))
                {
                    continue;
                }

                foreach (var image in entry.Images)
                {
                    AddImageTarget(targets, entry, entry.Name, image, progress, cancellationToken);
                }
            }
            else if (entry.VolumeImages.Count > 0)
            {
                foreach (var (volume, image) in entry.VolumeImages)
                {
                    var targetName = $"{entry.Name}-{volume}";
                    if (!RunsForTarget(targetName))
                    {
                        continue;
                    }

                    Report(progress, "flash-table", targetName, $"volume '{volume}' of partition '{entry.Name}' maps to '{image}'");
                    AddImageTarget(targets, entry, targetName, image, progress, cancellationToken);
                }
            }
            else if (RunsForTarget(entry.Name) &&
                _options.PartitionImageOverrides.TryGetValue(entry.Name, out var overrideImage))
            {
                // A partition the package leaves without an image (a reserved slot such as misc) is written from the
                // host-picked file instead; the table write alone created it empty.
                // 包内未给镜像的分区（如 misc 这样的保留槽位）改写宿主选择的文件；仅写表时它只是空分区。
                if (!File.Exists(overrideImage))
                {
                    throw new FastbootException(FastbootResponseState.Fail,
                        $"partition '{entry.Name}' maps to host image '{overrideImage}' which does not exist");
                }

                var overrideSize = new FileInfo(overrideImage).Length;
                var overrideLength = entry.Size > 0 ? Math.Min(overrideSize, entry.Size) : overrideSize;
                Report(progress, "flash-table", entry.Name,
                    $"partition '{entry.Name}' has no package image; flashing the host-picked file ({overrideLength} bytes)");
                if (overrideLength > 0)
                {
                    targets.Add((entry, entry.Name, overrideImage, overrideLength));
                }
            }
        }

        return targets;
    }

    /// <summary>Reports whether a target name passes the <see cref="SpacemiTFlashOptions.OnlyPartitions"/> and <see cref="SpacemiTFlashOptions.SkipPartitions"/> filters, matched ordinally. <para>判断目标名是否通过 <see cref="SpacemiTFlashOptions.OnlyPartitions"/> 与 <see cref="SpacemiTFlashOptions.SkipPartitions"/> 过滤，按 Ordinal 匹配。</para></summary>
    private bool RunsForTarget(string targetName)
    {
        if (_options.OnlyPartitions is { Count: > 0 } only && !only.Contains(targetName, StringComparer.Ordinal))
        {
            return false;
        }

        return !_options.SkipPartitions.Contains(targetName, StringComparer.Ordinal);
    }

    /// <summary>Resolves one image reference for a target and appends it when it yields bytes to write; a regex reference must match exactly one package file. <para>为目标解析一个镜像引用并在其产生可写字节时追加；正则引用必须恰好命中一个包内文件。</para></summary>
    private void AddImageTarget(
        List<(SpacemiTPartitionEntry Entry, string TargetName, string ImagePath, long Length)> targets,
        SpacemiTPartitionEntry entry,
        string targetName,
        string image,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var matches = _package.ResolveImageMatches(image, cancellationToken);
        if (matches.Count == 0)
        {
            throw new FastbootException(FastbootResponseState.Fail,
                $"partition '{entry.Name}' references image '{image}' which is missing from the package");
        }

        if (matches.Count > 1)
        {
            throw new FastbootException(FastbootResponseState.Fail,
                $"partition '{entry.Name}' references image '{image}' which matches {matches.Count} files in the package; the target '{targetName}' is ambiguous");
        }

        var resolved = matches[0];
        var fileSize = new FileInfo(resolved).Length;
        var length = entry.Size > 0 ? Math.Min(fileSize, entry.Size) : fileSize;
        if (length > 0)
        {
            targets.Add((entry, targetName, resolved, length));
        }
        else
        {
            Report(progress, "skip", targetName, $"image '{image}' contributes no bytes for target '{targetName}'");
        }
    }

    private void FlashPartition(
        FastbootSession session,
        (SpacemiTPartitionEntry Entry, string TargetName, string ImagePath, long Length) target,
        string tableFormat,
        int retry,
        TimeSpan? timeout,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (entry, targetName, imagePath, length) = target;
        var wroteSparse = false;
        RunWithRetry($"flash {targetName}", retry, skipFail: false, progress, targetName, () =>
        {
            using var stream = File.OpenRead(imagePath);
            var maxDownload = session.GetMaxDownloadSize();
            if (HasSparseHeader(stream))
            {
                wroteSparse = true;
                if (length > maxDownload)
                {
                    FlashResparseChunks(session, imagePath, targetName, maxDownload, timeout, progress, cancellationToken);
                    return true;
                }

                Report(progress, "flash-partition", targetName, $"flashing sparse image ({length} bytes)");
                session.DownloadData(stream, length, CreateByteProgress(progress, "flash-partition", targetName, length), timeout, cancellationToken)
                    .ThrowIfFailed();
                session.ExecuteCommand(FastbootConstants.CommandFlash + targetName).ThrowIfFailed();
                return true;
            }

            if (HasGzipHeader(stream) && length > maxDownload)
            {
                ResetChunkedWriteState(session, targetName, timeout, cancellationToken);
                using var decompressed = CompressionService.CreateDecompressionStream(stream, CompressionFormat.Gzip);
                FlashGzipChunks(session, targetName, decompressed, entry.Size > 0 ? entry.Size : -1,
                    expectFullLength: false, maxDownload,
                    TryParseGzipLevel(entry.Compress) ?? _options.GzipLevel, timeout, progress, cancellationToken);
                return true;
            }

            if (length <= maxDownload)
            {
                Report(progress, "flash-partition", targetName, $"flashing {length} bytes");
                session.DownloadData(stream, length, CreateByteProgress(progress, "flash-partition", targetName, length), timeout, cancellationToken)
                    .ThrowIfFailed();
                session.ExecuteCommand(FastbootConstants.CommandFlash + targetName).ThrowIfFailed();
                return true;
            }

            ResetChunkedWriteState(session, targetName, timeout, cancellationToken);
            FlashGzipChunks(session, targetName, stream, length, expectFullLength: true, maxDownload,
                TryParseGzipLevel(entry.Compress) ?? _options.GzipLevel, timeout, progress, cancellationToken);
            return true;
        });

        // A hidden block-device partition has no device GPT entry, so its read-back probe answers "no such
        // partition" (and boot-area content is rebuilt by the device itself); an MTD partition is always readable,
        // which is how the vendor NOR flow verifies bootinfo. The env skip is the vendor script's own.
        // 块设备上的 hidden 分区在设备 GPT 中没有条目，回读探测会应答 "no such partition"（且 boot 区内容由设备端
        // 自行重建）；MTD 分区始终可读，厂商 NOR 流程正是如此校验 bootinfo。env 跳过来自厂商脚本本身。
        if (_options.VerifyAfterFlash && !wroteSparse && !VerifySkippedTargets.Contains(targetName) &&
            !(tableFormat == SpacemiTConstants.PartitionFormatGpt && target.Entry.Hidden))
        {
            VerifyFlash(session, targetName, imagePath, length, progress, cancellationToken);
        }
    }

    /// <remarks>
    /// The comparison covers exactly the bytes the transfer wrote (the source image's first
    /// <c>length</c> bytes versus the read-back's first <c>length</c> bytes), because a partition
    /// larger than the image still holds erased content beyond the written region. Read-back is not
    /// wrapped in <see cref="RunWithRetry"/>: retrying a content mismatch cannot change the digest,
    /// so transport failures surface to the caller directly.
    /// <para>比对只覆盖传输实际写入的字节（源镜像前 <c>length</c> 字节对回读前 <c>length</c> 字节），
    /// 因为比分区小的镜像之外的区域仍是擦除内容。回读不经过 <see cref="RunWithRetry"/>：重试内容不匹配
    /// 不会改变摘要，传输异常直接抛给调用方。</para>
    /// </remarks>
    private void VerifyFlash(
        FastbootSession session,
        string targetName,
        string imagePath,
        long length,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceDigest = SourceImageDigest(targetName, imagePath, length);
        var readDigest = Sha256.CreateAccumulator();
        byte[] readDigestBytes;
        try
        {
            using var hashing = new DigestPrefixStream(readDigest, length);
            Report(progress, "verify-partition", targetName, $"verifying {targetName} against '{Path.GetFileName(imagePath)}' by reading back {length} bytes");
            SpacemiTPartitionReader.Read(session, targetName, hashing, length,
                CreateByteProgress(progress, "verify-partition", targetName, length), cancellationToken);
            readDigestBytes = readDigest.GetHashAndReset();
        }
        finally
        {
            readDigest.Dispose();
        }

        if (!readDigestBytes.AsSpan().SequenceEqual(sourceDigest))
        {
            throw new FastbootException(FastbootResponseState.Fail,
                $"verify mismatch for partition '{targetName}': read-back content does not match image '{Path.GetFileName(imagePath)}'");
        }

        Report(progress, "verify-partition", targetName, $"verified {targetName} ({length} bytes match)");
    }

    /// <summary>Computes the SHA-256 over the first <paramref name="length"/> bytes of a source image, streaming and cached per target so repeated verify runs do not re-hash. <para>对源镜像前 <paramref name="length"/> 字节流式计算 SHA-256，按目标缓存，使重复校验不必重新哈希。</para></summary>
    private byte[] SourceImageDigest(string targetName, string imagePath, long length)
    {
        if (_sourceDigests.TryGetValue(targetName, out var cached))
        {
            return cached;
        }

        byte[] digest;
        using (var stream = File.OpenRead(imagePath))
        {
            if (stream.Length <= length)
            {
                digest = Sha256.ComputeHash(stream);
            }
            else
            {
                var accumulator = Sha256.CreateAccumulator();
                try
                {
                    var buffer = new byte[64 * 1024];
                    long hashed = 0;
                    while (hashed < length)
                    {
                        var want = (int)Math.Min(buffer.Length, length - hashed);
                        var read = 0;
                        while (read < want)
                        {
                            var filled = stream.Read(buffer, read, want - read);
                            if (filled <= 0)
                            {
                                throw new EndOfStreamException($"image '{imagePath}' ended early at {hashed + read}/{length}");
                            }

                            read += filled;
                        }

                        accumulator.Append(buffer.AsSpan(0, read));
                        hashed += read;
                    }

                    digest = accumulator.GetHashAndReset();
                }
                finally
                {
                    accumulator.Dispose();
                }
            }
        }

        _sourceDigests[targetName] = digest;
        return digest;
    }

    /// <summary>
    /// Stream wrapper that feeds only the first N bytes into a digest accumulator and silently
    /// discards anything beyond, so read-back padding the device adds (block-aligned NOR reads
    /// round the staged length up) never enters the comparison.
    /// <para>流包装：只把前 N 字节送入摘要累加器，超出部分静默丢弃，使设备端因块对齐 NOR 读取向上
    /// 取整而多送的回读填充不进入比对。</para>
    /// </summary>
    private sealed class DigestPrefixStream(DigestAccumulator accumulator, long hashedBytes) : Stream
    {
        private long _accepted;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _accepted;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            var remaining = hashedBytes - _accepted;
            if (remaining <= 0 || count <= 0)
            {
                return;
            }

            var take = (int)Math.Min(count, remaining);
            accumulator.Append(buffer.AsSpan(offset, take));
            _accepted += take;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }
    }

    private static int? TryParseGzipLevel(string? compress)
    {
        if (compress is null)
        {
            return null;
        }

        var match = Regex.Match(compress, "^gzip-([1-9])$");
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <remarks>
    /// Every chunk must decompress to a multiple of the device storage block size: the device
    /// floors the running decompressed append offset to that block and rounds each raw write up
    /// to it, so an unaligned chunk would shift every following write and corrupt the image
    /// (while the device's own per-chunk readback checksum still passes). The chunk budget
    /// therefore floors the raw size to a block multiple, and the final chunk is zero-padded to
    /// the next block boundary so the trailing partial block is written deterministically.
    /// The vendor flasher chunks at the max-download-size verbatim instead, which only stays
    /// correct because device answers are block-aligned in practice and typical images compress
    /// well; both assumptions are made explicit here. Requested levels 1-3 map to level 4 because
    /// the BCL gzip Fastest band lacks the stored-block fallback that keeps incompressible
    /// members within the zlib expansion bound (its output expands ~5.5% on random data).
    /// <paramref name="expectFullLength"/> distinguishes exact-length raw sources, where an early
    /// end of stream is a truncated image, from EOF-terminated decompressed streams whose total
    /// length is only bounded by <paramref name="totalBytes"/>.
    /// <para>每个分块必须解压为设备存储块大小的倍数：设备端把解压后的追加偏移向下取整到块，
    /// 并把每次 raw 写入向上取整到块，未对齐的分块会使之后所有写入整体位移而损坏镜像（且设备端
    /// 逐块的回读校验仍然通过）。因此分块预算向下取整到块的倍数，最后一个分块补零到下一块边界，
    /// 使末尾不足一块的部分被确定性地写入。厂商烧录器直接以 max-download-size 作为分块尺寸，
    /// 仅因设备应答在实践中块对齐、常见镜像可压缩而成立；本实现把这两个假设显式化。请求的
    /// 1-3 级映射到 4 级：BCL gzip 的 Fastest 档缺少把不可压缩成员保持在 zlib 膨胀上界内的
    /// stored 块回退（对随机数据输出膨胀约 5.5%）。
    /// <paramref name="expectFullLength"/> 区分定长 raw 来源（提前读到流末尾即镜像被截断）与以
    /// EOF 结束、仅以 <paramref name="totalBytes"/> 为上限的解压流。</para>
    /// </remarks>
    private void FlashGzipChunks(
        FastbootSession session,
        string partition,
        Stream source,
        long totalBytes,
        bool expectFullLength,
        long maxDownload,
        int gzipLevel,
        TimeSpan? timeout,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "flash-partition", partition,
            totalBytes >= 0
                ? $"image exceeds max-download-size {maxDownload}; flashing {totalBytes} bytes as block-aligned gzip chunks"
                : $"image exceeds max-download-size {maxDownload}; flashing decompressed stream as block-aligned gzip chunks");

        var chunkBytes = checked((int)ComputeGzipChunkBytes(maxDownload));
        if (chunkBytes <= 0)
        {
            throw new FastbootException(FastbootResponseState.Fail,
                $"max-download-size {maxDownload} is too small for block-aligned gzip chunking");
        }

        var buffer = new byte[chunkBytes];
        var compressionLevel = gzipLevel switch
        {
            <= 3 => 4,
            <= 8 => 6,
            _ => 9,
        };
        var compressionOptions = new CompressionOptions { Level = compressionLevel };

        long sent = 0;
        var exhausted = false;
        while (totalBytes < 0 ? !exhausted : sent < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(chunkBytes, totalBytes < 0 ? chunkBytes : totalBytes - sent);
            int read = 0;
            while (read < want)
            {
                var filled = source.Read(buffer, read, want - read);
                if (filled <= 0)
                {
                    exhausted = true;
                    break;
                }

                read += filled;
            }

            if (expectFullLength && exhausted && sent + read < totalBytes)
            {
                throw new EndOfStreamException($"image ended early at {sent + read}/{totalBytes}");
            }

            if (read == 0)
            {
                break;
            }

            sent += read;
            var isFinalChunk = exhausted || (totalBytes >= 0 && sent >= totalBytes);
            var payloadBytes = read;
            if (isFinalChunk)
            {
                payloadBytes = (read + FlashBlockSizeBytes - 1) / FlashBlockSizeBytes * FlashBlockSizeBytes;
                Array.Clear(buffer, read, payloadBytes - read);
            }

            using var compressed = new MemoryStream();
            CompressionService.Compress(
                new MemoryStream(buffer, 0, payloadBytes, writable: false),
                compressed,
                CompressionFormat.Gzip,
                compressionOptions);
            compressed.Position = 0;
            if (compressed.Length > maxDownload)
            {
                throw new FastbootException(FastbootResponseState.Fail,
                    $"gzip chunk for '{partition}' compressed to {compressed.Length} bytes, exceeding max-download-size {maxDownload}; the expansion bound was underestimated");
            }

            session.DownloadData(compressed, compressed.Length,
                    CreateScaledByteProgress(progress, partition, sent - read, read, compressed.Length, totalBytes),
                    timeout, cancellationToken)
                .ThrowIfFailed();
            session.ExecuteCommand(FastbootConstants.CommandFlash + partition).ThrowIfFailed();
            Report(progress, "flash-partition", partition,
                totalBytes >= 0 ? $"gzip chunk sent, {sent}/{totalBytes} bytes" : $"gzip chunk sent, {sent} bytes");
        }
    }

    /// <summary>Computes the raw bytes per gzip chunk for a device max-download-size: the gzip
    /// worst-case expansion is reserved as headroom and the result is floored to the device
    /// storage block size, so every chunk decompresses to a block multiple as the device's
    /// append-offset math requires and no member can exceed the budget even for incompressible
    /// input. The vendor flasher skips both reservations and relies on device answers being
    /// block-aligned and images being compressible.
    /// <para>按设备 max-download-size 计算每个 gzip 分块的原始字节数：预留 gzip 最坏膨胀余量后
    /// 向下取整到设备存储块大小，使每个分块按设备追加偏移数学的要求解压为块的倍数，且不可压缩
    /// 输入也不会有任何成员超出预算。厂商烧录器省略了这两项预留，依赖设备应答块对齐与镜像可压缩。</para></summary>
    /// <param name="maxDownload">Negotiated device max-download-size in bytes; values above the
    /// per-chunk cap are clamped first. <para>协商的设备 max-download-size 字节数；超过单分块上限的值
    /// 会先被截断。</para></param>
    /// <returns>The raw chunk size in bytes; non-positive when the budget cannot hold a block-aligned chunk.
    /// <para>以字节计的分块原始尺寸；预算不足以容纳块对齐分块时为非正值。</para></returns>
    internal static long ComputeGzipChunkBytes(long maxDownload)
    {
        var grossBudget = Math.Min(maxDownload, MaxGzipChunkBytes);
        var headroom = GzipCompressBound(grossBudget) - grossBudget + GzipChunkSafetyMargin;
        var chunkBytes = Math.Min(Math.Max(grossBudget - headroom, 0), (long)int.MaxValue);
        return chunkBytes - chunkBytes % FlashBlockSizeBytes;
    }

    /// <summary>Writes one raw block of zeros to the partition so the device's running gzip append
    /// offset restarts from the partition beginning before a fresh chunk sequence.
    /// <para>向分区写入一个 raw 零块，使设备端的 gzip 追加偏移在新的分块序列开始前归零到分区起始。</para></summary>
    /// <remarks>
    /// The device keeps the append offset and last partition name in static state across
    /// <c>flash</c> calls and only clears them when a non-gzip payload arrives, so a retryed or
    /// re-run chunked sequence would otherwise continue at the stale offset of an earlier failed
    /// attempt; the zero block is fully overwritten by the first chunk afterwards.
    /// <para>设备端把追加偏移与上次分区名保存在跨 <c>flash</c> 调用的静态状态里，只在收到非 gzip
    /// 负载时才清除；否则重试或重跑的分块序列会从上一次失败尝试遗留的偏移继续写入。零块随后会被
    /// 第一个分块完整覆盖。</para>
    /// </remarks>
    private static void ResetChunkedWriteState(
        FastbootSession session, string partition, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var reset = new MemoryStream(new byte[FlashBlockSizeBytes], writable: false);
        session.DownloadData(reset, FlashBlockSizeBytes, null, timeout, cancellationToken).ThrowIfFailed();
        session.ExecuteCommand(FastbootConstants.CommandFlash + partition).ThrowIfFailed();
    }

    /// <summary>Conservative upper bound on the size of a gzip-compressed payload for
    /// <paramref name="inputLen"/> raw bytes, matching zlib's <c>compressBound</c> formula plus the
    /// gzip wrapper overhead (the gzip wrapper is ~12 bytes larger than a zlib stream). This is the
    /// full compressed size, so the expansion overhead itself is <c>GzipCompressBound(x) - x</c>.
    /// <para>对 <paramref name="inputLen"/> 字节原始数据压缩为 gzip 后的保守尺寸上限，与 zlib 的
    /// <c>compressBound</c> 公式一致，并计入 gzip 包装开销（gzip 包装比 zlib 流大约多 12 字节）。
    /// 这是压缩后的完整尺寸，因此膨胀开销本身为 <c>GzipCompressBound(x) - x</c>。</para></summary>
    /// <remarks>
    /// The formula is zlib's compressBound: <c>sourceLen + (sourceLen&gt;&gt;12) + (sourceLen&gt;&gt;14) +
    /// (sourceLen&gt;&gt;25) + 13</c>, plus the gzip wrapper overhead (10 header + 8 trailer - 6 zlib
    /// wrapper = 12).
    /// <para>公式即 zlib compressBound：<c>sourceLen + (sourceLen&gt;&gt;12) + (sourceLen&gt;&gt;14) +
    /// (sourceLen&gt;&gt;25) + 13</c>，再加 gzip 包装开销（10 字节头 + 8 字节尾 - 6 字节 zlib 包装 = 12）。</para>
    /// </remarks>
    internal static long GzipCompressBound(long inputLen)
    {
        if (inputLen <= 0)
        {
            return 0;
        }

        return inputLen + (inputLen >> 12) + (inputLen >> 14) + (inputLen >> 25) + 13 + 12;
    }

    /// <summary>
    /// Flashes an oversized sparse image by reparsing it into self-contained sparse chunks
    /// (absolute block addressing, matching the vendor flasher's SparseImageSplitter output
    /// shape) under <c>%TEMP%</c>, then flashing each chunk; the device's sparse writer
    /// resolves every chunk's absolute offsets against the partition start. The temporary
    /// chunk directory is removed afterwards.
    /// <para>把超过 max-download-size 的 sparse 镜像重切为自包含稀疏分片（绝对块寻址，与厂商
    /// 烧录器 SparseImageSplitter 的产出形态一致）到 <c>%TEMP%</c> 下并逐片烧写；设备端 sparse
    /// 写入器按分区起始解析每个分片的绝对偏移。临时分片目录随后删除。</para>
    /// </summary>
    private void FlashResparseChunks(
        FastbootSession session,
        string imagePath,
        string partition,
        long maxDownload,
        TimeSpan? timeout,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "flash-partition", partition,
            $"sparse image exceeds max-download-size {maxDownload}; reparsing into chunks");
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"uotan-spacemit-sparse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            SparseImageConverter.ResparseImage(imagePath, Path.Combine(tempDirectory, "chunk_{0}.sparse"), maxDownload);
            var chunks = Directory.GetFiles(tempDirectory, "chunk_*.sparse")
                .OrderBy(path => int.TryParse(
                    Path.GetFileNameWithoutExtension(path).AsSpan("chunk_".Length),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : int.MaxValue,
                    Comparer<int>.Default)
                .ToList();
            if (chunks.Count == 0)
            {
                throw new FastbootException(FastbootResponseState.Fail,
                    $"sparse image '{Path.GetFileName(imagePath)}' produced no reparse chunks");
            }

            var totalBytes = chunks.Sum(path => new FileInfo(path).Length);
            long sentBytes = 0;
            for (var index = 0; index < chunks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var chunk = File.OpenRead(chunks[index]);
                var chunkLength = chunk.Length;
                Report(progress, "flash-partition", partition,
                    $"uploading chunk {index + 1}/{chunks.Count} ({chunkLength} bytes)");
                session.DownloadData(chunk, chunkLength, CreateByteProgress(progress, "flash-partition", partition, totalBytes), timeout, cancellationToken)
                    .ThrowIfFailed();
                session.ExecuteCommand(FastbootConstants.CommandFlash + partition).ThrowIfFailed();
                sentBytes += chunkLength;
                Report(progress, "flash-partition", partition, $"chunk done, {sentBytes}/{totalBytes} bytes");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private void ExecuteDirectFlash(
        FastbootSession session,
        SpacemiTFlashPartitionAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var file = RequireFile(action.File, action.Partition);
        RunWithRetry($"flash {action.Partition}", action.Retry, action.SkipFail, progress, action.Partition, () =>
        {
            using var stream = File.OpenRead(file);
            session.DownloadData(stream, stream.Length, CreateByteProgress(progress, "flash-partition", action.Partition, stream.Length), action.Timeout, cancellationToken)
                .ThrowIfFailed();
            session.ExecuteCommand(FastbootConstants.CommandFlash + action.Partition).ThrowIfFailed();
            return true;
        });
    }

    private void ExecuteErase(
        FastbootSession session,
        SpacemiTEraseAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        RunWithRetry($"erase {action.Partition}", action.Retry, action.SkipFail, progress, action.Partition,
            () =>
            {
                session.ErasePartition(action.Partition).ThrowIfFailed();
                return true;
            });
    }

    private void ExecuteReboot(
        FastbootSession session,
        SpacemiTRebootAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        RunWithRetry("reboot", action.Retry, action.SkipFail, progress, null,
            () =>
            {
                session.Reboot(action.Target).ThrowIfFailed();
                return true;
            });
    }

    private bool RunWithRetry(string describe, int retry, bool skipFail,
        IProgress<SpacemiTFlashProgress>? progress, string? partition, Func<bool> attempt)
    {
        for (var attemptNumber = 1; attemptNumber <= retry; attemptNumber++)
        {
            try
            {
                if (attempt())
                {
                    return true;
                }
            }
            catch (Exception e) when (e is FastbootException or IOException or NotSupportedException or EndOfStreamException)
            {
                if (attemptNumber < retry)
                {
                    Report(progress, "action", partition, $"{describe} attempt {attemptNumber}/{retry} failed ({e.Message}), retrying");
                    continue;
                }

                if (skipFail)
                {
                    Report(progress, "skip", partition, $"{describe} failed after {retry} attempt(s): {e.Message}");
                    return false;
                }

                throw new FastbootException(FastbootResponseState.Fail,
                    $"{describe} failed after {retry} attempt(s): {e.Message}");
            }
        }

        return false;
    }

    private static FastbootResponse SendWithTimeout(
        FastbootSession session, string command, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        session.SendCommand(command);
        return session.ReceiveResponse((int?)timeout?.TotalMilliseconds, cancellationToken);
    }

    private string? ResolveTemplate(string template)
    {
        var match = Regex.Match(template, @"\{([A-Za-z0-9_.-]+)\}");
        if (!match.Success)
        {
            return template;
        }

        return _variables.TryGetValue(match.Groups[1].Value, out var value) ? value : null;
    }

    private string RequireFile(string reference, string context)
    {
        var resolved = _package.ResolveFile(reference)
            ?? throw new FastbootException(FastbootResponseState.Fail,
                $"flash file not found for '{context}': {reference}");
        return resolved;
    }

    private static bool HasSparseHeader(FileStream stream)
    {
        Span<byte> header = stackalloc byte[SparseHeaderBytes];
        var position = stream.Position;
        var read = stream.Read(header);
        stream.Position = position;
        return read == SparseHeaderBytes &&
            BinaryPrimitives.ReadUInt32LittleEndian(header) == SparseFormat.SparseHeaderMagic;
    }

    /// <remarks>
    /// Mirrors the device's <c>check_gzip_format</c>: the gzip header magic (1f 8b), the deflate
    /// method byte and no reserved flag bits, so the host decompresses exactly the payloads the
    /// device would decompress.
    /// <para>镜像设备端 <c>check_gzip_format</c> 的判定：gzip 头魔数（1f 8b）、deflate 方法字节且无
    /// 保留标志位，使宿主解压的负载与设备会解压的负载完全一致。</para>
    /// </remarks>
    private static bool HasGzipHeader(FileStream stream)
    {
        Span<byte> header = stackalloc byte[GzipHeaderBytes];
        var position = stream.Position;
        var read = stream.Read(header);
        stream.Position = position;
        return read == GzipHeaderBytes &&
            header[0] == 0x1f && header[1] == 0x8b &&
            header[2] == 0x08 && (header[3] & 0x20) == 0;
    }

    private static IProgress<long>? CreateByteProgress(
        IProgress<SpacemiTFlashProgress>? progress, string phase, string? partition, long total) =>
        progress is null ? null : new ByteProgressAdapter(progress, phase, partition, total);

    /// <summary>
    /// Progress adapter for one gzip chunk: the device only sees the compressed payload, so the bytes it accepts are
    /// mapped back onto the chunk's raw window and reported against the whole image. Without this the chunk loop runs
    /// a 1.5 GB image through the transport without ever moving the page's progress bar.
    /// <para>单个 gzip 分块的进度适配器：设备只看到压缩后的负载，因此把设备接受的字节映射回该分块的原始区间，并以整幅镜像为
    /// 分母上报。没有它，1.5 GB 的镜像会整段经过传输而页面进度条一次都不动。</para>
    /// </summary>
    /// <param name="progress">The run's sink, or <see langword="null"/> to skip reporting.
    /// <para>本次运行的进度接收器；为 <see langword="null"/> 时不上报。</para></param>
    /// <param name="partition">Partition the chunk belongs to. <para>该分块所属的分区。</para></param>
    /// <param name="rawStart">Raw bytes of the image already written before this chunk.
    /// <para>本分块之前已写入的镜像原始字节数。</para></param>
    /// <param name="rawLength">Raw bytes this chunk decompresses to. <para>本分块解压后的原始字节数。</para></param>
    /// <param name="compressedLength">Compressed payload length the device accepts.
    /// <para>设备接受的压缩负载长度。</para></param>
    /// <param name="total">Total raw bytes of the image; negative when the package does not state one.
    /// <para>镜像的原始总字节数；包未给出时为负。</para></param>
    /// <returns>The adapter, or <see langword="null"/> when nothing can be reported.
    /// <para>适配器；无法上报时为 <see langword="null"/>。</para></returns>
    private static IProgress<long>? CreateScaledByteProgress(
        IProgress<SpacemiTFlashProgress>? progress, string partition,
        long rawStart, long rawLength, long compressedLength, long total)
    {
        if (progress is null || compressedLength <= 0 || rawLength <= 0)
        {
            return null;
        }

        return new ScaledByteProgress(progress, "flash-partition", partition, rawStart, rawLength, compressedLength,
            total);
    }

    /// <summary>
    /// Maps the compressed bytes of one chunk onto the image's raw byte range; see
    /// <see cref="CreateScaledByteProgress"/>.
    /// <para>把单个分块的压缩字节映射到镜像的原始字节区间；见 <see cref="CreateScaledByteProgress"/>。</para>
    /// </summary>
    private sealed class ScaledByteProgress(
        IProgress<SpacemiTFlashProgress> sink, string phase, string? partition,
        long rawStart, long rawLength, long compressedLength, long total) : IProgress<long>
    {
        public void Report(long value)
        {
            var accepted = Math.Clamp(value, 0, compressedLength);
            var raw = rawStart + (long)(accepted * (double)rawLength / compressedLength);
            sink.Report(new SpacemiTFlashProgress(phase, partition, raw, total >= 0 ? total : null,
                total >= 0 ? $"{raw * 100d / total:F1}%" : $"{raw} bytes"));
        }
    }

    private void Report(IProgress<SpacemiTFlashProgress>? progress, string phase, string? partition, string message) =>
        progress?.Report(new SpacemiTFlashProgress(phase, partition, null, null, message));

    private sealed class ByteProgressAdapter(
        IProgress<SpacemiTFlashProgress> sink, string phase, string? partition, long total) : IProgress<long>
    {
        public void Report(long value) =>
            sink.Report(new SpacemiTFlashProgress(phase, partition, value, total,
                $"{(double)value / total * 100:F1}%"));
    }
}