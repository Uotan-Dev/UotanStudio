using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>Sahara 内存调试（crashdump RAM dump）采集服务：在已租用的 EDL 传输上直接读取调试区域表，
/// 逐个区域把裸字节落到调用方指定目录，并按既有回读文件的“临时文件 + 校验 + 替换”规则原子发布。</para>
/// Sahara memory-debug (crashdump RAM dump) collection service: reads the debug region table on an
/// already-leased EDL transport, dumps every region into a caller-supplied directory and publishes
/// each file with the same staging, verification and replacement discipline the read-back files use.
/// </summary>
/// <remarks>
/// <para>目标提出的文件名一律视为不可信输入：只有
/// <see cref="SaharaProtocol.IsSafeMemoryDumpFileName"/> 接受、且解析后仍落在输出目录内的名称才会被
/// 写出。整段序列不加载 programmer，也不做 Firehose configure，因此只作用于 crashdump 链路；采集需要
/// 调用方显式确认，未确认时在任一字节到达传输之前抛出。</para>
/// <para>Names proposed by the target are untrusted: only a name accepted by
/// <see cref="SaharaProtocol.IsSafeMemoryDumpFileName"/> that still resolves inside the output
/// directory is written. The sequence never uploads a programmer and never configures Firehose, so it
/// applies only to a crashdump link, and it requires an explicit confirmation before touching the
/// transport.</para>
/// </remarks>
internal static class EdlMemoryDumpService
{
    /// <summary>
    /// <para>在已租用的 EDL 会话上采集 crashdump 内存转储。</para>
    /// Collects the crashdump memory dump on a leased EDL session.
    /// </summary>
    /// <param name="device"><para>目标 EDL 设备。</para>The target EDL device.</param>
    /// <param name="outputDirectory"><para>调用方选定的输出目录，写入前会被创建。</para>
    /// The caller-supplied output directory, created before writing.</param>
    /// <param name="confirmed"><para>调用方是否已显式确认采集。</para>
    /// Whether the caller explicitly confirmed the collection.</param>
    /// <param name="progress"><para>阶段进度。</para>Phase progress.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns><para>采集结果。</para>The collection result.</returns>
    internal static Task<FlashResult> DumpAsync(DeviceInfo device, string outputDirectory, bool confirmed,
        IProgress<FlashProgress>? progress, CancellationToken ct)
        => App.DeviceMonitor.RunEdlAsync(device.Device,
            session => DumpCoreAsync(session, outputDirectory, confirmed, progress, ct), ct);

