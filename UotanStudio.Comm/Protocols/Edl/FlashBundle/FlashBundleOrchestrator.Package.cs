namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

public sealed partial class FlashBundleOrchestrator
{
    /// <summary>
    /// Captures and preflights an ordered package before storage writes, retaining parsed
    /// manifests and source handles until completion. Device preflight and execution share
    /// one operation lease. This does not prevent in-place file changes on every platform.
    /// <para>在存储写入前按顺序捕获并预检整个包，直到完成为止保留已解析的清单与源句柄。
    /// 设备预检与执行共享同一个操作租约。这不能在所有平台上阻止文件被就地修改。</para>
    /// </summary>
    /// <param name="bundles">One to 256 manifests. All must use the same DryRun mode.
    /// UFS provisioning is permitted only in the final manifest. Collections must not be
    /// mutated during synchronous capture. At most 1024 source handles and 65536
    /// selected program/patch entries are retained.
    /// <para>1 到 256 个清单，全部必须使用相同的 DryRun 模式；仅最后一个清单允许执行
    /// UFS 配置。同步捕获期间不得修改集合。最多保留 1024 个源句柄和 65536 个已选
    /// program/patch 条目。</para></param>
    /// <param name="progress">Per-bundle progress; callbacks must not wait for this session.
    /// <para>逐包进度回调；回调不得等待本会话。</para></param>
    /// <param name="cancellationToken">Cancellation for preparation, waiting and execution.
    /// <para>用于准备、等待与执行的取消令牌。</para></param>
    /// <returns>Ordered execution results, stopping at the first failed bundle; in DryRun
    /// mode, ordered offline inspection results. Preparation failures throw before writes.
    /// <para>按顺序返回的执行结果，遇到第一个失败的包即停止；DryRun 模式下按顺序返回
    /// 离线检查结果。准备失败会在写入前抛出异常。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="bundles"/> is <see langword="null"/> or contains a <see langword="null"/> element.
    /// <para><paramref name="bundles"/> 为 <see langword="null"/> 或包含 <see langword="null"/> 元素。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The manifest count is outside one to 256.
    /// <para>清单数量不在 1 到 256 之间。</para></exception>
    /// <exception cref="ArgumentException">The manifests mix inspection and execution modes.
    /// <para>清单混用了检查与执行模式。</para></exception>
    /// <exception cref="InvalidDataException">A non-final manifest provisions UFS, a package limit is exceeded, or a per-manifest preflight fails.
    /// <para>非最后的清单执行了 UFS 配置、超出包级上限，或某个清单的预检失败。</para></exception>
    /// <exception cref="EdlException">A manifest is missing or invalid, or the Firehose protocol is not initialized.
    /// <para>清单缺失或无效，或 Firehose 协议未初始化。</para></exception>
    public async Task<IReadOnlyList<FlashBundleResult>> FlashPackageAsync(
        IReadOnlyList<FlashBundleOptions> bundles, IProgress<FlashBundleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        cancellationToken.ThrowIfCancellationRequested();
        if (bundles.Count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(bundles), "A package requires one to 256 manifests.");
        }
        var options = new FlashBundleOptions[bundles.Count];
        var defaultSlot = RequireFirehose().Configuration.Slot;
        for (var i = 0; i < options.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(bundles[i]);
            options[i] = bundles[i].Snapshot();
            options[i].Slot ??= defaultSlot;
        }
        var dryRun = options[0].DryRun;
        if (options.Any(option => option.DryRun != dryRun))
        {
            throw new ArgumentException("A package cannot mix inspection and execution modes.", nameof(bundles));
        }
        var prepared = new List<PreparedBundle>();
        var results = new List<FlashBundleResult>();
        try
        {
            var remainingHandles = 1024;
            var remainingEntries = 65536;
            for (var i = 0; i < options.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = PrepareBundle(options[i], null, cancellationToken, remainingHandles, remainingEntries);
                prepared.Add(input);
                remainingHandles -= input.Sources?.Count ?? 0;
                remainingEntries -= input.Programs.Count + (input.Patches?.Count ?? 0);
                if (input.Ufs is not null && i != options.Length - 1)
                {
                    throw new InvalidDataException("Only the final package manifest may provision UFS.");
                }
                var inspection = options[i].Snapshot();
                inspection.DryRun = true;
                var result = await RunBundleAsync(inspection, null, false, cancellationToken, input).ConfigureAwait(false);
                RequirePreflight(result, i);
                if (dryRun)
                {
                    results.Add(result);
                }
            }
            if (dryRun)
            {
                return results.AsReadOnly();
            }

            using var operation = await RequireFirehose().EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < options.Length; i++)
            {
                var result = await RunBundleAsync(options[i], null, true, cancellationToken,
                    prepared[i], operationOwned: true).ConfigureAwait(false);
                RequirePreflight(result, i);
            }
            for (var i = 0; i < options.Length; i++)
            {
                var result = await RunBundleAsync(options[i], progress, false, cancellationToken,
                    prepared[i], operationOwned: true).ConfigureAwait(false);
                results.Add(result);
                if (!result.IsSuccess)
                {
                    break;
                }
            }
            return results.AsReadOnly();
        }
        finally
        {
            foreach (var input in prepared)
            {
                input.Dispose();
            }
        }
    }

    private static void RequirePreflight(FlashBundleResult result, int index)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidDataException($"Manifest {index} preflight failed: {string.Join("; ", result.Errors)}");
        }
    }

    private PreparedBundle PrepareBundle(FlashBundleOptions options, IProgress<FlashBundleProgress>? progress,
        CancellationToken cancellationToken, int maximumFiles = 1024, int maximumEntries = int.MaxValue)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        RaiseProgress(progress, FlashBundlePhase.Parsing, "Parsing rawprogram XML...", 0, 0);
        var programs = FilterProgramEntries(RawProgramParser.Parse(options.RawProgramPath), options);
        if (!options.SkipProgram)
        {
            ResolveSourceFiles(programs, options.ImageDirectory);
        }
        var absentPrograms = options.SkipAbsentSources && !options.SkipProgram
            ? programs.Where(entry => !string.IsNullOrEmpty(entry.Filename) && !File.Exists(entry.SourceFile)).ToList()
            : [];
        if (absentPrograms.Count > 0)
        {
            programs.RemoveAll(entry => absentPrograms.Contains(entry));
        }
        List<PatchEntry>? patches = null;
        if (!options.SkipPatch && !string.IsNullOrEmpty(options.PatchPath))
        {
            RaiseProgress(progress, FlashBundlePhase.Parsing, "Parsing patch XML...", 0, 0);
            patches = FilterPatchEntries(PatchParser.Parse(options.PatchPath), options);
        }
        if (programs.Count + (patches?.Count ?? 0) > maximumEntries)
        {
            throw new InvalidDataException("The package exceeds the selected manifest entry limit.");
        }
        UfsProvisionPlan? ufs = null;
        if (!options.SkipUfsProvision)
        {
            var path = ResolveUfsProvisionPath(options);
            if (path is not null)
            {
                RaiseProgress(progress, FlashBundlePhase.Parsing, "Parsing UFS provisioning XML...", 0, 0);
                ufs = PrepareUfs(UfsProvisionParser.Parse(path), options);
            }
        }
        var sources = options.SkipProgram ? null : FlashBundleSources.Open(programs, cancellationToken, maximumFiles);
        return new PreparedBundle(programs, patches, ufs, sources, absentPrograms);
    }

    private sealed record PreparedBundle(List<RawProgramEntry> Programs, List<PatchEntry>? Patches,
        UfsProvisionPlan? Ufs, FlashBundleSources? Sources,
        List<RawProgramEntry>? AbsentPrograms = null) : IDisposable
    {
        public void Dispose() => Sources?.Dispose();
    }
}