using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Executes a Tegra download chain against an RCM (APX) endpoint: the RCM handshake, the miniloader download, the
/// switch to nv3p, the boot configuration table and the bootloader. It never writes storage partitions — those go
/// through the bootloader rather than nv3p — so the type stops once the bootloader has been handed over.
/// <para>对 RCM（APX）端点执行 Tegra 下载链：RCM 握手、miniloader 下载、切换到 nv3p、引导配置表与 bootloader。
/// 它从不写存储分区——分区写入经 bootloader 而非 nv3p——因此本类型在 bootloader 交付完成后即停止。</para>
/// </summary>
/// <remarks>
/// <para>The miniloader makes the device re-enumerate, so the transport the RCM handshake ran on stops answering once
/// the download succeeded. The caller therefore supplies a factory that opens a fresh transport on the endpoint the
/// device re-enumerated as, and this type disposes the first transport before it asks for the second.</para>
/// <para>miniloader 会让设备重新枚举，因此下载成功后，RCM 握手所用的传输不再应答。为此调用方提供工厂，在设备重新枚举
/// 出的端点上打开新传输，本类型在索取第二个传输之前释放第一个。</para>
/// <para>A failed step aborts the whole run: the exception propagates unchanged, the progress sink has already seen
/// the failure line, and no later stage runs.</para>
/// <para>任一步骤失败都会中止整条运行：异常原样向上传播，进度接收器已收到失败行，后续阶段不再执行。</para>
/// </remarks>
public sealed class TegraFlasher
{
    /// <summary>
    /// Largest miniloader this run reads into memory, which is the payload ceiling of a single RCM message.
    /// <para>本次运行读入内存的 miniloader 上限，即单条 RCM 消息的负载上界。</para>
    /// </summary>
    public const int MaximumMiniloaderLength = TegraRcmMessage.MaximumPayloadLength;

    private readonly TegraFlashOptions _options;
    private readonly TegraFlashPlan _plan;
    private readonly Func<CancellationToken, Task<ICommTransport>> _openNv3pTransport;

