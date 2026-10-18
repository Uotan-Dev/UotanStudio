using System.Diagnostics;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// The phase of a flash bundle operation.
/// <para>刷写包操作的阶段。</para>
/// </summary>
public enum FlashBundlePhase
{
    /// <summary>
    /// Parsing the rawprogram / patch / UFS-provision XML files and initializing the session.
    /// <para>解析 rawprogram / patch / UFS 配置 XML 文件并初始化会话。</para>
    /// </summary>
    Parsing,

    /// <summary>
    /// Programming (flashing) image files.
    /// <para>编程（刷写）镜像文件。</para>
    /// </summary>
    Programming,

    /// <summary>
    /// Erasing rawprogram regions that carry no filename.
    /// <para>擦除 rawprogram 中无文件名的区域。</para>
    /// </summary>
    Erasing,

    /// <summary>
    /// Applying patch entries from the patch XML.
    /// <para>应用 patch XML 中的补丁条目。</para>
    /// </summary>
    Patching,

    /// <summary>
    /// Wiping leading sectors with zeros (program-zeros).
    /// <para>以零字节擦写前导扇区。</para>
    /// </summary>
    Wiping,

    /// <summary>
    /// Provisioning UFS storage.
    /// <para>配置 UFS 存储。</para>
    /// </summary>
    UfsProvisioning,

    /// <summary>
    /// Reading programmed entries back and comparing them with their sources.
    /// <para>回读已编程条目并与源文件比对。</para>
    /// </summary>
    Verifying,

    /// <summary>
    /// The operation completed.
    /// <para>操作已完成。</para>
    /// </summary>
    Completed,

    /// <summary>
    /// The operation failed with a fatal error.
    /// <para>操作因致命错误而失败。</para>
    /// </summary>
    Error
}

/// <summary>
/// A progress notification raised during a flash bundle operation.
/// <para>刷写包操作期间产生的进度通知。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FlashBundleProgress"/> class.
/// <para>初始化 <see cref="FlashBundleProgress"/> 类的新实例。</para>
/// </remarks>
/// <param name="phase">The flash bundle phase this notification reports.
/// <para>此通知所报告的刷写包阶段。</para></param>
/// <param name="message">Human-readable status text describing the current item or phase.
/// <para>描述当前条目或阶段的人类可读状态文本。</para></param>
/// <param name="current">Zero-based index of the item being processed within the phase; <c>0</c> for phases without per-item progress.
/// <para>阶段内正在处理条目的从零开始索引；不报告逐项进度的阶段为 <c>0</c>。</para></param>
/// <param name="total">Item count for the phase; <c>0</c> when the phase has no enumerable items.
/// <para>阶段条目总数；阶段无可枚举条目时为 <c>0</c>。</para></param>
public sealed class FlashBundleProgress(FlashBundlePhase phase, string message, int current, int total)
{
    /// <summary>Gets the current phase. <para>当前阶段。</para></summary>
    /// <value>The flash bundle phase the orchestrator was in when this notification was raised.
    /// <para>产生此通知时编排器所处的刷写包阶段。</para></value>
    public FlashBundlePhase Phase { get; } = phase;

    /// <summary>Gets a human-readable message. <para>人类可读的消息。</para></summary>
    /// <value>Status text describing the current item or phase; device-facing messages carry an inline Chinese translation.
    /// <para>描述当前条目或阶段的状态文本；面向设备的消息附带内联中文翻译。</para></value>
    public string Message { get; } = message;

    /// <summary>Gets the zero-based index of the current item. <para>当前条目从 0 开始的索引。</para></summary>
    /// <value>Index of the item being processed within the phase; 0 for phases that report no per-item progress.
    /// <para>阶段内正在处理的条目索引；不报告逐项进度的阶段为 0。</para></value>
    public int Current { get; } = current;

    /// <summary>Gets the total number of items in the current phase. <para>当前阶段条目总数。</para></summary>
    /// <value>Item count for the phase; 0 when the phase has no enumerable items.
    /// <para>阶段条目总数；阶段无可枚举条目时为 0。</para></value>
    public int Total { get; } = total;

    /// <summary>
    /// Returns a string representation of the progress.
    /// <para>返回进度的字符串表示。</para>
    /// </summary>
    /// <returns>A string describing the progress. <para>描述进度的字符串。</para></returns>
    public override string ToString() => $"FlashBundleProgress[{Phase}] {Message} ({Current}/{Total})";
}