    /// <summary>
    /// <para>在不接触设备租约的前提下执行采集：先确认门控与取消检查，再列出区域表，逐个区域经临时文件发布，
    /// 最后以一次 Sahara RESET 让目标离开内存调试模式；任何区域失败都只影响该区域，既有文件保持原样，
    /// 且失败路径仍会尝试尽力复位，与参考转储序列的收尾一致。</para>
    /// Runs the collection without touching the device lease: the confirmation and cancellation gates
    /// first, then the region table, then one staged publish per region, and finally a Sahara RESET that
    /// leaves memory-debug mode; a failing region only affects that region, existing files stay as they
    /// are, and the failure path still attempts the best-effort reset, matching the closing steps of the
    /// reference dump sequence.
    /// </summary>
    /// <param name="session"><para>承载 Sahara 内存调试序列的 EDL 会话。</para>
    /// The EDL session carrying the Sahara memory-debug sequence.</param>
    /// <param name="outputDirectory"><para>调用方选定的输出目录，必须非空。</para>
    /// The caller-supplied output directory; must not be empty.</param>
    /// <param name="confirmed"><para>调用方是否已显式确认采集。</para>
    /// Whether the caller explicitly confirmed the collection.</param>
    /// <param name="progress"><para>阶段进度，可为空。</para>Optional phase progress.</param>
    /// <param name="ct"><para>取消令牌，贯穿区域列表与每一块读取。</para>
    /// Cancellation token threaded through the table read and every chunk.</param>
    /// <returns><para>采集结果，摘要含写出区域数与字节数。</para>
    /// The collection result, summarizing the region count and byte count.</returns>
    /// <exception cref="InvalidOperationException"><para>未显式确认。</para>
    /// The collection was not confirmed.</exception>
    /// <exception cref="ArgumentException"><para>输出目录为空或仅空白。</para>
    /// The output directory is empty or whitespace.</exception>
    /// <exception cref="InvalidDataException"><para>区域给出的文件名不安全或在输出目录之外。</para>
    /// A region proposed an unsafe file name or one resolving outside the output directory.</exception>
    /// <exception cref="SaharaException"><para>目标的区域表或字节流不合法，或未以 RESET_RESPONSE 应答收尾
    /// 复位。</para>The target's table or byte stream was malformed, or it did not answer the closing
    /// RESET with a RESET_RESPONSE.</exception>
    internal static async Task<FlashResult> DumpCoreAsync(EdlSession session, string outputDirectory,
        bool confirmed, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        if (!confirmed)
        {
            throw new InvalidOperationException(
                "Collecting a crashdump RAM dump requires explicit confirmation. 采集 crashdump 内存转储需要显式确认。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        var regions = await session.ReadMemoryDebugRegionsAsync(ct).ConfigureAwait(false);
        var linkClosed = false;
        try
        {
            var planned = regions.Count(region => region.Length > 0);
            progress?.Report(new FlashProgress(null, 0,
                $"Discovered {planned} non-empty Sahara memory-debug region(s). 已发现 {planned} 个非空的 Sahara 内存调试区域。"));
            if (planned == 0)
            {
                await session.ResetMemoryDebugLinkAsync(ct).ConfigureAwait(false);
                linkClosed = true;
                return new FlashResult
                {
                    Success = false,
                    Summary = "The crashdump target reported no non-empty memory-debug region, so nothing was written. " +
                        "目标未上报任何非空的内存调试区域，因此未写出任何文件。"
                };
            }

            var written = new List<SaharaMemoryRegion>(planned);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var totalBytes = 0L;
            var completed = 0;
            foreach (var region in regions)
            {
                ct.ThrowIfCancellationRequested();
                if (region.Length == 0)
                {
                    continue;
                }

                var destination = ResolveRegionPath(root, region);
                if (!usedNames.Add(Path.GetFileName(destination)))
                {
                    throw new InvalidDataException(
                        $"Two Sahara debug regions resolve to the file name '{Path.GetFileName(destination)}'. " +
                        "两个 Sahara 调试区域解析出同一个文件名。");
                }

                progress?.Report(new FlashProgress(Path.GetFileName(destination), 0,
                    $"Reading 0x{region.Address:X16}+0x{region.Length:X} ({region.Description})... 正在读取该内存区域..."));
                var length = await PublishRegionAsync(session, region, destination, ct).ConfigureAwait(false);
                totalBytes = checked(totalBytes + length);
                completed++;
                written.Add(region);
                progress?.Report(new FlashProgress(Path.GetFileName(destination),
                    (int)((completed * 100L) / planned),
                    $"Published {length} bytes. 已发布 {length} 字节。"));
            }

            await session.ResetMemoryDebugLinkAsync(ct).ConfigureAwait(false);
            linkClosed = true;
            progress?.Report(new FlashProgress(null, 100, "Memory dump collected. 内存转储采集完成。"));
            return new FlashResult
            {
                Success = true,
                Summary = $"Collected {written.Count} Sahara memory-debug region(s) ({totalBytes} bytes) into '{root}'. " +
                    $"已把 {written.Count} 个 Sahara 内存调试区域（共 {totalBytes} 字节）采集到该目录。"
            };
        }
        finally
        {
            if (!linkClosed)
            {
                try
                {
                    await session.TryResetMemoryDebugLinkAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    progress?.Report(new FlashProgress(null, 0,
                        $"The target was left in memory-debug mode because the crashdump reset did not complete: {ex.Message} " +
                        $"目标仍停留在内存调试模式，因为其复位未能完成：{ex.Message}"));
                }
            }
        }
    }

    /// <summary>
    /// <para>解析单个区域的落盘路径：目标自报的名称必须先通过
    /// <see cref="SaharaProtocol.IsSafeMemoryDumpFileName"/>，再确认解析结果仍是输出目录内的直接子文件。</para>
    /// Resolves one region's destination: the target-proposed name must first pass
    /// <see cref="SaharaProtocol.IsSafeMemoryDumpFileName"/> and then still resolve to a direct child of
    /// the output directory.
    /// </summary>
    /// <param name="outputRoot"><para>已规范化的输出目录。</para>The normalized output directory.</param>
    /// <param name="region"><para>要写出的区域。</para>The region to write.</param>
    /// <returns><para>输出目录内的绝对文件路径。</para>The absolute file path inside the output directory.</returns>
    /// <exception cref="InvalidDataException"><para>名称不安全、为空，或解析后逃出输出目录。</para>
    /// The name is unsafe, empty, or resolves outside the output directory.</exception>
    internal static string ResolveRegionPath(string outputRoot, SaharaMemoryRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        if (!SaharaProtocol.IsSafeMemoryDumpFileName(region.FileName))
        {
            throw new InvalidDataException(
                $"The crashdump target proposed an unsafe region file name '{region.FileName}'. " +
                $"目标给出了不安全的区域文件名 '{region.FileName}'。");
        }

        var root = Path.GetFullPath(outputRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, region.FileName));
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal)
            || string.Equals(candidate, rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The region file name '{region.FileName}' does not resolve inside the output directory. " +
                "该区域文件名不会落在输出目录之内。");
        }

        return candidate;
    }

    /// <summary>
    /// <para>把一个区域的字节先写入输出目录内的唯一临时文件，核对实测长度与区域声明长度一致后再原子
    /// 替换目标；长度不符或取消都会删除临时文件并保留既有目标内容。</para>
    /// Writes one region's bytes to a unique staging file inside the output directory, compares the
    /// measured length with the length the region declared, and only then replaces the destination
    /// atomically; a length mismatch or a cancellation removes the staging file and keeps any existing
    /// destination untouched.
    /// </summary>
    /// <param name="session"><para>承载读取的 EDL 会话。</para>The EDL session carrying the reads.</param>
    /// <param name="region"><para>要转储的区域。</para>The region to dump.</param>
    /// <param name="destinationPath"><para>最终发布路径。</para>The published destination path.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns><para>发布的字节数。</para>The number of published bytes.</returns>
    /// <exception cref="IOException"><para>目标返回的字节数与其声明的区域长度不符。</para>
    /// The target returned a number of bytes that differs from the region's declared length.</exception>
    private static async Task<long> PublishRegionAsync(EdlSession session, SaharaMemoryRegion region,
        string destinationPath, CancellationToken ct)
    {
        var expected = checked((long)region.Length);
        var stagingPath = destinationPath + ".uotan-" + Guid.NewGuid().ToString("N");
        try
        {
            long written;
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                written = checked((long)await session
                    .DumpMemoryRegionAsync(region, staging, ct)
                    .ConfigureAwait(false));
                if (written != expected)
                {
                    throw new IOException(
                        $"The region at 0x{region.Address:X16} returned {written} of {expected} declared bytes. " +
                        $"该区域只返回了 {written} 字节，而声明长度为 {expected} 字节。");
                }

                await staging.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destinationPath, overwrite: true);
            return written;
        }
        finally
        {
            try
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}