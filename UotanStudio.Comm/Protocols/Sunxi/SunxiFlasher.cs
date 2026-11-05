namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// Executes a Sunxi flash plan against a FES session, following the reference flashing flow: erase by mode,
/// table, partitions with flash access switched on, the boot images selected from the service answers, per-write
/// CRC verification, and the completion tool mode at the end. Partitions the package does not carry and
/// partitions outside the selected scope are reported as skipped; nothing is zero-filled.
/// <para>对 FES 会话执行全志烧录计划，遵循参考刷机流程：按模式擦除、分区表、在打开闪存访问后写分区、按服务应答
/// 选择引导镜像、逐次 CRC 校验，最后切换完成用的工具模式。包内不存在的分区与不在所选范围内的分区按跳过上报；
/// 不做任何零填充。</para>
/// </summary>
public sealed class SunxiFlasher
{
    /// <summary>Status-verification attempts before an inconclusive verification is reported as such, matching
    /// the reference retry budget. <para>无法判定的校验在上报前的重试次数上限，与参考的重试预算一致。</para></summary>
    private const int VerifyAttempts = 5;

    /// <summary>Delay between verification attempts, matching the reference client's settle time.
    /// <para>两次校验尝试之间的等待，与参考客户端的稳定时间一致。</para></summary>
    private static readonly TimeSpan VerifyRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly string[] UserDataPartitions = ["udisk", "private", "reserve"];

    private readonly SunxiFlashPlan _plan;
    private readonly SunxiFlashOptions _options;

    /// <summary>
    /// Creates a flasher for the given plan and options.
    /// <para>为给定计划与选项创建烧录器。</para>
    /// </summary>
    /// <param name="plan">Loaded flash plan; the flasher consumes it without owning it.
    /// <para>已加载的烧录计划；烧录器只消费而不接管其所有权。</para></param>
    /// <param name="options">Flash options; the confirmation phrase is validated before any I/O.
    /// <para>烧录选项；确认短语在任何 I/O 之前校验。</para></param>
    public SunxiFlasher(SunxiFlashPlan plan, SunxiFlashOptions options)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Runs every selected plan step against the session and switches the completion tool mode.
    /// <para>对会话执行选定的计划步骤，并切换完成用的工具模式。</para>
    /// </summary>
    /// <param name="session">Open FES session. <para>已打开的 FES 会话。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels between steps and between transfer chunks.
    /// <para>在步骤之间与传输分块之间取消。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The options are not confirmed. <para>选项未确认。</para></exception>
    /// <exception cref="InvalidDataException">The package carries no boot image for the service's boot mode and
    /// storage type. <para>包内没有与服务启动模式和存储类型匹配的引导镜像。</para></exception>
    /// <exception cref="SunxiEfexException">The device rejected a stage or reported an authoritative CRC
    /// mismatch. <para>设备拒绝了某个阶段，或上报了权威的 CRC 不一致。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task ExecuteAsync(SunxiFesSession session, IProgress<SunxiFlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        _options.Validate();

        var secureWord = await session.QuerySecureAsync(cancellationToken).ConfigureAwait(false);
        var storageWord = await session.QueryStorageAsync(cancellationToken).ConfigureAwait(false);
        var bootMode = SunxiBootImageSelector.BootFileModeFromWord(secureWord);
        var storage = SunxiBootImageSelector.StorageKindFromWord(storageWord);
        var steps = SelectSteps(bootMode, storage, progress);

        foreach (var missing in _plan.MissingPartitions.Where(IsSelectedPartition))
        {
            progress?.Report(new SunxiFlashProgress("skip", missing, null, null,
                $"No package file for partition '{missing}'; skipped."));
        }

        if (_options.Mode != SunxiFlashMode.Partition)
        {
            progress?.Report(new SunxiFlashProgress("erase", null, null, null,
                $"Erasing storage with flag 0x{(uint)_options.EraseFlag:X}."));
            await session.EraseAsync(_options.EraseFlag, cancellationToken, SunxiFesSession.EraseTimeoutMilliseconds)
                .ConfigureAwait(false);
            await VerifyEraseAsync(session, progress, cancellationToken).ConfigureAwait(false);
        }

        var flashOn = false;
        try
        {
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Kind == SunxiFlashStepKind.Partition && !flashOn)
                {
                    await session.SetFlashAsync(0, on: true, cancellationToken).ConfigureAwait(false);
                    flashOn = true;
                }
                else if (step.Kind != SunxiFlashStepKind.Partition && flashOn)
                {
                    await TurnOffFlashAsync(session, progress, cancellationToken).ConfigureAwait(false);
                    flashOn = false;
                }

