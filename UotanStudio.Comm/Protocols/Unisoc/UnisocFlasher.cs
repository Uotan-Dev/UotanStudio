namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// One resolved device partition a flash pass can address: GPT partitions carry confirmed byte extents, native
/// table partitions only carry the device's own size value whose unit the device defines.
/// <para>刷写流程可寻址的一个已解析设备分区：GPT 分区携带确认的字节范围，原生表分区只携带由设备定义单位的
/// 原始大小值。</para>
/// </summary>
/// <param name="Name">Partition name as the device spells it. <para>设备拼写形式的分区名。</para></param>
/// <param name="Offset">Absolute byte offset, confirmed through GPT; <see langword="null"/> on the native table.
/// <para>绝对字节偏移，经 GPT 确认；原生表为 <see langword="null"/>。</para></param>
/// <param name="Length">Byte length, confirmed through GPT; <see langword="null"/> on the native table.
/// <para>字节长度，经 GPT 确认；原生表为 <see langword="null"/>。</para></param>
/// <param name="NativeSizeValue">Raw native size field, when the table came from the native path.
/// <para>原生路径下表的原始大小字段。</para></param>
public sealed record UnisocPartitionTarget(string Name, long? Offset, long? Length, uint? NativeSizeValue = null);

/// <summary>
/// Executes one flash pass against a connected Unisoc session: brings the link up to FDL2 with the plan's loaders,
/// resolves device partitions, applies the erase mode, writes the selected images with progress, and runs the
/// completion mode. NV partitions are never written or erased.
/// <para>对已连接的紫光展锐会话执行一次刷写流程：用计划的加载器把链路提升到 FDL2、解析设备分区、应用擦除模式、
/// 带进度写入所选镜像并执行收尾模式。NV 分区永不写入或擦除。</para>
/// </summary>
public sealed class UnisocFlasher
{
    private readonly UnisocDeviceSession _session;
    private readonly IProgress<UnisocFlashProgress>? _progress;