    /// <summary>
    /// Creates a flasher for one plan, its options and the factory that reopens the endpoint after the device
    /// re-enumerates. <para>为一份计划、其选项以及设备重新枚举后重开端点所用的工厂创建烧录器。</para>
    /// </summary>
    /// <param name="plan">The validated plan; the flasher consumes it without owning it.
    /// <para>已校验的计划；烧录器只消费而不接管其所有权。</para></param>
    /// <param name="options">The run options, whose confirmation phrases gate the run before any wire byte moves.
    /// <para>运行选项；其确认短语在任何线上字节移动之前约束本次运行。</para></param>
    /// <param name="openNv3pTransport">Opens a transport on the endpoint the device re-enumerated as after the
    /// miniloader download. The caller keeps ownership of what the factory returns and releases it when the run ends;
    /// the nv3p session this type creates never disposes it.
    /// <para>在 miniloader 下载后设备重新枚举出的端点上打开传输。工厂返回的传输由调用方持有，并在运行结束时释放；
    /// 本类型创建的 nv3p 会话绝不释放它。</para></param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.
    /// <para>任一参数为 <see langword="null"/>。</para></exception>
    public TegraFlasher(TegraFlashPlan plan, TegraFlashOptions options,
        Func<CancellationToken, Task<ICommTransport>> openNv3pTransport)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _openNv3pTransport = openNv3pTransport ?? throw new ArgumentNullException(nameof(openNv3pTransport));
    }

    /// <summary>
    /// Runs the chain on an open RCM transport and reports every stage.
    /// <para>在已打开的 RCM 传输上执行整条链并上报每个阶段。</para>
    /// </summary>
    /// <param name="rcmTransport">The transport the boot ROM answers on. The flasher never disposes it: the device
    /// re-enumerates once the miniloader runs, so its handle stops answering anyway, and the lease that opened it is
    /// the only owner allowed to release it. <para>boot ROM 应答所用的传输。烧录器绝不释放它：miniloader 运行后设备会
    /// 重新枚举，该句柄本就不再应答，而只有打开它的租约才有权释放。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels between stages and between transfer chunks.
    /// <para>在阶段之间与传输分块之间取消。</para></param>
    /// <returns>A task that completes once the bootloader has been handed over, or after the BCT when the plan
    /// carries no bootloader. <para>bootloader 交付完成后完成的任务；计划未携带 bootloader 时在 BCT 之后完成。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="rcmTransport"/> is <see langword="null"/>.
    /// <para><paramref name="rcmTransport"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The options or the plan do not validate, or the device reports an
    /// operating mode that forbids a download. <para>选项或计划未通过校验，或设备上报的操作模式禁止下载。</para></exception>
    /// <exception cref="NotSupportedException">The device requires signed downloads and this run carries no signing
    /// material; no byte of the affected payload is sent. <para>设备要求签名下载而本次运行没有签名材料；受影响负载的任何
    /// 字节都不会发出。</para></exception>
    /// <exception cref="TegraProtocolException">The device rejected a stage, answered a bad checksum, or violated the
    /// frame sequence. <para>设备拒绝了某个阶段、应答了错误校验和，或违反了帧序号。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task ExecuteAsync(ICommTransport rcmTransport, IProgress<TegraFlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rcmTransport);

        // Both gates run before the first byte: the options carry the confirmation phrases and the plan re-checks the
        // files the run will read.
        // 两道校验都在首个字节之前完成：选项携带确认短语，计划重新核对本次运行将读取的文件。
        _options.Validate();
        _plan.Validate();

        var target = _plan.Chip;
        Report(progress, "handshake", "rcm", null, null,
            $"Starting the Tegra RCM handshake for {target}.");

        await RunRcmHandshakeAsync(rcmTransport, target, progress, cancellationToken).ConfigureAwait(false);
        await DownloadMiniloaderAsync(rcmTransport, target, progress, cancellationToken).ConfigureAwait(false);

        await RunNv3pChainAsync(target, progress, cancellationToken).ConfigureAwait(false);

        Report(progress, "done", null, null, null, "Tegra download chain completed.");
    }

    /// <summary>
    /// Runs the RCM handshake: the three version queries and the synchronisation the reference performs before it
    /// downloads anything. <para>执行 RCM 握手：参考实现在下载任何内容之前所做的三次版本查询与同步。</para>
    /// </summary>
    private async Task RunRcmHandshakeAsync(ICommTransport transport, TegraChip chip,
        IProgress<TegraFlashProgress>? progress, CancellationToken cancellationToken)
    {
        using var session = new TegraRcmSession(transport);
        var rcmVersion = await session.QueryRcmVersionAsync(chip, cancellationToken).ConfigureAwait(false);
        Report(progress, "handshake", "rcm", null, null,
            $"RCM version {TegraConstants.GetRcmVersionMajor(rcmVersion)}."
            + $"{TegraConstants.GetRcmVersionMinor(rcmVersion)} reported.");
        var bootRomVersion = await session.QueryBrVersionAsync(chip, cancellationToken).ConfigureAwait(false);
        Report(progress, "handshake", "rcm", null, null, $"Boot ROM version 0x{bootRomVersion:X8} reported.");
        var bootDeviceVersion = await session.QueryBdVersionAsync(chip, cancellationToken).ConfigureAwait(false);
        Report(progress, "handshake", "rcm", null, null,
            $"Boot device version 0x{bootDeviceVersion:X8} reported.");
        await session.SyncAsync(chip, cancellationToken).ConfigureAwait(false);
        Report(progress, "handshake", "rcm", null, null, "RCM synchronised.");
    }

    /// <summary>
    /// Sends the miniloader over RCM and reports the payload byte counts the RCM session produces.
    /// <para>经 RCM 发送 miniloader，并转发 RCM 会话产生的负载字节数。</para>
    /// </summary>
    private async Task DownloadMiniloaderAsync(ICommTransport transport, TegraChip chip,
        IProgress<TegraFlashProgress>? progress, CancellationToken cancellationToken)
    {
        if (_options.MiniloaderPath is not { } path)
        {
            throw new InvalidOperationException("The plan carries no miniloader, so the chain cannot start.");
        }

        var image = await ReadPayloadAsync(path, MaximumMiniloaderLength, cancellationToken).ConfigureAwait(false);
        Report(progress, "miniloader", "miniloader", 0, image.Length,
            $"Downloading the miniloader '{Path.GetFileName(path)}' ({image.Length} bytes).");

        using var session = new TegraRcmSession(transport);
        var forward = progress is null ? null : new RcmProgressForwarder(progress);
        await session.DownloadMiniloaderAsync(image, chip, forward, cancellationToken).ConfigureAwait(false);
        Report(progress, "miniloader", "miniloader", image.Length, image.Length,
            "The miniloader download finished; the device is re-enumerating as an nv3p endpoint.");
    }

    /// <summary>
    /// Opens the re-enumerated endpoint and runs the nv3p half of the chain: the platform info, the BCT and the
    /// bootloader. <para>打开重新枚举出的端点并执行下载链的 nv3p 部分：平台信息、BCT 与 bootloader。</para>
    /// </summary>
    private async Task RunNv3pChainAsync(TegraChip chip, IProgress<TegraFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "nv3p", null, null, null, "Opening the device again as an nv3p endpoint.");
        var transport = await _openNv3pTransport(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The nv3p transport factory returned no transport.");
        using var session = new TegraNv3pSession(transport);

        var info = await session.GetPlatformInfoAsync(cancellationToken).ConfigureAwait(false);
        Report(progress, "nv3p", "platform-info", null, null,
            $"Platform info: chip {info.Chip}, boot device 0x{info.BootDevice:X}, operating mode "
            + $"0x{info.OperatingMode:X}.");
        if (!info.IsFlashable)
        {
            throw new InvalidOperationException(
                $"The device reports operating mode 0x{info.OperatingMode:X}, which accepts no download; only the "
                + "pre-production, developer and ODM modes do.");
        }

        // A device that re-enumerated under a different silicon revision means the run targets the wrong bundle, and
        // its BCT would describe the wrong memory straps.
        // 设备以不同芯片版本重新枚举意味着本次运行指向了错误的组合，其 BCT 会描述错误的内存跳线。
        if (info.Generation != chip)
        {
            throw new InvalidOperationException(
                $"The device reports chip {info.Chip}, which is {info.Generation}, but the run targets {chip}.");
        }

        await DownloadBctAsync(session, progress, cancellationToken).ConfigureAwait(false);
        if (_options.BootloaderBinaryPath is { } bootloaderPath)
        {
            await DownloadBootloaderAsync(session, bootloaderPath, progress, cancellationToken).ConfigureAwait(false);
        }

        if (_options.CompletionMode == TegraCompletionMode.Reboot)
        {
            Report(progress, "reboot", "bootloader", null, null,
                "The bootloader holds control; the device starts it on its own once the download completes.");
        }
    }

    /// <summary>
    /// Sends the BCT and, when the plan asks for it, asks the device to describe the BCT it stored again.
    /// <para>发送 BCT，并在计划要求时再次要求设备描述其存储的 BCT。</para>
    /// </summary>
    private async Task DownloadBctAsync(TegraNv3pSession session, IProgress<TegraFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_options.BctPath is not { } path)
        {
            throw new InvalidOperationException("The plan carries no BCT, so the bootloader could not be configured.");
        }

        var bct = await ReadPayloadAsync(path, TegraNv3pSession.MaximumBctLength, cancellationToken)
            .ConfigureAwait(false);
        Report(progress, "bct", "bct", 0, bct.Length,
            $"Downloading the BCT '{Path.GetFileName(path)}' ({bct.Length} bytes).");

        var forward = progress is null ? null : new Nv3pProgressForwarder(progress, "bct");
        await session.DownloadBctAsync(bct, forward, cancellationToken).ConfigureAwait(false);
        Report(progress, "bct", "bct", bct.Length, bct.Length, "The BCT download finished.");

        if (!_options.VerifyAfter)
        {
            return;
        }

        // The nv3p command set this layer speaks has no read-back of the BCT contents, so the strongest check
        // available is the length the device reports for the table it now holds.
        // 本层所讲的 nv3p 命令集没有回读 BCT 内容的能力，因此可用的最强检查是设备为当前持有的表上报的长度。
        var stored = await session.GetBctAsync(cancellationToken).ConfigureAwait(false);
        Report(progress, "verify", "bct", stored.Length, bct.Length,
            stored.Length == bct.Length
                ? $"The device reports the {stored.Length}-byte BCT it stored."
                : $"The device reports {stored.Length} stored BCT bytes, but {bct.Length} were sent.");
    }

    /// <summary>
    /// Sends the bootloader with the load and entry addresses the options carry.
    /// <para>以选项携带的加载地址与入口地址发送 bootloader。</para>
    /// </summary>
    private async Task DownloadBootloaderAsync(TegraNv3pSession session, string path,
        IProgress<TegraFlashProgress>? progress, CancellationToken cancellationToken)
    {
        var image = await ReadPayloadAsync(path, TegraNv3pSession.MaximumBootloaderLength, cancellationToken)
            .ConfigureAwait(false);
        var loadAddress = _options.BootloaderLoadAddress
            ?? throw new InvalidOperationException("A bootloader download needs BootloaderLoadAddress.");
        Report(progress, "bootloader", "bootloader", 0, image.Length,
            $"Downloading the bootloader '{Path.GetFileName(path)}' ({image.Length} bytes) to "
            + $"0x{loadAddress:X8}.");

        var forward = progress is null ? null : new Nv3pProgressForwarder(progress, "bootloader");
        await session.DownloadBootloaderAsync(image, loadAddress, _options.EffectiveBootloaderEntryAddress, forward,
            cancellationToken).ConfigureAwait(false);
        Report(progress, "bootloader", "bootloader", image.Length, image.Length,
            "The bootloader download finished.");
    }

    /// <summary>
    /// Reads a payload file into memory after checking its size against the layer limit, so a wrong file cannot drive
    /// an allocation the protocol would refuse anyway. <para>在按本层上限核对大小之后把负载文件读入内存，使错误的文件
    /// 无法驱动一次协议本就会拒绝的分配。</para>
    /// </summary>
    private static async Task<byte[]> ReadPayloadAsync(string path, int maximumLength,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path) is not { Exists: true } info || info.Length == 0)
        {
            throw new InvalidOperationException($"The payload file '{path}' does not exist or is empty.");
        }

        if (info.Length > maximumLength)
        {
            throw new InvalidOperationException(
                $"The payload file '{path}' is {info.Length} bytes, above the {maximumLength}-byte limit.");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static void Report(IProgress<TegraFlashProgress>? progress, string phase, string? target,
        long? completed, long? total, string message)
        => progress?.Report(new TegraFlashProgress(phase, target, completed, total, message));

    /// <summary>
    /// Forwards an RCM session's reports into the flash progress stream. The RCM record carries optional byte
    /// counts, so absent values stay absent instead of turning into zeros.
    /// <para>把 RCM 会话的报告转发进烧录进度流。RCM 记录的字节数可为空，因此缺失值保持缺失而不变成零。</para>
    /// </summary>
    private sealed class RcmProgressForwarder(IProgress<TegraFlashProgress> sink) : IProgress<TegraRcmProgress>
    {
        /// <inheritdoc/>
        public void Report(TegraRcmProgress value)
            => sink.Report(new TegraFlashProgress("miniloader", "miniloader", value.BytesCompleted,
                value.BytesTotal, value.Message));
    }

    /// <summary>
    /// Forwards an nv3p session's reports into the flash progress stream under the stage that owns the transfer.
    /// <para>把 nv3p 会话的报告转发进烧录进度流，并归入拥有该传输的阶段。</para>
    /// </summary>
    private sealed class Nv3pProgressForwarder(IProgress<TegraFlashProgress> sink, string phase)
        : IProgress<TegraNv3pProgress>
    {
        /// <inheritdoc/>
        public void Report(TegraNv3pProgress value)
            => sink.Report(new TegraFlashProgress(phase, phase, value.BytesCompleted, value.BytesTotal,
                value.Message));
    }
}