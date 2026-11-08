using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Runs every numbered rawprogram and matching patch through one EDL session.<para>把每个带编号的 rawprogram 及其配套 patch 在同一个 EDL 会话中执行。</para></summary>
internal static class FlashEdlBundleService
{
    /// <summary>Inspects all bundle members before opening a device, then flashes them in manifest order.
    /// <para>先在打开设备前检查包内全部成员，再按清单顺序刷写。</para></summary>
    /// <param name="device">The EDL device to flash.<para>要刷写的 EDL 设备。</para></param>
    /// <param name="folder">Directory holding the rawprogram manifests and images.
    /// <para>存放 rawprogram 清单与镜像的目录。</para></param>
    /// <param name="programmerPath">Path of the Firehose programmer image to load.
    /// <para>要加载的 Firehose programmer 镜像路径。</para></param>
    /// <param name="ct">Token cancelling the run.<para>用于取消执行的令牌。</para></param>
    /// <param name="continueOnFailure">Keeps going after a failed write when true.
    /// <para>为真时写入失败后继续执行。</para></param>
    /// <param name="skipAbsentSources">Tolerates missing source images when true.
    /// <para>为真时容忍缺失的源镜像。</para></param>
    /// <param name="labels">Labels selecting manifest entries; null selects every entry.
    /// <para>筛选清单条目的标签，为空时选择全部条目。</para></param>
    /// <param name="progress">Reporter for per-step bundle progress.<para>逐步骤的进度上报对象。</para></param>
    /// <param name="verify">Requests read-back checks of written data when true.
    /// <para>为真时要求回读校验已写入的数据。</para></param>
    /// <param name="skipUfsProvision">Omits the UFS provision step when true.
    /// <para>为真时跳过 UFS 配置步骤。</para></param>
    /// <param name="tuning">Optional sector size, slot, chunking and wipe overrides.
    /// <para>扇区大小、槽位、分块与擦除的可选覆盖项。</para></param>
    /// <param name="initialization">Options controlling session initialization.
    /// <para>控制会话初始化的选项。</para></param>
    /// <param name="chunkProgress">Optional reporter for the per-chunk program byte counters of every entry, or
    /// null to keep the transfer silent.<para>每个条目逐块编程字节计数的可选上报对象；为 null 时传输保持无回调。</para></param>
    /// <param name="programmerProgress">Optional reporter for the per-image Sahara programmer upload byte counts,
    /// or null to keep the upload silent.<para>Sahara programmer 逐镜像上传字节计数的可选上报对象；为 null 时上传保持无回调。</para></param>
    /// <param name="programmerImages">Optional pre-built programmer image set, e.g. an in-memory inferred
    /// multi-image mapping; it is uploaded instead of the file at <paramref name="programmerPath"/> and makes
    /// that path optional.
    /// <para>可选的预构建 programmer 镜像集合，例如内存推断的多镜像映射；提供时替代
    /// <paramref name="programmerPath"/> 文件上传，并使该路径可缺省。</para></param>
    /// <returns>The non-fatal deviations recorded while the bundle ran.
    /// <para>包执行期间记录的非致命偏差。</para></returns>
    public static async Task<IReadOnlyList<string>> FlashAsync(DetectedDevice device, string folder, string programmerPath,
        CancellationToken ct, bool continueOnFailure = false, bool skipAbsentSources = false,
        IReadOnlyList<string>? labels = null, IProgress<FlashBundleProgress>? progress = null, bool verify = false,
        bool skipUfsProvision = false, EdlBundleTuning? tuning = null, EdlInitializationOptions? initialization = null,
        IProgress<EdlProgramChunkProgress>? chunkProgress = null,
        IProgress<SaharaImageProgressEventArgs>? programmerProgress = null,
        EdlProgrammerImageSet? programmerImages = null)
    {
        if (device.Protocol != DeviceProtocolKind.Edl || (!File.Exists(programmerPath) && programmerImages is null))
        {
            throw new InvalidOperationException("An EDL device and Firehose programmer are required.");
        }
        var (options, programs) = InspectFolder(folder, skipUfsProvision, ct, continueOnFailure, skipAbsentSources,
            labels, verify, tuning);
        var (overrides, staged) = await StageCompressedSourcesAsync(programs, ct).ConfigureAwait(false);
        if (overrides.Count > 0)
        {
            foreach (var option in options)
            {
                option.SourceOverrides = overrides;
            }
        }

        try
        {
            return await App.DeviceMonitor.RunEdlAsync(device, async session =>
            {
                if (programmerProgress is not null)
                {
                    session.SaharaImageProgress += OnProgrammerProgress;
                }
                try
                {
                    await InspectAllAsync(session, options, ct).ConfigureAwait(false);
                    if (programmerImages is not null)
                    {
                        await EdlProgrammerSession.EnsureInitializedAsync(session, programmerImages, initialization,
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await EdlProgrammerSession.EnsureInitializedAsync(session, programmerPath, initialization,
                            ct).ConfigureAwait(false);
                    }
                    return await ExecuteAllAsync(session, options, progress, chunkProgress, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (programmerProgress is not null)
                    {
                        session.SaharaImageProgress -= OnProgrammerProgress;
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var path in staged)
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        void OnProgrammerProgress(object? sender, SaharaImageProgressEventArgs imageProgress)
            => programmerProgress!.Report(imageProgress);
    }

    /// <summary>Inspects all bundle members before opening a serial device, then flashes them in manifest order.
    /// <para>先在打开串口设备前检查包内全部成员，再按清单顺序刷写。</para></summary>
    /// <param name="connect">Connector producing the serial EDL session.<para>产生串口 EDL 会话的连接器。</para></param>
    /// <param name="folder">Directory holding the rawprogram manifests and images.
    /// <para>存放 rawprogram 清单与镜像的目录。</para></param>
    /// <param name="includeUfsProvision">Includes the UFS provision step when true.
    /// <para>为真时包含 UFS 配置步骤。</para></param>
    /// <param name="cancellationToken">Token cancelling the run.<para>用于取消执行的令牌。</para></param>
    /// <param name="continueOnFailure">Keeps going after a failed write when true.
    /// <para>为真时写入失败后继续执行。</para></param>
    /// <param name="skipAbsentSources">Tolerates missing source images when true.
    /// <para>为真时容忍缺失的源镜像。</para></param>
    /// <param name="labels">Labels selecting manifest entries; null selects every entry.
    /// <para>筛选清单条目的标签，为空时选择全部条目。</para></param>
    /// <param name="progress">Reporter for per-step bundle progress.<para>逐步骤的进度上报对象。</para></param>
    /// <param name="verify">Requests read-back checks of written data when true.
    /// <para>为真时要求回读校验已写入的数据。</para></param>
    /// <param name="tuning">Optional sector size, slot, chunking and wipe overrides.
    /// <para>扇区大小、槽位、分块与擦除的可选覆盖项。</para></param>
    /// <param name="chunkProgress">Optional reporter for the per-chunk program byte counters of every entry, or
    /// null to keep the transfer silent.<para>每个条目逐块编程字节计数的可选上报对象；为 null 时传输保持无回调。</para></param>
    /// <returns>The non-fatal deviations recorded while the bundle ran.
    /// <para>包执行期间记录的非致命偏差。</para></returns>
    internal static async Task<IReadOnlyList<string>> FlashSerialCoreAsync(Func<CancellationToken, Task<EdlSession>> connect,
        string folder, bool includeUfsProvision, CancellationToken cancellationToken, bool continueOnFailure = false,
        bool skipAbsentSources = false, IReadOnlyList<string>? labels = null,
        IProgress<FlashBundleProgress>? progress = null, bool verify = false, EdlBundleTuning? tuning = null,
        IProgress<EdlProgramChunkProgress>? chunkProgress = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        var (options, programs) = InspectFolder(folder, !includeUfsProvision, cancellationToken, continueOnFailure,
            skipAbsentSources, labels, verify, tuning);
        var (overrides, staged) = await StageCompressedSourcesAsync(programs, cancellationToken).ConfigureAwait(false);
        if (overrides.Count > 0)
        {
            foreach (var option in options)
            {
                option.SourceOverrides = overrides;
            }
        }

        try
        {
            using var session = await connect(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The serial connector returned no EDL session.");
            cancellationToken.ThrowIfCancellationRequested();
            await InspectAllAsync(session, options, cancellationToken).ConfigureAwait(false);
            return await ExecuteAllAsync(session, options, progress, chunkProgress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var path in staged)
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Decompresses every distinct zstd-compressed bundle source into a staged temporary file and maps it over
    /// the manifest source so the orchestrator never programs compressed bytes.
    /// <para>把每个不同的 zstd 压缩包源解压到暂存临时文件并映射覆盖清单源，使编排器永不把压缩字节写入设备。</para>
    /// </summary>
    /// <param name="programs">Program entries parsed from the bundle manifests.
    /// <para>从包清单解析出的 program 条目。</para></param>
    /// <param name="ct">Cancels decompression.<para>取消解压。</para></param>
    /// <returns>The source-to-staged override map (empty when nothing is compressed) and the staged paths to
    /// delete after the run.
    /// <para>源到暂存文件的覆盖映射（无压缩源时为空）与运行结束后应删除的暂存路径。</para></returns>
    private static async Task<(IReadOnlyDictionary<string, string> Overrides, List<string> Staged)>
        StageCompressedSourcesAsync(IEnumerable<RawProgramEntry> programs, CancellationToken ct)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var staged = new List<string>();
        foreach (var source in programs
            .Select(entry => entry.SourceFile)
            .Where(source => !string.IsNullOrEmpty(source)
                && source.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                continue;
            }

            var replacement = await FlashCompressedImage.PrepareAsync(source, ct).ConfigureAwait(false);
            overrides.Add(source, replacement);
            staged.Add(replacement);
        }
        return (overrides, staged);
    }

    private static (FlashBundleOptions[] Options, IReadOnlyList<RawProgramEntry> Programs) InspectFolder(
        string folder, bool skipUfsProvision, CancellationToken ct,
        bool continueOnFailure, bool skipAbsentSources, IReadOnlyList<string>? labels, bool verify,
        EdlBundleTuning? tuning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ct.ThrowIfCancellationRequested();
        folder = Path.GetFullPath(folder);
        var directory = Directory.Exists(Path.Combine(folder, "images"))
            ? Path.Combine(folder, "images") : folder;
        var manifests = FlashManifestSelection.Enumerate(directory);
        if (manifests.Length is 0 or > 256)
        {
            throw new InvalidDataException("A bundle requires between one and 256 rawprogram manifests.");
        }
        var programs = RawProgramParser.ParseAll(manifests, directory);
        foreach (var entry in programs.Where(entry => !string.IsNullOrEmpty(entry.Filename)))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(entry.SourceFile) && !skipAbsentSources)
            {
                throw new FileNotFoundException("EDL bundle image is missing.", entry.SourceFile);
            }
        }
        return ([.. manifests.Select((manifest, index) => new FlashBundleOptions
        {
            RawProgramPath = manifest,
            PatchPath = File.Exists(Path.Combine(directory,
                "patch" + Path.GetFileNameWithoutExtension(manifest)["rawprogram".Length..] + ".xml"))
                ? Path.Combine(directory,
                    "patch" + Path.GetFileNameWithoutExtension(manifest)["rawprogram".Length..] + ".xml")
                : null,
            ImageDirectory = directory,
            ContinueOnFailure = continueOnFailure,
            SkipAbsentSources = skipAbsentSources,
            Verify = verify,
            OverrideSectorSize = tuning?.OverrideSectorSize,
            Slot = tuning?.Slot,
            WipeFirstSectors = tuning?.WipeFirstSectors ?? 0,
            MaxProgramChunkBytes = tuning?.MaxProgramChunkBytes,
            NandPagesPerBlock = tuning?.NandPagesPerBlock,
            Labels = labels is { Count: > 0 } selected ? [.. selected] : null,
            WipeConfirmation = tuning?.ConfirmManifestWipe == true
                ? new WipeConfirmation(WipeConfirmation.RequiredPhrase)
                : null,
            AllowEntireStorageWipe = tuning?.AllowEntireStorageWipe == true,
            SkipUfsProvision = skipUfsProvision || index != manifests.Length - 1
        })], programs);
    }

    private static async Task InspectAllAsync(EdlSession session, FlashBundleOptions[] options, CancellationToken ct)
    {
        var orchestrator = new FlashBundleOrchestrator(session);
        foreach (var option in options)
        {
            ct.ThrowIfCancellationRequested();
            option.DryRun = true;
            var preflight = await orchestrator.FlashBundleAsync(option, null, ct).ConfigureAwait(false);
            if (!preflight.IsSuccess)
            {
                throw new InvalidDataException(DescribeFailure(preflight));
            }
            option.DryRun = false;
        }
    }

    private static async Task<IReadOnlyList<string>> ExecuteAllAsync(EdlSession session, FlashBundleOptions[] options,
        IProgress<FlashBundleProgress>? progress, IProgress<EdlProgramChunkProgress>? chunkProgress,
        CancellationToken ct)
    {
        var orchestrator = new FlashBundleOrchestrator(session);
        if (chunkProgress is not null)
        {
            orchestrator.ProgramChunkProgress += (_, chunk) => chunkProgress.Report(chunk);
        }

        var results = await orchestrator.FlashPackageAsync(options, progress, ct).ConfigureAwait(false);
        var warnings = new List<string>();
        foreach (var result in results)
        {
            if (!result.IsSuccess)
            {
                throw new IOException(DescribeFailure(result));
            }
            warnings.AddRange(result.Warnings);
            warnings.AddRange(result.WipePreviews.Select(preview => preview.RefusalReason is { Length: > 0 } refusal
                ? $"Wipe '{preview.Name}' on LUN {preview.PhysicalPartitionNumber} was refused: {refusal}"
                : $"Wipe '{preview.Name}' on LUN {preview.PhysicalPartitionNumber} overwrote " +
                    $"{preview.SectorCount} sector(s) at {preview.StartSector} ({preview.BytesToOverwrite} bytes)."));
        }
        return warnings;
    }

    /// <summary>
    /// Builds the failure description from structured execution evidence rather than parsed response text.
    /// <para>依据结构化执行证据而不是解析响应文本来构造失败说明。</para>
    /// </summary>
    /// <param name="result">The completed bundle result to describe.<para>要描述的已完成包结果。</para></param>
    /// <returns>The composed failure description.<para>组装出的失败说明。</para></returns>
    private static string DescribeFailure(FlashBundleResult result)
    {
        var interrupted = result.Items.FirstOrDefault(item =>
            item.Outcome is not Comm.Protocols.Common.Evidence.CommandOutcomeKind.Completed);
        return $"EDL bundle failed: outcome={result.FinalOutcome}, failure={(result.FailureKind?.ToString() ?? "unclassified")}, " +
            $"item={(interrupted is null ? "-" : $"{interrupted.Kind}:{interrupted.Outcome}/{interrupted.FailureKind?.ToString() ?? "-"}")}, " +
            $"requiresReconnect={result.RequiresReconnect}, acknowledgedBytes={result.TotalBytesWritten}, " +
            $"programmerHash={(result.ResourceSha256Hex ?? "-")}. Detail: {string.Join("; ", result.Errors)}";
    }
}