/// <summary>Orchestrates a Qualcomm EDL flash bundle: parses rawprogram0.xml / patch0.xml / UFS-provision XML files and dispatches the individual Program, Erase, Patch, Wipe and UFS provisioning operations through the Firehose protocol.
/// <para>编排 Qualcomm EDL 刷写包：解析 rawprogram0.xml / patch0.xml / UFS 配置 XML 文件， 并通过 Firehose 协议逐项派发 Program、Erase、Patch、Wipe 与 UFS 配置操作。</para></summary>
/// <remarks>This orchestrator operates directly on an <see cref="EdlSession"/> (which owns the Firehose transport) instead of a driver abstraction. Skip-block SHA256 verification, NAND geometry back-fill and the bootable-drive step are intentionally not implemented here.
/// <para>本编排器直接基于 <see cref="EdlSession"/>（其持有 Firehose 传输）操作，不使用驱动抽象。 skip-block SHA256 校验、NAND 几何回填与可启动驱动器步骤在此有意未实现。</para></remarks>
/// <remarks>Initializes a new instance of the <see cref="FlashBundleOrchestrator"/> class.
/// <para>初始化 <see cref="FlashBundleOrchestrator"/> 类的新实例。</para></remarks>
/// <param name="session">The EDL session used for all device operations.
/// <para>用于所有设备操作的 EDL 会话。</para></param>
public sealed partial class FlashBundleOrchestrator(EdlSession session)
{
    private readonly EdlSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>Runs a flash bundle from a rawprogram file (and optional patch file), resolving image paths against <paramref name="imageDir"/>. UFS provisioning XML is auto-discovered in the image directory.
    /// <para>从 rawprogram 文件（及可选的 patch 文件）运行刷写包，镜像路径按 <paramref name="imageDir"/> 解析。UFS 配置 XML 在镜像目录中自动发现。</para></summary>
    /// <param name="rawprogramPath">The rawprogram XML file path.
    /// <para>rawprogram XML 文件路径。</para></param>
    /// <param name="patchPath">The optional patch XML file path.
    /// <para>可选的 patch XML 文件路径。</para></param>
    /// <param name="imageDir">The base directory for image files; defaults to the rawprogram's directory.
    /// <para>镜像文件基础目录；默认为 rawprogram 所在目录。</para></param>
    /// <param name="progress">Optional progress reporter.<para>可选的进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The flash bundle result.<para>刷写包结果。</para></returns>
    public async Task<FlashBundleResult> FlashBundleAsync(
        string rawprogramPath,
        string? patchPath,
        string? imageDir,
        IProgress<FlashBundleProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var options = new FlashBundleOptions
        {
            RawProgramPath = rawprogramPath,
            PatchPath = patchPath,
            ImageDirectory = imageDir ?? Path.GetDirectoryName(Path.GetFullPath(rawprogramPath)) ?? string.Empty,
            UfsProvisionPath = null
        };

        return await FlashBundleAsync(options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a flash bundle from the specified options.
    /// <para>根据指定选项运行刷写包。</para>
    /// </summary>
    /// <remarks>
    /// <para>Options and selection collections are captured before progress callbacks or awaits.
    /// Callers must not mutate collections during that initial synchronous capture. Device phases hold
    /// one Firehose operation lease. Progress and log callbacks must not synchronously wait for another
    /// operation on the same session.</para>
    /// <para>选项与选择集合在进度回调或等待之前捕获，调用方不得在那段初始同步捕获期间改动集合。
    /// 设备阶段持有一个 Firehose 操作租约；进度与日志回调不得同步等待同一会话上的另一个操作。</para>
    /// </remarks>
    /// <param name="options">The flash bundle options. <para>刷写包选项。</para></param>
    /// <param name="progress">Optional progress reporter. <para>可选的进度报告器。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash bundle result. <para>刷写包结果。</para></returns>
    public Task<FlashBundleResult> FlashBundleAsync(
        FlashBundleOptions options,
        IProgress<FlashBundleProgress>? progress,
        CancellationToken cancellationToken = default)
        => RunBundleAsync(options, progress, false, cancellationToken);

    /// <summary>Validates sources and device ranges without program, erase, patch, wipe or provisioning.
    /// <para>在不执行 program、erase、patch、wipe 或 provisioning 的情况下校验源文件与设备范围。</para>Session initialization and storage queries are allowed and require caller authorization. This observation does not retain a prepared plan; execution must validate again.<para>允许会话初始化与存储查询，且需要调用方授权。该观察不会保留已准备好的计划；执行时必须 重新校验。</para></summary>
    /// <param name="options">Selected bundle inputs and policy, captured at entry.
    /// <para>入口处捕获的所选包输入与策略。</para></param>
    /// <param name="cancellationToken">Cancellation for preflight and device queries.
    /// <para>用于预检与设备查询的取消令牌。</para></param>
    /// <returns>The validation result; processed storage-operation counts remain zero.
    /// <para>校验结果；已处理存储操作的计数保持为 0。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    public Task<FlashBundleResult> PreflightDeviceAsync(FlashBundleOptions options,
        CancellationToken cancellationToken = default)
        => RunBundleAsync(options, null, true, cancellationToken);

    private async Task<FlashBundleResult> RunBundleAsync(FlashBundleOptions options,
        IProgress<FlashBundleProgress>? progress, bool devicePreflight, CancellationToken cancellationToken,
        PreparedBundle? prepared = null, bool operationOwned = false)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options = options.Snapshot();
        if (devicePreflight)
        {
            options.DryRun = true;
        }
        var stopwatch = Stopwatch.StartNew();
        var result = new FlashBundleResult { StartedAtUtc = DateTimeOffset.UtcNow };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ownedPreparation = prepared is null ? PrepareBundle(options, progress, cancellationToken) : null;
            var input = prepared ?? ownedPreparation!;
            var filteredPrograms = input.Programs;
            var filteredPatches = input.Patches;
            var filteredWipes = input.Wipes;
            var ufsProvision = input.Ufs;
            var sources = input.Sources;
            if (input.AbsentPrograms is { Count: > 0 } absentPrograms)
            {
                result.ProgramEntriesAbsent = absentPrograms.Count;
                foreach (var entry in absentPrograms)
                {
                    result.Warnings.Add($"Program entry '{(string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label)}' was not programmed because its source image '{entry.Filename}' is absent from the package.");
                }
            }

            var hasSectorCommands = (!options.SkipProgram && filteredPrograms.Count > 0)
                || filteredPatches is { Count: > 0 };
            var preflightSectorSize = FlashBundlePreflight.SelectSectorSize(filteredPrograms, filteredPatches,
                options, RequireFirehose().Configuration.SectorSize);
            List<EdlPhysicalRange>? storageRanges = null;
            if (preflightSectorSize > 0)
            {
                storageRanges = FlashBundlePreflight.Validate(filteredPrograms, filteredPatches, options,
                    preflightSectorSize, cancellationToken, sources, options.Slot, filteredWipes);
            }

            if (options.DryRun && !devicePreflight)
            {
                if (hasSectorCommands && preflightSectorSize == 0)
                {
                    throw new EdlException(EdlErrorCode.InvalidParameter,
                        "Offline preflight requires a sector size in the manifest, configuration or explicit override.");
                }

                RecordWipePreviews(filteredWipes, options, result, geometries: null);
                result.ProgramEntriesSkipped = filteredPrograms.Count;
                result.PatchEntriesSkipped = filteredPatches?.Count ?? 0;
                result.IsSuccess = true;
                RaiseProgress(progress, FlashBundlePhase.Completed, "Offline bundle inspection completed.", 1, 1);
                return result;
            }

            var firehose = RequireFirehose();
            using var operation = operationOwned ? null : await firehose.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            if (ufsProvision is not null && ufsProvision.Slot.GetValueOrDefault() != (options.Slot ?? firehose.Configuration.Slot))
            {
                throw new InvalidDataException("The selected UFS slot changed while the bundle waited for the device.");
            }

            if (options.MaxProgramChunkBytes is { } chunkBytes)
            {
                if (chunkBytes is < 512 or > 16777216 || (chunkBytes & 511) != 0)
                {
                    throw new EdlException(EdlErrorCode.InvalidParameter,
                        "The write-chunk override must be a 512-byte multiple between 512 and 16777216 bytes.");
                }

                firehose.Configuration.MaxProgramChunkBytes = chunkBytes;
            }

            if (options.NandPagesPerBlock is { } pagesPerBlock)
            {
                if (pagesPerBlock is < 0 or > 65535)
                {
                    throw new EdlException(EdlErrorCode.InvalidParameter,
                        "The NAND pages-per-block override must be 0 or a value between 1 and 65535.");
                }

                firehose.Configuration.NandPagesPerBlock = pagesPerBlock;
            }

            RaiseProgress(progress, FlashBundlePhase.Parsing, "Initializing EDL session... 初始化 EDL 会话...", 0, 0);
            await _session.InitializeOwnedAsync(null, cancellationToken).ConfigureAwait(false);
            if (options.Slot.HasValue)
            {
                firehose.Configuration.Slot = options.Slot.Value;
            }

            if (options.OverrideSectorSize is > 0)
            {
                firehose.Configuration.SectorSize = options.OverrideSectorSize.Value;
            }

            var sectorSize = FlashBundlePreflight.SelectSectorSize(filteredPrograms, filteredPatches, options, firehose.Configuration.SectorSize);
            if (hasSectorCommands && sectorSize == 0)
            {
                throw new EdlException(EdlErrorCode.InvalidParameter, "Cannot determine the sector size for bundle preflight.");
            }

            if (sectorSize != preflightSectorSize)
            {
                storageRanges = FlashBundlePreflight.Validate(filteredPrograms, filteredPatches, options,
                    sectorSize, cancellationToken, sources, options.Slot, filteredWipes);
            }

            firehose.Configuration.SectorSize = sectorSize;
            await FlashBundlePreflight.ValidateDeviceRangesOwnedAsync(firehose, storageRanges ?? [], cancellationToken).ConfigureAwait(false);
            result.Record(new FlashItemEvidence
            {
                Kind = FlashBundleItemKind.Preflight,
                Outcome = CommandOutcomeKind.Completed,
                FileName = options.RawProgramPath is { Length: > 0 } rawPath ? Path.GetFileName(rawPath) : null,
                AcknowledgedBytes = 0,
                Label = $"ranges={storageRanges?.Count ?? 0}"
            });
            sources?.ValidateLengths(cancellationToken);
            if (devicePreflight)
            {
                RecordWipePreviews(filteredWipes, options, result, await CollectWipeGeometriesAsync(
                    firehose, filteredWipes, result, cancellationToken).ConfigureAwait(false));
                result.ProgramEntriesSkipped = filteredPrograms.Count;
                result.PatchEntriesSkipped = filteredPatches?.Count ?? 0;
                result.IsSuccess = true;
                return result;
            }
            if (!await ExecuteBundleAsync(options, progress, cancellationToken, result, firehose, filteredPrograms,
                    filteredPatches, filteredWipes, ufsProvision, sources, sectorSize).ConfigureAwait(false))
            {
                return result;
            }

            RaiseProgress(progress, FlashBundlePhase.Completed, "Flash bundle completed. 刷写包已完成。", 1, 1);
            result.IsSuccess = result.Errors.Count == 0;
        }
        catch (OperationCanceledException ex)
        {
            result.IsSuccess = false;
            var protocol = _session.Firehose;
            var observedKind = protocol?.LastFailureKind;
            var observed = observedKind is { } recorded && recorded != EdlFailureKind.None
                ? recorded
                : EdlFailureClassification.Classify(ex);
            result.FailureKind = observed;
            result.RequiresReconnect = protocol?.RequiresReconnect == true
                || observed is EdlFailureKind.CancelledAfterTransfer or EdlFailureKind.ExecutionUnknown;
            result.Record(new FlashItemEvidence
            {
                Kind = FlashBundleItemKind.Preflight,
                Outcome = CommandOutcomeKind.Cancelled,
                FailureKind = observed,
                RequiresReconnect = result.RequiresReconnect,
                Label = "bundle-cancelled"
            });
            result.Errors.Add("Flash bundle cancelled. 刷写包已取消。");
            RaiseProgress(progress, FlashBundlePhase.Error, $"Flash bundle cancelled: {ex.Message} 刷写包已取消。", 0, 0);
            stopwatch.Stop();
            result.ElapsedTime = stopwatch.Elapsed;
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            BindRunIdentity(result);
            throw new FlashBundleCancellationException(result, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.IsSuccess = false;
            result.FailureKind ??= EdlFailureClassification.Classify(ex);
            result.RequiresReconnect = _session.Firehose?.RequiresReconnect == true;
            result.Errors.Add($"Fatal error: {ex} 致命错误。");
            RaiseProgress(progress, FlashBundlePhase.Error, $"Flash bundle failed: {ex.Message} 刷写包失败。", 0, 0);
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedTime = stopwatch.Elapsed;
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            BindRunIdentity(result);
        }

        return result;
    }

    private void BindRunIdentity(FlashBundleResult result)
    {
        var capabilities = _session.Capabilities;
        result.ResourceSha256Hex ??= capabilities?.ResourceSha256Hex;
        result.TransportGeneration ??= capabilities?.TransportGeneration;
    }
    private FirehoseProtocol RequireFirehose()
    {
        var firehose = _session.Firehose;
        return firehose ?? throw new EdlException(EdlErrorCode.NotInitialized, "Firehose protocol is not initialized. Firehose 协议未初始化。");
    }
    private static void RaiseProgress(
        IProgress<FlashBundleProgress>? progress, FlashBundlePhase phase, string message, int current, int total)
        => progress?.Report(new FlashBundleProgress(phase, message, current, total));
}