                if (step.Kind == SunxiFlashStepKind.Partition)
                {
                    await DownloadPartitionAsync(session, step, progress, cancellationToken).ConfigureAwait(false);
                    await VerifyPartitionAsync(session, step, progress, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await DownloadTaggedAsync(session, step, progress, cancellationToken).ConfigureAwait(false);
                await VerifyTaggedAsync(session, step, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (flashOn)
            {
                await TurnOffFlashAsync(session, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new SunxiFlashProgress("tool", null, null, null,
            $"Requesting tool mode {_options.CompletionMode}."));
        await session.SetToolModeAsync(SunxiToolMode.Normal, _options.CompletionMode, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new SunxiFlashProgress("done", null, null, null, "Flash run completed."));
    }

    private List<SunxiFlashStep> SelectSteps(SunxiBootFileMode? bootMode, SunxiStorageKind? storage,
        IProgress<SunxiFlashProgress>? progress)
    {
        var steps = new List<SunxiFlashStep>();
        var hasBoot0 = false;
        var hasBoot1 = false;
        foreach (var step in _plan.Steps)
        {
            if (step.Kind == SunxiFlashStepKind.Mbr)
            {
                steps.Add(step);
                continue;
            }

            if (step.Kind == SunxiFlashStepKind.Partition)
            {
                if (IsSelectedPartition(step.Name))
                {
                    steps.Add(step);
                }
                else
                {
                    progress?.Report(new SunxiFlashProgress("skip", step.Name, null, null,
                        $"Partition '{step.Name}' is outside the selected scope; skipped."));
                }

                continue;
            }

            if (step.BootRole is not { } role
                || !SunxiBootImageSelector.Matches(role, bootMode, storage))
            {
                continue;
            }

            steps.Add(step);
            hasBoot0 |= IsBoot0Role(role);
            hasBoot1 |= IsBoot1Role(role);
        }

        if (!hasBoot0)
        {
            throw new InvalidDataException(
                $"The package carries no boot0 image for boot mode '{bootMode?.ToString() ?? "unknown"}' and "
                + $"storage '{storage?.ToString() ?? "unknown"}'.");
        }

        if (bootMode is not null && !hasBoot1)
        {
            throw new InvalidDataException(
                $"The package carries no boot1 image for boot mode '{bootMode}' and "
                + $"storage '{storage?.ToString() ?? "unknown"}'.");
        }

        return steps;
    }

    private bool IsSelectedPartition(string name)
    {
        if (_options.Mode == SunxiFlashMode.KeepData
            && UserDataPartitions.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return _options.Mode != SunxiFlashMode.Partition
            || _options.Partitions.Count == 0
            || _options.Partitions.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBoot0Role(SunxiBootImageRole role) => role is
        SunxiBootImageRole.Boot0NormalNand or SunxiBootImageRole.Boot0NormalCard
        or SunxiBootImageRole.Boot0NormalNor or SunxiBootImageRole.Boot0NormalUfs
        or SunxiBootImageRole.Boot0TocDefault or SunxiBootImageRole.Boot0TocSd
        or SunxiBootImageRole.Boot0TocNand or SunxiBootImageRole.Boot0TocNor
        or SunxiBootImageRole.Boot0TocUfs;

    private static bool IsBoot1Role(SunxiBootImageRole role) => role is
        SunxiBootImageRole.Boot1Normal or SunxiBootImageRole.Boot1Toc
        or SunxiBootImageRole.Boot1Package or SunxiBootImageRole.Boot1PackageNor;

    private async Task TurnOffFlashAsync(SunxiFesSession session, IProgress<SunxiFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.SetFlashAsync(0, on: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            progress?.Report(new SunxiFlashProgress("flash", null, null, null,
                $"Turning flash access off failed: {exception.Message}"));
        }
    }

    private async Task DownloadPartitionAsync(SunxiFesSession session, SunxiFlashStep step,
        IProgress<SunxiFlashProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new SunxiFlashProgress("download", step.Name, 0, step.ByteCount,
            $"Downloading partition '{step.Name}' at sector {step.FirstSector} ({step.ByteCount} bytes)."));
        var sectorAddressed = !SunxiEfexFrames.IsByteAddressed(step.Tag);
        var buffer = new byte[SunxiEfexConstants.MaximumTransfer];
        var reported = 0L;
        var offset = 0L;
        var current = step.FirstSector;
        while (offset < step.ByteCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (int)Math.Min(buffer.Length, step.ByteCount - offset);
            var last = offset + length == step.ByteCount;
            var flags = (uint)step.Tag | (last ? SunxiEfexConstants.TransferFinishTag : 0u);
            var written = await _plan.ExtractSegmentAsync(step, offset, buffer, cancellationToken)
                .ConfigureAwait(false);
            await session.DownloadChunkAsync(current, (uint)written, flags, buffer.AsMemory(0, written),
                cancellationToken).ConfigureAwait(false);
            current = checked(current + (uint)(sectorAddressed ? written / SunxiEfexConstants.SectorSize : written));
            offset += written;
            if (offset - reported >= 4 * 1024 * 1024 || last)
            {
                reported = offset;
                progress?.Report(new SunxiFlashProgress("download", step.Name, offset, step.ByteCount,
                    $"Downloading partition '{step.Name}': {offset} of {step.ByteCount} bytes."));
            }
        }
    }

    private async Task DownloadTaggedAsync(SunxiFesSession session, SunxiFlashStep step,
        IProgress<SunxiFlashProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new SunxiFlashProgress("download", step.Name, null, step.ByteCount,
            $"Downloading {step.Name} ({step.ByteCount} bytes)."));
        var data = _plan.ReadTaggedEntry(step, cancellationToken);
        await session.DownloadAsync(step.FirstSector, data, step.Tag, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyEraseAsync(SunxiFesSession session, IProgress<SunxiFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= VerifyAttempts; attempt++)
        {
            await Task.Delay(VerifyRetryDelay, cancellationToken).ConfigureAwait(false);
            var result = await session.VerifyStatusAsync((uint)SunxiFesDataTag.Erase, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new SunxiFlashProgress("verify", "erase", null, null,
                $"Verify erase attempt {attempt}: flag 0x{result.Flag:X8}, media 0x{result.MediaCrc:X8}."));
            if (result.IsStatusOk)
            {
                return;
            }
        }

        progress?.Report(new SunxiFlashProgress("verify", "erase", null, null,
            $"Erase verification stayed inconclusive after {VerifyAttempts} attempts; continuing."));
    }

    private async Task VerifyTaggedAsync(SunxiFesSession session, SunxiFlashStep step,
        IProgress<SunxiFlashProgress>? progress, CancellationToken cancellationToken)
    {
        if (step.Kind == SunxiFlashStepKind.Mbr)
        {
            for (var attempt = 1; attempt <= VerifyAttempts; attempt++)
            {
                await Task.Delay(VerifyRetryDelay, cancellationToken).ConfigureAwait(false);
                var result = await session.VerifyStatusAsync((uint)step.Tag, cancellationToken).ConfigureAwait(false);
                progress?.Report(new SunxiFlashProgress("verify", step.Name, null, null,
                    $"Verify {step.Name} attempt {attempt}: flag 0x{result.Flag:X8}, media 0x{result.MediaCrc:X8}."));
                if (result.IsStatusOk)
                {
                    return;
                }
            }

            progress?.Report(new SunxiFlashProgress("verify", step.Name, null, null,
                $"Verify {step.Name} stayed inconclusive after {VerifyAttempts} attempts; continuing."));
            return;
        }

        var boot = await session.VerifyStatusAsync((uint)step.Tag, cancellationToken).ConfigureAwait(false);
        if (!boot.IsCrcValid)
        {
            progress?.Report(new SunxiFlashProgress("verify", step.Name, null, null,
                $"Verify {step.Name} reported flag 0x{boot.Flag:X8}; the medium did not confirm the write."));
        }
    }

    private async Task VerifyPartitionAsync(SunxiFesSession session, SunxiFlashStep step,
        IProgress<SunxiFlashProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_options.VerifyAfter)
        {
            return;
        }

        var result = await session.VerifyValueAsync(step.FirstSector, (ulong)step.ByteCount, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new SunxiFlashProgress("verify", step.Name, null, null,
            $"Verify '{step.Name}': flag 0x{result.Flag:X8}, fes 0x{result.FesCrc:X8}, media 0x{result.MediaCrc:X8}."));
        if (!result.IsCrcValid)
        {
            progress?.Report(new SunxiFlashProgress("verify", step.Name, null, null,
                $"Verify '{step.Name}' did not return a valid CRC flag; continuing."));
            return;
        }

        if (result.FesCrc != result.MediaCrc)
        {
            throw new SunxiEfexException(
                $"The CRC verification of '{step.Name}' reported a mismatch (flag 0x{result.Flag:X8}, "
                + $"fes 0x{result.FesCrc:X8}, media 0x{result.MediaCrc:X8}).");
        }
    }
}