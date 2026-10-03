using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Host reachability layer for the MediaTek secure-boot subsystem, routed through the download-agent
/// family the caller already selected.
/// <para>联发科安全启动子系统的宿主可达层，全部经由调用方已选定的下载代理家族路由。</para>
/// </summary>
/// <remarks>
/// <para>Every entry validates the family, the session stage, the requested range and the write opt-in before
/// it touches the transport, so an unsupported or unconfirmed request costs zero device I/O. Device and
/// protocol failures come back as non-success outcomes carrying the vendor status code instead of being
/// reported as success, and the OEM key entry only prepares a request; submitting it stays with the session
/// APIs. The caller owns bootstrap: the session must already be at the stage the selected family reached
/// through <see cref="MtkFlashService"/> or <see cref="MtkBootstrap"/>.</para>
/// <para>每个入口在触碰传输层之前都会校验家族、会话阶段、请求范围与写入选择，因此不受支持或未确认的请求
/// 不产生任何设备读写。设备与协议失败以携带厂商状态码的非成功结果返回，而不会被上报为成功；OEM 密钥入口
/// 只准备请求，实际提交仍由会话 API 负责。调用方负责引导：会话必须已通过 <see cref="MtkFlashService"/> 或
/// <see cref="MtkBootstrap"/> 进入所选家族对应的阶段。</para>
/// </remarks>
internal static partial class MtkSecCfgService
{
    private const string SecCfgLabel = "seccfg";
    private const string EfuseLabel = "efuse";
    private const string OemKeyLabel = "oem-key";