    /// <summary>Creates a flasher bound to one session. <para>创建绑定到一个会话的刷写器。</para></summary>
    /// <param name="session">The connected session. <para>已连接的会话。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度接收器。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    public UnisocFlasher(UnisocDeviceSession session, IProgress<UnisocFlashProgress>? progress = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _progress = progress;
    }

    /// <summary>
    /// Resolves the partitions the device currently exposes: the validated <c>user_partition</c> GPT when present,
    /// otherwise the native table with its device-defined size units.
    /// <para>解析设备当前暴露的分区：有校验通过的 <c>user_partition</c> GPT 时用它，否则用原生表（其大小单位由
    /// 设备定义）。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the resolution. <para>取消解析。</para></param>
    /// <returns>The resolved targets in table order. <para>按表顺序解析出的目标。</para></returns>
    /// <exception cref="UnisocProtocolException">The device refused; the session has faulted.
    /// <para>设备拒绝；会话已失效。</para></exception>
    public async Task<IReadOnlyList<UnisocPartitionTarget>> ResolvePartitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var storage = RequireStorage();
        var gpt = await storage.ReadGptAsync(UnisocStorageSession.DefaultGptWindowBytes, cancellationToken)
            .ConfigureAwait(false);
        if (gpt is not null)
        {
            var targets = new List<UnisocPartitionTarget>(gpt.Table.Partitions.Count);
            foreach (var partition in gpt.Table.Partitions)
            {
                var (offset, length) = gpt.ExtentOf(partition);
                targets.Add(new UnisocPartitionTarget(partition.Name, offset, length));
            }

            return targets;
        }

        var records = await storage.ReadPartitionTableAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Where(record => !record.IsRemaining)
            .Select(record => new UnisocPartitionTarget(record.Name, null, null, record.SizeValue))];
    }

    /// <summary>
    /// Runs the flash pass. The options are validated before any device I/O; a cancelled pass leaves the device in
    /// whatever state the transfers reached, because unknown results are never retried.
    /// <para>执行刷写流程。选项在任何设备 I/O 之前校验；被取消的流程把设备留在传输已到达的状态，因为未知结果
    /// 从不重试。</para>
    /// </summary>
    /// <param name="plan">The loaded flash plan. <para>已加载的刷写计划。</para></param>
    /// <param name="options">Flash options with confirmations. <para>带确认的刷写选项。</para></param>
    /// <param name="selectedNames">Partition names to write; <see langword="null"/> writes every plan image that
    /// matches a device partition.
    /// <para>要写入的分区名；<see langword="null"/> 表示写入计划中所有与设备分区匹配的镜像。</para></param>
    /// <param name="cancellationToken">Cancels the pass. <para>取消流程。</para></param>
    /// <exception cref="InvalidOperationException">The options are unconfirmed or the loaders are missing for the
    /// current stage. <para>选项未确认，或当前阶段缺少加载器。</para></exception>
    /// <exception cref="UnisocProtocolException">Any exchange failed; the session has faulted.
    /// <para>任一交互失败；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task ExecuteAsync(UnisocFlashPlan plan, UnisocFlashOptions options,
        IReadOnlyList<string>? selectedNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        var confirmation = options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureFdl2Async(plan, cancellationToken).ConfigureAwait(false);
        var storage = RequireStorage();
        var targets = await ResolvePartitionsAsync(cancellationToken).ConfigureAwait(false);
        var targetsByName = targets
            .GroupBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var selected = selectedNames is null
            ? plan.Images.Select(image => image.Name).ToArray()
            : [.. selectedNames];
        if (selected.Length == 0)
        {
            Report(new UnisocFlashProgress(UnisocFlashPhase.Done, null, 0, 0));
            return;
        }

        if (options.Mode == UnisocFlashMode.FullErase)
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.Name.Contains("nv", StringComparison.OrdinalIgnoreCase))
                {
                    Report(new UnisocFlashProgress(UnisocFlashPhase.Skip, target.Name, 0, 0,
                        "NV partitions are never erased."));
                    continue;
                }

                Report(new UnisocFlashProgress(UnisocFlashPhase.Erase, target.Name, 0, 0));
                await storage.ErasePartitionAsync(target.Name, confirmation, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (var image in plan.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selected.Contains(image.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!targetsByName.TryGetValue(image.Name, out var target))
            {
                Report(new UnisocFlashProgress(UnisocFlashPhase.Skip, image.Name, 0, image.Length,
                    "The device reports no partition with this name."));
                continue;
            }

            if (options.Mode == UnisocFlashMode.PartitionErase)
            {
                Report(new UnisocFlashProgress(UnisocFlashPhase.Erase, image.Name, 0, image.Length));
                await storage.ErasePartitionAsync(image.Name, confirmation, cancellationToken)
                    .ConfigureAwait(false);
            }

            Report(new UnisocFlashProgress(UnisocFlashPhase.Flash, image.Name, 0, image.Length));
            await using var source = plan.OpenImage(image);
            await storage.WritePartitionAsync(image.Name, source, image.Length, confirmation,
                cancellationToken, new ReportingProgress(this, image.Name, image.Length)).ConfigureAwait(false);
            Report(new UnisocFlashProgress(UnisocFlashPhase.Flash, image.Name, image.Length, image.Length));
        }

        switch (options.Completion)
        {
            case UnisocCompletionMode.Reboot:
                await _session.NormalResetAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UnisocCompletionMode.PowerOff:
                await _session.PowerOffAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        Report(new UnisocFlashProgress(UnisocFlashPhase.Done, null, 0, 0));
    }

    private async Task EnsureFdl2Async(UnisocFlashPlan plan, CancellationToken cancellationToken)
    {
        Report(new UnisocFlashProgress(UnisocFlashPhase.Connect, null, 0, 0));
        switch (_session.Stage)
        {
            case UnisocBootStage.BootRom:
                await using (var first = plan.OpenEntry(plan.Loaders.FirstStage))
                {
                    await _session.UploadFdl1Async(first, plan.Loaders.FirstStageAddress, cancellationToken)
                        .ConfigureAwait(false);
                }

                await UploadFdl2Async(plan, cancellationToken).ConfigureAwait(false);
                break;
            case UnisocBootStage.Fdl1:
                await UploadFdl2Async(plan, cancellationToken).ConfigureAwait(false);
                break;
            case UnisocBootStage.Fdl2:
                break;
            default:
                throw new UnisocProtocolException("The Unisoc link has no identified loader stage.");
        }
    }

    private async Task UploadFdl2Async(UnisocFlashPlan plan, CancellationToken cancellationToken)
    {
        await using var second = plan.OpenEntry(plan.Loaders.SecondStage);
        await _session.UploadFdl2Async(second, plan.Loaders.SecondStageAddress, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class ReportingProgress(UnisocFlasher owner, string name, long total) : IProgress<long>
    {
        public void Report(long value) => owner._progress?.Report(
            new UnisocFlashProgress(UnisocFlashPhase.Flash, name, value, total));
    }

    private UnisocStorageSession RequireStorage()
    {
        try
        {
            return _session.OpenStorage();
        }
        catch (InvalidOperationException)
        {
            throw new UnisocProtocolException("Storage operations require an FDL2 connection.");
        }
    }

    private void Report(UnisocFlashProgress progress) => _progress?.Report(progress);
}