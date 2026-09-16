using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

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
public sealed class FlashBundleProgress(FlashBundlePhase phase, string message, int current, int total)
{
    /// <summary>Gets the current phase. 当前阶段。</summary>
    public FlashBundlePhase Phase { get; } = phase;

    /// <summary>Gets a human-readable message. 人类可读的消息。</summary>
    public string Message { get; } = message;

    /// <summary>Gets the zero-based index of the current item. 当前条目从 0 开始的索引。</summary>
    public int Current { get; } = current;

    /// <summary>Gets the total number of items in the current phase. 当前阶段条目总数。</summary>
    public int Total { get; } = total;

    /// <summary>
    /// Returns a string representation of the progress.
    /// <para>返回进度的字符串表示。</para>
    /// </summary>
    /// <returns>A string describing the progress. 描述进度的字符串。</returns>
    public override string ToString() => $"FlashBundleProgress[{Phase}] {Message} ({Current}/{Total})";
}

/// <summary>
/// Orchestrates a Qualcomm EDL flash bundle: parses rawprogram0.xml / patch0.xml /
/// UFS-provision XML files and dispatches the individual Program, Erase, Patch, Wipe and UFS
/// provisioning operations through the Firehose protocol.
/// <para>编排 Qualcomm EDL 刷写包：解析 rawprogram0.xml / patch0.xml / UFS 配置 XML 文件，
/// 并通过 Firehose 协议逐项派发 Program、Erase、Patch、Wipe 与 UFS 配置操作。</para>
/// </summary>
/// <remarks>
/// This is a session-based port of the reference <c>FlashBundleOrchestrator</c>: instead of a
/// driver abstraction it operates directly on an <see cref="EdlSession"/> (which owns the
/// Firehose transport). Skip-block SHA256 verification, NAND geometry back-fill and the
/// bootable-drive step of the reference are intentionally not ported here.
/// <para>这是参考 <c>FlashBundleOrchestrator</c> 的基于会话的移植：不使用驱动抽象，而是直接
/// 基于 <see cref="EdlSession"/>（其持有 Firehose 传输）操作。参考实现中的 skip-block SHA256
/// 校验、NAND 几何回填与可启动驱动器步骤在此有意未移植。</para>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="FlashBundleOrchestrator"/> class.
/// <para>初始化 <see cref="FlashBundleOrchestrator"/> 类的新实例。</para>
/// </remarks>
/// <param name="session">The EDL session used for all device operations. 用于所有设备操作的 EDL 会话。</param>
public sealed class FlashBundleOrchestrator(EdlSession session)
{
    private readonly EdlSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// Runs a flash bundle from a rawprogram file (and optional patch file), resolving image
    /// paths against <paramref name="imageDir"/>. UFS provisioning XML is auto-discovered in
    /// the image directory.
    /// <para>从 rawprogram 文件（及可选的 patch 文件）运行刷写包，镜像路径按
    /// <paramref name="imageDir"/> 解析。UFS 配置 XML 在镜像目录中自动发现。</para>
    /// </summary>
    /// <param name="rawprogramPath">The rawprogram XML file path. rawprogram XML 文件路径。</param>
    /// <param name="patchPath">The optional patch XML file path. 可选的 patch XML 文件路径。</param>
    /// <param name="imageDir">The base directory for image files; defaults to the rawprogram's directory. 镜像文件基础目录；默认为 rawprogram 所在目录。</param>
    /// <param name="progress">Optional progress reporter. 可选的进度报告器。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The flash bundle result. 刷写包结果。</returns>
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
    /// <param name="options">The flash bundle options. 刷写包选项。</param>
    /// <param name="progress">Optional progress reporter. 可选的进度报告器。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The flash bundle result. 刷写包结果。</returns>
    public async Task<FlashBundleResult> FlashBundleAsync(
        FlashBundleOptions options,
        IProgress<FlashBundleProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var stopwatch = Stopwatch.StartNew();
        var result = new FlashBundleResult();

        try
        {
            ValidateOptions(options);

            RaiseProgress(progress, FlashBundlePhase.Parsing, "Parsing rawprogram XML... 解析 rawprogram XML...", 0, 0);
            var rawProgramEntries = RawProgramParser.Parse(options.RawProgramPath);
            ResolveSourceFiles(rawProgramEntries, options.ImageDirectory);
            var filteredPrograms = FilterProgramEntries(rawProgramEntries, options);

            List<PatchEntry>? filteredPatches = null;
            if (!options.SkipPatch && !string.IsNullOrEmpty(options.PatchPath))
            {
                RaiseProgress(progress, FlashBundlePhase.Parsing, "Parsing patch XML... 解析 patch XML...", 0, 0);
                var patchEntries = PatchParser.Parse(options.PatchPath);
                filteredPatches = FilterPatchEntries(patchEntries, options);
            }

            UfsProvisionFile? ufsProvision = null;
            if (!options.SkipUfsProvision)
            {
                var ufsPath = ResolveUfsProvisionPath(options);
                if (ufsPath != null)
                {
                    RaiseProgress(progress, FlashBundlePhase.Parsing, "Parsing UFS provisioning XML... 解析 UFS 配置 XML...", 0, 0);
                    ufsProvision = UfsProvisionParser.Parse(ufsPath);
                }
            }

            RaiseProgress(progress, FlashBundlePhase.Parsing, "Initializing EDL session... 初始化 EDL 会话...", 0, 0);
            await _session.EnsureInitializedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var firehose = RequireFirehose();
            if (options.Slot.HasValue)
            {
                firehose.Configuration.Slot = options.Slot.Value;
            }

            if (options.OverrideSectorSize is > 0)
            {
                firehose.Configuration.SectorSize = options.OverrideSectorSize.Value;
            }

            var sectorSize = firehose.Configuration.SectorSize > 0 ? firehose.Configuration.SectorSize : 4096;
            var touchedPartitions = new HashSet<int>();

            // ---- Program / Erase phase ---------------------------------------------
            if (!options.SkipProgram)
            {
                var total = filteredPrograms.Count;
                for (var i = 0; i < total; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = filteredPrograms[i];
                    var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;

                    if (string.IsNullOrEmpty(entry.Filename))
                    {
                        if (entry.NumPartitionSectors <= 0)
                        {
                            result.ProgramEntriesSkipped++;
                            continue;
                        }

                        RaiseProgress(progress, FlashBundlePhase.Erasing, $"Erasing {displayName}... 擦除 {displayName}...", i, total);
                        try
                        {
                            if (!options.DryRun)
                            {
                                var response = await _session.ErasePartitionAsync(
                                    ParseStartSector(entry.StartSector),
                                    entry.NumPartitionSectors,
                                    entry.PhysicalPartitionNumber,
                                    cancellationToken).ConfigureAwait(false);
                                if (!response.IsAck)
                                {
                                    throw new EdlException(EdlErrorCode.EraseFailed,
                                        $"Erase failed for '{displayName}'. Response: {response.RawValue} 擦除失败。");
                                }

                                touchedPartitions.Add(entry.PhysicalPartitionNumber);
                            }

                            result.EraseEntriesProcessed++;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"Erase failed for '{displayName}': {ex.Message} 擦除失败。");
                            result.ProgramEntriesSkipped++;
                        }

                        continue;
                    }

                    RaiseProgress(progress, FlashBundlePhase.Programming, $"Programming {displayName}... 编程 {displayName}...", i, total);
                    try
                    {
                        if (options.DryRun)
                        {
                            result.ProgramEntriesSkipped++;
                            continue;
                        }

                        var bytesWritten = await FlashProgramEntryAsync(entry, sectorSize, cancellationToken).ConfigureAwait(false);
                        result.ProgramEntriesProcessed++;
                        result.TotalBytesWritten += bytesWritten;
                        touchedPartitions.Add(entry.PhysicalPartitionNumber);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Program failed for '{displayName}': {ex.Message} 编程失败。");
                        result.ProgramEntriesSkipped++;
                    }
                }
            }
            else
            {
                result.ProgramEntriesSkipped = filteredPrograms.Count;
            }

            // ---- Patch phase --------------------------------------------------------
            if (!options.SkipPatch && filteredPatches is { Count: > 0 })
            {
                var total = filteredPatches.Count;
                for (var i = 0; i < total; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = filteredPatches[i];
                    var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;

                    RaiseProgress(progress, FlashBundlePhase.Patching, $"Patching {displayName}... 修补 {displayName}...", i, total);
                    try
                    {
                        if (!options.DryRun)
                        {
                            var response = await firehose.PatchAsync(
                                entry.Filename,
                                entry.PhysicalPartitionNumber,
                                ParseStartSector(entry.StartSector),
                                entry.ByteOffset,
                                entry.Value,
                                entry.SizeInBytes,
                                cancellationToken).ConfigureAwait(false);
                            if (!response.IsAck)
                            {
                                throw new EdlException(EdlErrorCode.ProgramFailed,
                                    $"Patch failed for '{displayName}'. Response: {response.RawValue} 修补失败。");
                            }
                        }

                        result.PatchEntriesProcessed++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Patch failed for '{displayName}': {ex.Message} 修补失败。");
                        result.PatchEntriesSkipped++;
                    }
                }
            }

            // ---- Wipe phase (program-zeros over the first N sectors) -----------------
            if (!options.SkipWipe && options.WipeFirstSectors > 0 && touchedPartitions.Count > 0)
            {
                var targets = options.WipePhysicalPartitions is { Length: > 0 }
                    ? options.WipePhysicalPartitions.Distinct().Order().ToArray()
                    : [.. touchedPartitions.Order()];

                for (var i = 0; i < targets.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var partition = targets[i];
                    RaiseProgress(progress, FlashBundlePhase.Wiping,
                        $"Wiping first {options.WipeFirstSectors} sectors of partition {partition}... 擦写分区 {partition} 前 {options.WipeFirstSectors} 个扇区...", i, targets.Length);
                    try
                    {
                        if (!options.DryRun)
                        {
                            var response = await firehose.WipeAsync(
                                0, options.WipeFirstSectors, partition, cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (!response.IsAck)
                            {
                                throw new EdlException(EdlErrorCode.ProgramFailed,
                                    $"Wipe failed for partition {partition}. Response: {response.RawValue} 擦写失败。");
                            }
                        }

                        result.WipeEntriesProcessed++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Wipe failed for partition {partition}: {ex.Message} 擦写失败。");
                    }
                }
            }

            // ---- UFS provisioning phase ----------------------------------------------
            if (ufsProvision != null)
            {
                RaiseProgress(progress, FlashBundlePhase.UfsProvisioning, "Provisioning UFS... 配置 UFS...", 0, 1);
                try
                {
                    if (!options.DryRun)
                    {
                        var common = ufsProvision.Common is null
                            ? new UfsCommon()
                            : MapUfsCommon(ufsProvision.Common);
                        var bodies = ufsProvision.Bodies.Select(MapUfsBody).ToList();
                        var epilogue = ufsProvision.Epilogue is null
                            ? new UfsEpilogue()
                            : MapUfsEpilogue(ufsProvision.Epilogue);
                        var commit = options.UfsCommit ?? ((ufsProvision.Epilogue?.Commit ?? 0) != 0);

                        var response = await firehose.ProvisionUfsAsync(common, bodies, epilogue, commit, cancellationToken).ConfigureAwait(false);
                        if (!response.IsAck)
                        {
                            throw new EdlException(EdlErrorCode.UfsProvisioningFailed,
                                $"UFS provisioning failed. Response: {response.RawValue} UFS 配置失败。");
                        }
                    }

                    result.UfsProvisioned = true;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"UFS provisioning failed: {ex.Message} UFS 配置失败。");
                }
            }

            RaiseProgress(progress, FlashBundlePhase.Completed, "Flash bundle completed. 刷写包已完成。", 1, 1);
            result.IsSuccess = result.Errors.Count == 0;
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.Errors.Add($"Fatal error: {ex} 致命错误。");
            RaiseProgress(progress, FlashBundlePhase.Error, $"Flash bundle failed: {ex.Message} 刷写包失败。", 0, 0);
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedTime = stopwatch.Elapsed;
        }