    /// <summary>Reads and decodes the secure-boot configuration partition on the selected family.
    /// <para>在所选家族上读取并解码安全启动配置分区。</para></summary>
    /// <remarks>
    /// <para>The read is pure: no register is written, no lock state changes, and the decoded layout comes from
    /// the session decoders rather than a second host-side parser. The XML family carries no secure-boot
    /// configuration command at all, so that family is refused before any byte leaves the host.</para>
    /// <para>该读取是纯操作：不写寄存器、不改锁定状态，且解码布局取自会话自带的解码器而不是主机侧的第二套
    /// 解析。XML 家族完全没有安全启动配置命令，因此该家族在任何字节离开主机之前就被拒绝。</para>
    /// </remarks>
    /// <param name="session">A session already bootstrapped for <paramref name="family"/>.
    /// <para>已按 <paramref name="family"/> 完成引导的会话。</para></param>
    /// <param name="family">The download-agent family selected by the caller; no detection or fallback happens.
    /// <para>调用方选定的下载代理家族；不做探测或回退。</para></param>
    /// <param name="partitionId">The discovered physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现物理区域。</para></param>
    /// <param name="progress">Optional phase progress reporter. <para>可选的阶段进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole exchange.
    /// <para>整个交互过程的取消令牌。</para></param>
    /// <returns>The decoded view, or a non-success outcome carrying the vendor status code.
    /// <para>解码后的视图；或携带厂商状态码的非成功结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="NotSupportedException">The XML family was selected.
    /// <para>所选家族为 XML。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not at a stage the family can serve.
    /// <para>会话未处于该家族可服务的阶段。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The Legacy region identifier does not fit one byte.
    /// <para>Legacy 区域标识无法放进一个字节。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before or during the exchange.
    /// <para>调用方在交互之前或之中取消。</para></exception>
    internal static async Task<MtkSecCfgReportOutcome> ReadReportAsync(MtkSession session, MtkDaProtocol family,
        uint partitionId, IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireImplementedFamily(family, "secure-boot configuration read");
        RequireStage(session, family, partitionId, storageRequired: true, operation: "secure-boot configuration read");
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new FlashProgress(SecCfgLabel, 0, "Reading the secure-boot configuration partition..."));
        try
        {
            var view = family switch
            {
                MtkDaProtocol.XFlash => await session.ReadXFlashSecCfgAsync(partitionId, cancellationToken)
                    .ConfigureAwait(false),
                _ => await session.ReadLegacySecCfgAsync((byte)partitionId, cancellationToken)
                    .ConfigureAwait(false)
            };
            progress?.Report(new FlashProgress(SecCfgLabel, 100, "Secure-boot configuration decoded."));
            return new MtkSecCfgReportOutcome(MtkSecOperationStatus.Succeeded(DescribeReport(view)), view, family);
        }
        catch (IOException exception)
        {
            progress?.Report(new FlashProgress(SecCfgLabel, 0, "The secure-boot configuration read was rejected."));
            return new MtkSecCfgReportOutcome(
                MtkSecOperationStatus.Failed("The secure-boot configuration read failed on the target.", exception),
                null, family);
        }
    }

    /// <summary>Applies one documented lock-state transition to the secure-boot configuration partition.
    /// <para>对安全启动配置分区应用一个已记载的锁定状态转换。</para></summary>
    /// <remarks>
    /// <para>The opt-in is checked before the session is entered, so an unconfirmed request sends nothing at
    /// all and leaves the family at its current stage; the session API still keeps its own refusal for the
    /// case where the read reveals an off-device rewrite. A rejected status from the agent is reported as a
    /// failure with its vendor code, never as a completed write.</para>
    /// <para>选择在进入会话之前就完成校验，因此未确认的请求不会发送任何字节，并使家族保持当前阶段；会话 API
    /// 自身对“读取后发现在设备外无法重写”的拒绝仍然保留。代理返回的否定状态会连同厂商状态码一起上报为失败，
    /// 绝不当作写入完成。</para>
    /// </remarks>
    /// <param name="session">A session already bootstrapped for <paramref name="family"/>.
    /// <para>已按 <paramref name="family"/> 完成引导的会话。</para></param>
    /// <param name="family">The download-agent family selected by the caller.
    /// <para>调用方选定的下载代理家族。</para></param>
    /// <param name="intent">The documented state transition. <para>已记载的状态转换。</para></param>
    /// <param name="partitionId">The discovered physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现物理区域。</para></param>
    /// <param name="confirmed">The explicit caller opt-in that authorizes the partition write.
    /// <para>授权分区写入的显式调用方选择。</para></param>
    /// <param name="progress">Optional phase progress reporter. <para>可选的阶段进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole exchange.
    /// <para>整个交互过程的取消令牌。</para></param>
    /// <returns>The write receipt when the target acknowledged, otherwise a failure outcome.
    /// <para>目标确认时返回写入回执，否则返回失败结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intent"/> is undefined, or the Legacy
    /// region identifier does not fit one byte. <para><paramref name="intent"/> 未定义，或 Legacy 区域标识
    /// 无法放进一个字节。</para></exception>
    /// <exception cref="NotSupportedException">The XML family was selected. <para>所选家族为 XML。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not at a stage the family can serve.
    /// <para>会话未处于该家族可服务的阶段。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmed"/> is <see langword="false"/>;
    /// no byte was sent. <para><paramref name="confirmed"/> 为 <see langword="false"/>；未发送任何字节。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before or during the exchange.
    /// <para>调用方在交互之前或之中取消。</para></exception>
    internal static async Task<MtkSecCfgApplyOutcome> ApplyLockStateAsync(MtkSession session, MtkDaProtocol family,
        MtkSecCfgLockIntent intent, uint partitionId, bool confirmed, IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(intent))
        {
            throw new ArgumentOutOfRangeException(nameof(intent),
                $"The lock-state transition {intent} is not one of the documented values.");
        }

        RequireImplementedFamily(family, "secure-boot lock-state change");
        RequireStage(session, family, partitionId, storageRequired: true, operation: "secure-boot lock-state change");
        if (!confirmed)
        {
            throw new UnauthorizedAccessException(
                $"Changing the secure-boot configuration to {intent} rewrites a device partition and requires an explicit confirmation; the request was refused before any byte was sent.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new FlashProgress(SecCfgLabel, 0, $"Preparing the {intent} secure-boot configuration..."));
        try
        {
            var receipt = family switch
            {
                MtkDaProtocol.XFlash => await session.ApplyXFlashSecCfgAsync(intent, partitionId,
                    confirmWrite: true, cancellationToken).ConfigureAwait(false),
                _ => await session.ApplyLegacySecCfgAsync(intent, (byte)partitionId,
                    confirmWrite: true, cancellationToken).ConfigureAwait(false)
            };
            progress?.Report(new FlashProgress(SecCfgLabel, 100, "Secure-boot configuration written."));
            return new MtkSecCfgApplyOutcome(MtkSecOperationStatus.Succeeded(DescribeWrite(family, intent, receipt)),
                receipt, family);
        }
        catch (IOException exception)
        {
            progress?.Report(new FlashProgress(SecCfgLabel, 0, "The secure-boot configuration write was rejected."));
            return new MtkSecCfgApplyOutcome(
                MtkSecOperationStatus.Failed($"The {intent} request was not accepted by the target.", exception),
                null, family);
        }
    }

    /// <summary>Reads one documented named eFuse field through the selected family.
    /// <para>通过所选家族读取一个已记载的具名 eFuse 字段。</para></summary>
    /// <remarks>
    /// <para>Only the boot public key window is addressable by name because it is the only field the session
    /// APIs locate without a positional cell table; the address arithmetic is checked here so an overflowing
    /// base is refused before any register read is framed. The bytes are returned exactly as the agent
    /// reported them and no key material is reinterpreted.</para>
    /// <para>只有引导公钥窗口可以按名字寻址，因为它是唯一不需要按位置单元表就能定位的具名字段；这里的地址
    /// 运算预先受检，因此会溢出的基址在构造任何寄存器读取之前就被拒绝。返回字节按代理上报的原样给出，不重新
    /// 解释任何密钥材料。</para>
    /// </remarks>
    /// <param name="session">A session already bootstrapped for <paramref name="family"/>.
    /// <para>已按 <paramref name="family"/> 完成引导的会话。</para></param>
    /// <param name="family">The download-agent family selected by the caller.
    /// <para>调用方选定的下载代理家族。</para></param>
    /// <param name="field">The named field to read. <para>要读取的具名字段。</para></param>
    /// <param name="efuseBase">The eFuse base address supplied by the caller's device profile.
    /// <para>由调用方设备配置提供的 eFuse 基址。</para></param>
    /// <param name="progress">Optional phase progress reporter. <para>可选的阶段进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole exchange.
    /// <para>整个交互过程的取消令牌。</para></param>
    /// <returns>The exact field bytes, or a failure outcome carrying the vendor status code.
    /// <para>精确的字段字节；或携带厂商状态码的失败结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="NotSupportedException">The XML family was selected, or the field name has no documented
    /// location. <para>所选家族为 XML，或该字段名没有记载的位置。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not at a stage the family can serve.
    /// <para>会话未处于该家族可服务的阶段。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="efuseBase"/> plus the documented field
    /// offset would leave the 32-bit address space. <para><paramref name="efuseBase"/> 加上已记载字段偏移
    /// 会超出 32 位地址空间。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before or during the exchange.
    /// <para>调用方在交互之前或之中取消。</para></exception>
    internal static async Task<MtkSecEfuseFieldOutcome> ReadEfuseFieldAsync(MtkSession session, MtkDaProtocol family,
        MtkSecEfuseField field, uint efuseBase, IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireImplementedFamily(family, "named eFuse field read");
        if (field != MtkSecEfuseField.BootPublicKey)
        {
            throw new NotSupportedException(
                $"The eFuse field {field} has no documented location outside the positional cell table, so it cannot be read by name.");
        }

        RequireStage(session, family, partitionId: 0, storageRequired: false, operation: "named eFuse field read");
        RequireEfuseWindow(efuseBase, MtkEfuseAccess.PublicKeyOffset, MtkEfuseAccess.PublicKeyLength);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new FlashProgress(EfuseLabel, 0, "Reading the named eFuse field..."));
        try
        {
            var bytes = family switch
            {
                MtkDaProtocol.XFlash => await session.ReadXFlashBootPublicKeyAsync(efuseBase, cancellationToken)
                    .ConfigureAwait(false),
                _ => await session.ReadLegacyBootPublicKeyAsync(efuseBase, cancellationToken).ConfigureAwait(false)
            };
            progress?.Report(new FlashProgress(EfuseLabel, 100, "Named eFuse field read."));
            return new MtkSecEfuseFieldOutcome(
                MtkSecOperationStatus.Succeeded($"Read 0x{bytes.Length:X} bytes of {field} from 0x{checked(efuseBase + MtkEfuseAccess.PublicKeyOffset):X8}."),
                bytes, field, family);
        }
        catch (IOException exception)
        {
            progress?.Report(new FlashProgress(EfuseLabel, 0, "The named eFuse field read was rejected."));
            return new MtkSecEfuseFieldOutcome(
                MtkSecOperationStatus.Failed("The named eFuse field read failed on the target.", exception), null,
                field, family);
        }
    }

    /// <summary>Reads a bounded range of eFuse cells from the per-chip table through the selected family.
    /// <para>通过所选家族读取逐芯片表中一段有界的 eFuse 单元。</para></summary>
    /// <remarks>
    /// <para>The range is validated against the transcribed table before the session is entered, so an unknown
    /// hardware code or an out-of-range request costs no device traffic instead of half-reading the table.
    /// Cells the documented reader reports without an address are still returned, exactly as the session
    /// presents them.</para>
    /// <para>范围在进入会话之前就依据转录表完成校验，因此未知硬件代码或越界请求不会产生任何设备流量，也不会
    /// 只读半张表。已记载读取器无需寻址即可上报的单元仍会返回，且与会话给出的结果完全一致。</para>
    /// </remarks>
    /// <param name="session">A session already bootstrapped for <paramref name="family"/>.
    /// <para>已按 <paramref name="family"/> 完成引导的会话。</para></param>
    /// <param name="family">The download-agent family selected by the caller.
    /// <para>调用方选定的下载代理家族。</para></param>
    /// <param name="hardwareCode">The hardware code selecting the cell table.
    /// <para>用于选择单元表的硬件代码。</para></param>
    /// <param name="efuseBase">The eFuse base address supplied by the caller's device profile.
    /// <para>由调用方设备配置提供的 eFuse 基址。</para></param>
    /// <param name="startIndex">The first cell index to read. <para>要读取的首个单元索引。</para></param>
    /// <param name="count">The number of cells to read. <para>要读取的单元数量。</para></param>
    /// <param name="progress">Optional phase progress reporter. <para>可选的阶段进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole exchange.
    /// <para>整个交互过程的取消令牌。</para></param>
    /// <returns>The per-cell readings, or a failure outcome carrying the vendor status code.
    /// <para>逐单元读取结果；或携带厂商状态码的失败结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="NotSupportedException">The XML family was selected, or the hardware code has no
    /// transcribed table. <para>所选家族为 XML，或该硬件代码没有转录的单元表。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not at a stage the family can serve.
    /// <para>会话未处于该家族可服务的阶段。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested range is empty, oversized or outside the
    /// table, or the cell window overflows the 32-bit address space.
    /// <para>请求范围为空、超限或不在表内，或单元窗口超出 32 位地址空间。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before or during the exchange.
    /// <para>调用方在交互之前或之中取消。</para></exception>
    internal static async Task<MtkSecEfuseRangeOutcome> ReadEfuseRangeAsync(MtkSession session, MtkDaProtocol family,
        ushort hardwareCode, uint efuseBase, int startIndex, int count, IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireImplementedFamily(family, "eFuse cell read");
        RequireStage(session, family, partitionId: 0, storageRequired: false, operation: "eFuse cell read");
        if (!MtkEfuseFieldTable.TryGetCells(hardwareCode, out var cells))
        {
            throw new NotSupportedException(
                $"No transcribed eFuse cell table exists for hardware code 0x{hardwareCode:X4}; the request was refused before any byte was sent.");
        }

        MtkEfuseAccess.ValidateRange(cells, startIndex, count);
        RequireEfuseWindow(efuseBase, 0, checked(count * MtkEfuseAccess.CellLength));
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new FlashProgress(EfuseLabel, 0,
            $"Reading {count} eFuse cells from index {startIndex}..."));
        try
        {
            var reading = family switch
            {
                MtkDaProtocol.XFlash => await session.ReadXFlashEfuseCellsAsync(hardwareCode, efuseBase, startIndex,
                    count, cancellationToken).ConfigureAwait(false),
                _ => await session.ReadLegacyEfuseCellsAsync(hardwareCode, efuseBase, startIndex, count,
                    cancellationToken).ConfigureAwait(false)
            };
            progress?.Report(new FlashProgress(EfuseLabel, 100, "eFuse cell range read."));
            return new MtkSecEfuseRangeOutcome(
                MtkSecOperationStatus.Succeeded($"Read {reading.Cells.Count} cells for hardware code 0x{hardwareCode:X4} " +
                    $"({reading.ReadCellCount} fetched from the target)."), reading, family);
        }
        catch (IOException exception)
        {
            progress?.Report(new FlashProgress(EfuseLabel, 0, "The eFuse cell read was rejected."));
            return new MtkSecEfuseRangeOutcome(
                MtkSecOperationStatus.Failed("The eFuse cell read failed on the target.", exception), null, family);
        }
    }

    /// <summary>Validates and describes an OEM key flash request without submitting it.
    /// <para>校验并描述一次 OEM 密钥刷写请求，但不提交。</para></summary>
    /// <remarks>
    /// <para>This entry performs no device I/O in either direction: the key payload, the class, the register
    /// window and the family support are all decided on the host, and the returned plan names what a later
    /// submission would send. Without the opt-in the request is refused outright, and with it nothing is
    /// programmed either, because submitting key material stays with
    /// <see cref="MtkSession.ProgramXFlashOemKeyAsync"/> and <see cref="MtkSession.ProgramLegacyOemKeyAsync"/>.
    /// The key bytes themselves are never repeated in the plan, only their digest.</para>
    /// <para>本入口在任一方向都不执行设备读写：密钥载荷、类别、寄存器窗口与家族支持全部在主机侧决定，返回的
    /// 计划只说明后续提交会发送什么。缺少选择时请求直接被视为拒绝；即便带上选择也不会编程任何内容，因为提交
    /// 密钥材料仍由 <see cref="MtkSession.ProgramXFlashOemKeyAsync"/> 与
    /// <see cref="MtkSession.ProgramLegacyOemKeyAsync"/> 负责。计划中绝不重复密钥字节本身，只给出其摘要。</para>
    /// </remarks>
    /// <param name="session">A session already bootstrapped for <paramref name="family"/>.
    /// <para>已按 <paramref name="family"/> 完成引导的会话。</para></param>
    /// <param name="family">The download-agent family selected by the caller.
    /// <para>调用方选定的下载代理家族。</para></param>
    /// <param name="request">The key class request to prepare. <para>要准备的密钥类别请求。</para></param>
    /// <param name="confirmed">The explicit caller opt-in that authorizes a later submission.
    /// <para>授权后续提交的显式调用方选择。</para></param>
    /// <param name="progress">Optional phase progress reporter. <para>可选的阶段进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation before the plan is produced.
    /// <para>生成计划之前的取消令牌。</para></param>
    /// <returns>The prepared plan; nothing was sent to the device.
    /// <para>准备好的计划；未向设备发送任何内容。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="request"/> is null.
    /// <para><paramref name="session"/> 或 <paramref name="request"/> 为 null。</para></exception>
    /// <exception cref="NotSupportedException">The XML family was selected, or the Legacy family was asked for
    /// the replay-protected class it never submits. <para>所选家族为 XML，或要求 Legacy 家族提交其从不提交的
    /// 重放保护类别。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not at a stage the family can serve.
    /// <para>会话未处于该家族可服务的阶段。</para></exception>
    /// <exception cref="ArgumentException">The key payload, class or SEJ base address does not match the
    /// documented class. <para>密钥载荷、类别或 SEJ 基址与已记载类别不符。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmed"/> is <see langword="false"/>.
    /// <para><paramref name="confirmed"/> 为 <see langword="false"/>。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before the plan was produced.
    /// <para>调用方在生成计划之前取消。</para></exception>
    internal static MtkOemKeyPlanOutcome PrepareOemKeyRequest(MtkSession session, MtkDaProtocol family,
        MtkOemKeyRequest request, bool confirmed, IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        request = ResolveOemKeySejBase(session, request);
        var key = ValidateOemKeyRequest(session, family, request, confirmed);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new FlashProgress(OemKeyLabel, 0, "Validating the OEM key request..."));
        IReadOnlyList<uint> addresses = request.Kind == MtkOemKeyKind.SoftwareOneTimeProgramKey
            ? [.. MtkOemKeyRegisterWindow.Offsets.Select(offset => checked(request.SejBaseAddress!.Value + offset))]
            : [];
        var plan = new MtkOemKeyPlan(family, request.Kind, Convert.ToHexString(SHA256.HashData(key)), addresses,
            request.Kind == MtkOemKeyKind.SoftwareOneTimeProgramKey
                ? $"the stage-two register window starting at 0x{addresses[0]:X8}"
                : "the stage-two set-key control code",
            request.Kind == MtkOemKeyKind.SoftwareOneTimeProgramKey ? addresses.Count : 1);
        progress?.Report(new FlashProgress(OemKeyLabel, 100, "OEM key request prepared; nothing was submitted."));
        return new MtkOemKeyPlanOutcome(
            MtkSecOperationStatus.Succeeded($"Prepared a {request.Kind} submission on the {family} family through " +
                $"{plan.Delivery} ({plan.SubmittedUnits} unit(s)); the key digest is {plan.KeySha256Hex}. No bytes were sent."),
            plan);
    }

    /// <summary>Supplies the SEJ peripheral base the reference table records for the session's chip.
    /// <para>为会话芯片补齐参考表记录的 SEJ 外设基址。</para></summary>
    /// <remarks>
    /// <para>An address the operator typed always wins, so this only ever fills a gap. The chip entry comes from
    /// the identity the session already read during the boot handshake, which keeps the resolution free of device
    /// I/O. When neither the operator nor the reference table supplies one, the request is returned unchanged and
    /// <see cref="MtkOemKeyRequest.DecodeKey"/> still refuses it; no address is guessed.</para>
    /// <para>用户手动输入的地址永远优先，因此本方法只用于补缺。芯片条目来自会话在 boot 握手中已经读取的身份，
    /// 因此补齐过程不产生任何设备交互。当用户与参考表都没有给出地址时，请求原样返回，仍由
    /// <see cref="MtkOemKeyRequest.DecodeKey"/> 拒绝，绝不猜测地址。</para>
    /// </remarks>
    /// <param name="session">A session whose boot handshake already reported the chip identity.
    /// <para>已在 boot 握手上报芯片身份的会话。</para></param>
    /// <param name="request">The request as the caller built it. <para>调用方构造的原始请求。</para></param>
    /// <returns>The request with a recorded base when the class needs one, the caller left it empty and the
    /// reference table records it; otherwise the same instance.
    /// <para>当该类别需要基址、调用方留空且参考表有记录时返回补齐基址的请求；否则返回同一实例。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or
    /// <paramref name="request"/> is <see langword="null"/>.</exception>
    internal static MtkOemKeyRequest ResolveOemKeySejBase(MtkSession session, MtkOemKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ResolveOemKeySejBase(request, session.BypassChipProfile);
    }

    /// <summary>Applies the recorded-versus-entered precedence for one OEM key request.
    /// <para>对单个 OEM 密钥请求应用「记录值与用户输入值」的优先级。</para></summary>
    /// <param name="request">The request as the caller built it. <para>调用方构造的原始请求。</para></param>
    /// <param name="profile">The chip entry the reference table records for the connected chip, or
    /// <see langword="null"/> when the table documents no entry. <para>参考表为已连接芯片记录的条目；表未记录该
    /// 芯片时为 <see langword="null"/>。</para></param>
    /// <returns>The request, with <see cref="MtkOemKeyRequest.SejBaseAddress"/> filled from
    /// <see cref="MtkBromChipProfile.SejBaseAddress"/> only when the caller left it empty for a software
    /// one-time-program key. <para>请求本身；仅当调用方为软件一次性编程密钥留空时，才用
    /// <see cref="MtkBromChipProfile.SejBaseAddress"/> 填充 <see cref="MtkOemKeyRequest.SejBaseAddress"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    internal static MtkOemKeyRequest ResolveOemKeySejBase(MtkOemKeyRequest request, MtkBromChipProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != MtkOemKeyKind.SoftwareOneTimeProgramKey || request.SejBaseAddress is not null)
        {
            return request;
        }

        return profile?.SejBaseAddress is { } recorded
            ? request with { SejBaseAddress = recorded }
            : request;
    }

    /// <summary>Runs every local check an OEM key request has to pass before any byte is framed.
    /// <para>在任何字节成帧之前，对 OEM 密钥请求执行全部本地校验。</para></summary>
    /// <remarks>
    /// <para>Both the prepare-only and the submitting entry share this check, so an unsupported family, a class
    /// the family cannot frame, a session at the wrong stage, a malformed key or a missing confirmation is
    /// refused identically and without device I/O.</para>
    /// <para>仅准备入口与真正提交入口共用这段校验，因此不受支持的家族、该家族无法成帧的类别、阶段不符的会话、
    /// 非法密钥或缺少确认都会以同样的方式被拒绝，且不产生设备交互。</para>
    /// </remarks>
    /// <param name="session">A session already bootstrapped for <paramref name="family"/>.
    /// <para>已按 <paramref name="family"/> 完成引导的会话。</para></param>
    /// <param name="family">The download-agent family selected by the caller.
    /// <para>调用方选定的下载代理家族。</para></param>
    /// <param name="request">The key request. <para>密钥请求。</para></param>
    /// <param name="confirmed">Whether the caller explicitly confirmed an irreversible write.
    /// <para>调用方是否显式确认了不可逆写入。</para></param>
    /// <returns>The decoded key bytes. <para>解码后的密钥字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or
    /// <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The family frames no such key class.
    /// <para>该家族不成帧此类密钥。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The write was not confirmed.
    /// <para>写入未经确认。</para></exception>
    private static byte[] ValidateOemKeyRequest(MtkSession session, MtkDaProtocol family,
        MtkOemKeyRequest request, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        RequireImplementedFamily(family, "OEM key programming");
        if (family == MtkDaProtocol.Legacy && request.Kind != MtkOemKeyKind.SoftwareOneTimeProgramKey)
        {
            throw new NotSupportedException(
                $"The Legacy family offers no documented submission path for the {request.Kind} class; only the software one-time-program key is framed by that agent.");
        }

        RequireStage(session, family, partitionId: 0, storageRequired: false, operation: "OEM key programming");
        var key = request.DecodeKey();
        if (!confirmed)
        {
            throw new UnauthorizedAccessException(
                $"Programming the {request.Kind} changes irreversible device state and requires an explicit confirmation; the request was refused before any byte was sent.");
        }

        return key;
    }

    /// <summary>Submits an OEM key through the family's documented path after the shared local validation.
    /// <para>在共用的本地校验之后，按家族的已记载路径提交 OEM 密钥。</para></summary>
    /// <remarks>
    /// <para>The receipt the agent returns is reported verbatim: the key is identified by its digest and the
    /// submitted unit count, and no key material is echoed into the summary. Only an <see cref="IOException"/>
    /// from the protocol becomes a failed outcome; cancellation and local refusals propagate.</para>
    /// <para>代理返回的回执按原样上报：密钥只以摘要与提交单元数标识，摘要中不回显任何密钥材料。只有协议层的
    /// <see cref="IOException"/> 会转为失败结果；取消与本地拒绝照常抛出。</para>
    /// </remarks>
    /// <param name="session">A session already bootstrapped for <paramref name="family"/>.
    /// <para>已按 <paramref name="family"/> 完成引导的会话。</para></param>
    /// <param name="family">The download-agent family selected by the caller.
    /// <para>调用方选定的下载代理家族。</para></param>
    /// <param name="request">The key request to submit. <para>要提交的密钥请求。</para></param>
    /// <param name="confirmed">Must be <see langword="true"/> or the request is refused.
    /// <para>必须为 <see langword="true"/>，否则请求被拒绝。</para></param>
    /// <param name="progress">Optional phase progress reporter. <para>可选的阶段进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole exchange.
    /// <para>整个交互过程的取消令牌。</para></param>
    /// <returns>The submission outcome with the agent receipt when the target accepted it.
    /// <para>提交结果，目标接受时附带代理回执。</para></returns>
    internal static async Task<MtkOemKeyReceiptOutcome> ProgramOemKeyAsync(MtkSession session,
        MtkDaProtocol family, MtkOemKeyRequest request, bool confirmed,
        IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        request = ResolveOemKeySejBase(session, request);
        ValidateOemKeyRequest(session, family, request, confirmed);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new FlashProgress(OemKeyLabel, 50, $"Submitting the {request.Kind} key..."));
        try
        {
            var receipt = family switch
            {
                MtkDaProtocol.XFlash => await session.ProgramXFlashOemKeyAsync(request, confirmWrite: true,
                    cancellationToken).ConfigureAwait(false),
                _ => await session.ProgramLegacyOemKeyAsync(request, confirmWrite: true, cancellationToken)
                    .ConfigureAwait(false)
            };
            progress?.Report(new FlashProgress(OemKeyLabel, 100, "OEM key submitted."));
            return new MtkOemKeyReceiptOutcome(
                MtkSecOperationStatus.Succeeded($"Submitted the {receipt.Kind} key on the {family} family " +
                    $"({receipt.SubmittedUnits} unit(s), digest {receipt.KeySha256Hex})."),
                receipt, family);
        }
        catch (IOException exception)
        {
            progress?.Report(new FlashProgress(OemKeyLabel, 0, "The OEM key submission was rejected."));
            return new MtkOemKeyReceiptOutcome(
                MtkSecOperationStatus.Failed($"The {request.Kind} submission was not accepted by the target.",
                    exception), null, family);
        }
    }

    /// <summary>Refuses a family that implements none of the secure-boot commands.
    /// <para>拒绝不实现任何安全启动命令的家族。</para></summary>
    /// <param name="family">The selected download-agent family. <para>选定的下载代理家族。</para></param>
    /// <param name="operation">The refused operation, named in the refusal text.
    /// <para>被拒绝的操作，会在拒绝文案中点名。</para></param>
    /// <exception cref="NotSupportedException">The XML family carries no such command.
    /// <para>XML 家族不携带该命令。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The family value is undefined.
    /// <para>家族取值未定义。</para></exception>
    private static void RequireImplementedFamily(MtkDaProtocol family, string operation)
    {
        if (!Enum.IsDefined(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family),
                $"The download-agent family {family} is not one of the three supported families.");
        }

        if (family == MtkDaProtocol.Xml)
        {
            throw new NotSupportedException(
                $"The XML download-agent family implements no {operation}; its only security command uploads an explicitly supplied flash-policy or all-in-one signature resource, so the request was refused before any byte was sent.");
        }
    }

    /// <summary>Checks that the session is at a stage the selected family can serve for this operation.
    /// <para>校验会话已处于所选家族为该操作可服务的阶段。</para></summary>
    /// <remarks>
    /// <para>The check mirrors the stages the session APIs themselves require, so a refusal here means the
    /// caller skipped bootstrap for that family rather than a device rejection. A Legacy region identifier is
    /// bounded at the same time because the Legacy commands name a region with one byte while the XFlash
    /// commands use a 32-bit identifier.</para>
    /// <para>该校验镜像会话 API 自身要求的阶段，因此这里的拒绝意味着调用方跳过了该家族的引导，而不是设备
    /// 拒绝了请求。Legacy 区域标识同时在这里受限，因为 Legacy 命令用一个字节命名区域，而 XFlash 命令使用
    /// 32 位标识。</para>
    /// </remarks>
    /// <param name="session">The live session. <para>当前会话。</para></param>
    /// <param name="family">The selected download-agent family. <para>选定的下载代理家族。</para></param>
    /// <param name="partitionId">The physical region the operation names. <para>操作所指明的物理区域。</para></param>
    /// <param name="storageRequired">Whether the operation reads or writes block storage rather than the
    /// stage-two register window. <para>操作是否读写块存储而非二阶段寄存器窗口。</para></param>
    /// <param name="operation">The operation, named in the refusal text.
    /// <para>操作名称，会在拒绝文案中点名。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The Legacy region identifier does not fit one byte.
    /// <para>Legacy 区域标识无法放进一个字节。</para></exception>
    /// <exception cref="InvalidOperationException">The session is at another stage.
    /// <para>会话处于其他阶段。</para></exception>
    private static void RequireStage(MtkSession session, MtkDaProtocol family, uint partitionId,
        bool storageRequired, string operation)
    {
        if (family == MtkDaProtocol.Legacy && partitionId > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionId),
                $"The Legacy family names a region with one byte, so region {partitionId} cannot be addressed.");
        }

        var state = session.State;
        var isReady = family switch
        {
            MtkDaProtocol.XFlash => storageRequired
                ? state == MtkSessionState.XFlashStorageReady
                : state is MtkSessionState.XFlashStageTwoStarted or MtkSessionState.XFlashStorageReady,
            MtkDaProtocol.Legacy => state == MtkSessionState.LegacyStorageReady,
            _ => false
        };
        if (!isReady)
        {
            throw new InvalidOperationException(
                $"The {family} family serves {operation} only from its download-agent storage stage; the session is at {state}, so bootstrap is missing rather than the device refusing.");
        }
    }

    /// <summary>Checks that a field window stays inside the 32-bit address space.
    /// <para>校验字段窗口保持在 32 位地址空间之内。</para></summary>
    /// <param name="efuseBase">The eFuse base address. <para>eFuse 基址。</para></param>
    /// <param name="offset">The documented field offset from that base.
    /// <para>相对该基址的已记载字段偏移。</para></param>
    /// <param name="length">The window length in bytes. <para>窗口长度（字节）。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The window would leave the address space.
    /// <para>窗口会超出地址空间。</para></exception>
    private static void RequireEfuseWindow(uint efuseBase, uint offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return;
        }

        if ((long)efuseBase + offset + length > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(efuseBase),
                $"The window 0x{efuseBase:X8}+0x{offset:X}+0x{length:X} would leave the 32-bit address space.");
        }
    }

    /// <summary>Describes a decoded configuration without claiming more than the layout documents.
    /// <para>描述解码后的配置，而不声称超出布局记载的内容。</para></summary>
    /// <param name="view">The decoded view. <para>解码后的视图。</para></param>
    /// <returns>A host-readable summary of the located partition and its lock state.
    /// <para>定位到的分区及其锁定状态的主机可读摘要。</para></returns>
    private static string DescribeReport(MtkSecCfgView view)
    {
        if (view.Version4 is { } version4)
        {
            return $"Version 4 configuration on {view.PartitionName} at 0x{view.ByteOffset:X} " +
                $"(0x{view.ByteLength:X} bytes): lock state {version4.LockStateRaw} " +
                $"({DescribeOrUndocumented(version4.LockState)}), critical lock state {version4.CriticalLockStateRaw} " +
                $"({DescribeOrUndocumented(version4.CriticalLockState)}), runtime {version4.RuntimeRaw} " +
                $"({DescribeOrUndocumented(version4.Runtime)}), integrity {version4.HashKind}, " +
                $"image digest {view.ImageSha256Hex}.";
        }

        var version3 = view.Version3!;
        return $"Version 3 configuration on {view.PartitionName} at 0x{view.ByteOffset:X} " +
            $"(0x{view.ByteLength:X} bytes): declared size 0x{version3.Size:X}, encryption length " +
            $"0x{version3.EncryptionLength:X8} ({DescribeOrUndocumented(version3.DocumentedState)}), " +
            $"software lock counters {version3.SoftwareLockTry}/{version3.SoftwareLockDone}, body 0x{version3.BodyLength:X} " +
            $"bytes with digest {version3.BodySha256Hex}; the body needs the target SEJ engine to reinterpret.";
    }

    /// <summary>Describes an acknowledged configuration write.
    /// <para>描述一次已确认的配置写入。</para></summary>
    /// <param name="family">The family that performed the write. <para>执行写入的家族。</para></param>
    /// <param name="intent">The requested transition. <para>请求的状态转换。</para></param>
    /// <param name="receipt">The session write receipt. <para>会话写入回执。</para></param>
    /// <returns>A summary naming what was submitted and where.
    /// <para>写明所提交内容与其落点的说明。</para></returns>
    private static string DescribeWrite(MtkDaProtocol family, MtkSecCfgLockIntent intent,
        MtkSecCfgWriteResult receipt) =>
        $"The {family} agent acknowledged the {intent} write of 0x{receipt.ByteLength:X} {receipt.Format} bytes to " +
        $"{receipt.PartitionName} at 0x{receipt.ByteOffset:X} (submitted digest {receipt.ImageSha256Hex}); the new " +
        "state is only proven by a later read.";

    /// <summary>Renders an undocumented raw value as explicit text.
    /// <para>把未记载的原始取值渲染成明确的文本。</para></summary>
    /// <param name="value">The decoded name, or null when the evidence does not name the raw value.
    /// <para>解码后的名称；原始取值未被证据命名时为 null。</para></param>
    /// <returns>The name, or the undocumented marker. <para>名称；或表示未记载的标记文本。</para></returns>
    private static string DescribeOrUndocumented(Enum? value) => value?.ToString() ?? "undocumented";
}