        return result;
    }

    private static void ValidateOptions(FlashBundleOptions options)
    {
        if (string.IsNullOrEmpty(options.RawProgramPath))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "RawProgramPath must be specified. 必须指定 RawProgramPath。");
        }

        if (!File.Exists(options.RawProgramPath))
        {
            throw new EdlException(EdlErrorCode.FileIO, $"Rawprogram file not found: {options.RawProgramPath} rawprogram 文件不存在。");
        }

        if (!string.IsNullOrEmpty(options.PatchPath) && !File.Exists(options.PatchPath))
        {
            throw new EdlException(EdlErrorCode.FileIO, $"Patch file not found: {options.PatchPath} patch 文件不存在。");
        }

        if (string.IsNullOrEmpty(options.ImageDirectory))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "ImageDirectory must be specified. 必须指定 ImageDirectory。");
        }
    }

    private FirehoseProtocol RequireFirehose()
    {
        var firehose = _session.Firehose;
        return firehose ?? throw new EdlException(EdlErrorCode.NotInitialized, "Firehose protocol is not initialized. Firehose 协议未初始化。");
    }

    private static void ResolveSourceFiles(IEnumerable<RawProgramEntry> entries, string imageDirectory)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.SourceFile) && !string.IsNullOrEmpty(entry.Filename))
            {
                var candidate = Path.Combine(imageDirectory, entry.Filename);
                entry.SourceFile = File.Exists(candidate) ? candidate : entry.Filename;
            }
        }
    }

    private static List<RawProgramEntry> FilterProgramEntries(
        List<RawProgramEntry> entries, FlashBundleOptions options)
    {
        IEnumerable<RawProgramEntry> filtered = entries;

        if (options.Labels is { Count: > 0 })
        {
            var labelSet = new HashSet<string>(options.Labels, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(e => !string.IsNullOrEmpty(e.Label) && labelSet.Contains(e.Label));
        }

        if (options.NotLabels is { Count: > 0 })
        {
            var notLabelSet = new HashSet<string>(options.NotLabels, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(e => string.IsNullOrEmpty(e.Label) || !notLabelSet.Contains(e.Label));
        }

        return [.. filtered];
    }

    private static List<PatchEntry> FilterPatchEntries(
        List<PatchEntry> entries, FlashBundleOptions options)
    {
        IEnumerable<PatchEntry> filtered = entries;

        if (options.Labels is { Count: > 0 })
        {
            var labelSet = new HashSet<string>(options.Labels, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(e => e.Label != null && labelSet.Contains(e.Label));
        }

        if (options.NotLabels is { Count: > 0 })
        {
            var notLabelSet = new HashSet<string>(options.NotLabels, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(e => e.Label == null || !notLabelSet.Contains(e.Label));
        }

        return [.. filtered];
    }

    private static string? ResolveUfsProvisionPath(FlashBundleOptions options)
    {
        if (!string.IsNullOrEmpty(options.UfsProvisionPath))
        {
            var path = options.UfsProvisionPath;
            return !File.Exists(path) ? throw new EdlException(EdlErrorCode.FileIO, $"UFS provision file not found: {path} UFS 配置文件不存在。") : path;
        }

        return string.IsNullOrEmpty(options.ImageDirectory) || !Directory.Exists(options.ImageDirectory)
            ? null
            : Directory.EnumerateFiles(options.ImageDirectory, "*.xml", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.Contains("ufs", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("provision", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static UfsCommon MapUfsCommon(UfsProvisionCommon source) => new()
    {
        BNumberLU = source.BNumberLU,
        BBootEnable = source.BBootEnable,
        BDescrAccessEn = source.BDescrAccessEn,
        BInitPowerMode = source.BInitPowerMode,
        BHighPriorityLUN = source.BHighPriorityLUN,
        BSecureRemovalType = source.BSecureRemovalType,
        BInitActiveICCLevel = source.BInitActiveICCLevel,
        WPeriodicRTCUpdate = source.WPeriodicRTCUpdate,
        BConfigDescrLock = source.BConfigDescrLock
    };

    private static UfsBody MapUfsBody(UfsProvisionBody source) => new()
    {
        LUNum = source.LUNum,
        BLUEnable = source.BLUEnable,
        BBootLUNID = source.BBootLUNID,
        SizeInKb = source.SizeInKb,
        BDataReliability = source.BDataReliability,
        BLUWriteProtect = source.BLUWriteProtect,
        BMemoryType = source.BMemoryType,
        BLogicalBlockSize = source.BLogicalBlockSize,
        BProvisioningType = source.BProvisioningType,
        WContextCapacities = source.WContextCapacities,
        Desc = source.Desc
    };

    private static UfsEpilogue MapUfsEpilogue(UfsProvisionEpilogue source) => new()
    {
        LUNumToGrow = source.LUNumToGrow
    };

    private async Task<long> FlashProgramEntryAsync(
        RawProgramEntry entry, long sectorSize, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(entry.SourceFile))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"No source file for program entry '{entry.Label ?? entry.Filename}'. program 条目缺少源文件。");
        }

        if (!File.Exists(entry.SourceFile))
        {
            throw new EdlException(EdlErrorCode.FileIO, $"Image file not found: {entry.SourceFile} 镜像文件不存在。");
        }

        var fileInfo = new FileInfo(entry.SourceFile);
        var startSector = ParseStartSector(entry.StartSector);

        using var stream = new FileStream(entry.SourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (entry.IsSparse || IsAndroidSparse(stream))
        {
            stream.Position = 0;
            long totalSectors;
            try
            {
                totalSectors = AndroidSparseImage.CountDataSectors(stream, sectorSize);
            }
            finally
            {
                stream.Position = 0;
            }

            if (totalSectors <= 0)
            {
                throw new EdlException(EdlErrorCode.InvalidParameter,
                    $"Sparse image contains no data chunks: {entry.SourceFile} 稀疏镜像不含数据块。");
            }

            using var rawStream = new AndroidSparseImage.SparseRawStream(stream);
            var response = await _session.FlashPartitionAsync(
                rawStream, startSector, totalSectors, entry.Filename, entry.PhysicalPartitionNumber, cancellationToken).ConfigureAwait(false);
            return !response.IsAck
                ? throw new EdlException(EdlErrorCode.ProgramFailed,
                    $"Sparse flash failed for '{entry.Label ?? entry.Filename}'. Response: {response.RawValue} 稀疏刷写失败。")
                : fileInfo.Length;
        }

        if (entry.FileSectorOffset > 0)
        {
            stream.Position = Math.Min(entry.FileSectorOffset * sectorSize, stream.Length);
        }

        var lengthFromOffset = stream.Length - stream.Position;
        var numSectors = entry.NumPartitionSectors > 0
            ? entry.NumPartitionSectors
            : (lengthFromOffset / sectorSize) + (lengthFromOffset % sectorSize != 0 ? 1 : 0);

        if (numSectors <= 0)
        {
            return 0;
        }

        var responseRaw = await _session.FlashPartitionAsync(
            stream, startSector, numSectors, entry.Filename, entry.PhysicalPartitionNumber, cancellationToken).ConfigureAwait(false);
        return !responseRaw.IsAck
            ? throw new EdlException(EdlErrorCode.ProgramFailed,
                $"Flash failed for '{entry.Label ?? entry.Filename}'. Response: {responseRaw.RawValue} 刷写失败。")
            : fileInfo.Length;
    }

    private static bool IsAndroidSparse(Stream stream)
    {
        var position = stream.Position;
        try
        {
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) == AndroidSparseImage.Magic;
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static long ParseStartSector(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
            ? dec
            : value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
            ? hex
            : throw new EdlException(EdlErrorCode.InvalidParameter, $"Invalid start_sector value: '{value}' start_sector 值无效。");
    }

    private static void RaiseProgress(
        IProgress<FlashBundleProgress>? progress, FlashBundlePhase phase, string message, int current, int total)
        => progress?.Report(new FlashBundleProgress(phase, message, current, total));
}