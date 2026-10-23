using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Compress;
using UotanStudio.Firmware.BlockImage.Sparse;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Executes a SpacemiT flash package against a fastboot session, replicating the vendor
/// flasher's flow: optional BootROM FSBL/U-Boot staging, super-speed switch, partition
/// table write (<c>flash:gpt</c>/<c>flash:mtd</c>) and per-partition image flashing with
/// gzip-chunked oversized images.
/// <para>对 fastboot 会话执行 SpacemiT 烧录包，复刻厂商烧录器流程：可选的 BootROM
/// FSBL/U-Boot 暂存拉起、super speed 切换、分区表写入（<c>flash:gpt</c>/<c>flash:mtd</c>）
/// 与逐分区镜像刷写（超大镜像按 gzip 分块传输）。</para>
/// </summary>
/// <remarks>
/// <para>Device contract (verified against SpacemiT U-Boot <c>fb_spacemit.c</c>/<c>fb_blk.c</c>):
/// a download payload starting with the gzip magic is transparently decompressed, and
/// consecutive <c>flash:&lt;same partition&gt;</c> calls with gzip payloads append at the
/// running decompressed offset until the partition name changes or a non-gzip payload
/// arrives; non-gzip sparse payloads are written by the on-device sparse writer; raw
/// payloads always write at the partition start.</para>
/// <para>设备端契约（已对照 SpacemiT U-Boot <c>fb_spacemit.c</c>/<c>fb_blk.c</c> 验证）：
/// 以 gzip 魔数开头的下载负载会被透明解压；对同一分区连续多次携带 gzip 负载的
/// <c>flash:&lt;分区&gt;</c> 调用会在设备端按解压后的运行偏移追加写入，直到分区名变化或
/// 出现非 gzip 负载；非 gzip 的 sparse 负载由设备端 sparse 写入器处理；raw 负载始终从
/// 分区起始处写入。</para>
/// </remarks>
public sealed class SpacemiTFlasher
{
    private const int SparseHeaderBytes = 4;

    /// <summary>Headroom subtracted from the device max-download-size per gzip chunk so the
    /// compressed member never exceeds the negotiated limit even for incompressible data.
    /// <para>每个 gzip 分块从设备 max-download-size 中预留的余量，确保即使数据不可压缩，
    /// 压缩后的成员也绝不超出协商上限。</para></summary>
    private const long GzipChunkHeadroom = 64 * 1024;

    /// <summary>Upper bound for a single raw gzip chunk, so a device reporting a huge
    /// max-download-size cannot force an equally huge host allocation.
    /// <para>单个 raw gzip 分块的上限，避免设备上报的巨大 max-download-size 逼出同样巨大的
    /// 宿主端分配。</para></summary>
    private const long MaxGzipChunkBytes = 0x10000000;

    private readonly SpacemiTFlashPackage _package;
    private readonly SpacemiTFlashOptions _options;
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

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
    /// Runs the package script. The supplied re-open handler is invoked whenever the device
    /// re-enumerates (after <c>continue</c> or an accepted super-speed switch): it must dispose
    /// the stale session and return a session for the re-enumerated device, or
    /// <see langword="null"/> when the device never re-appeared.
    /// <para>执行包脚本。设备重新枚举时（<c>continue</c> 或被接受的 super speed 切换之后）
    /// 调用传入的重开处理器：它必须释放过期会话并返回重新枚举设备的新会话；设备未重新出现时
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
        for (var index = 0; index < actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = actions[index];
            if (SpacemiTConditionEvaluator.ShouldSkip(action.SkipWhen, _variables))
            {
                Report(progress, "skip", null, $"action #{index + 1} skipped by skip_when '{action.SkipWhen}'");
                continue;
            }

            switch (action)
            {
                case SpacemiTGetVarAction getVar:
                    ExecuteGetVar(session, getVar, progress, cancellationToken);
                    break;
                case SpacemiTStageAction stage:
                    ExecuteStage(session, stage, progress, cancellationToken);
                    break;
                case SpacemiTContinueAction:
                    session = ExecuteContinue(session, reopenSession, progress, cancellationToken);
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
                default:
                    throw new InvalidOperationException($"Unsupported action type {action.GetType().Name}.");
            }
        }

        if (_options.RebootAfterFlash)
        {
            RunWithRetry("reboot", 1, skipFail: false, progress, null,
                () =>
                {
                    session.Reboot().ThrowIfFailed();
                    return true;
                });
        }

        Report(progress, "done", null, "flash script finished");
    }

    private void ExecuteGetVar(
        FastbootSession session,
        SpacemiTGetVarAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? value = null;
        var ok = RunWithRetry($"getvar {action.Variable}", action.Retry, action.SkipFail, progress, action.Variable, () =>
        {
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
            Report(progress, "action", action.Variable, $"getvar {action.Variable} = '{value}'");
        }
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
            session.DownloadData(stream, stream.Length, CreateByteProgress(progress, "stage", null, stream.Length), cancellationToken)
                .ThrowIfFailed();
            return true;
        });
    }

    private FastbootSession ExecuteContinue(
        FastbootSession session,
        Func<FastbootSession, FastbootSession?> reopenSession,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "action", null, "continue: device leaves fastboot, waiting for re-enumeration");
        RunWithRetry("continue", 1, skipFail: false, progress, null,
            () =>
            {
                session.ContinueBoot().ThrowIfFailed();
                return true;
            });

        return ReopenAfterReEnumeration(session, reopenSession, progress, cancellationToken);
    }

    private FastbootSession ExecuteOem(
        FastbootSession session,
        Func<FastbootSession, FastbootSession?> reopenSession,
        SpacemiTOemAction action,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var accepted = RunWithRetry($"oem {action.Args}", action.Retry, action.SkipFail, progress, null, () =>
        {
            var response = SendWithTimeout(session, FastbootConstants.CommandOem + action.Args, action.Timeout, cancellationToken);
            if (response.State != FastbootResponseState.Success)
            {
                throw new FastbootException(response.State, $"oem {action.Args} failed: {response.Text}");
            }

            return true;
        });

        if (accepted && action.Args.Equals(SpacemiTConstants.OemSuperSpeed, StringComparison.Ordinal))
        {
            Report(progress, "action", null, "super-speed accepted, waiting for re-enumeration");
            return ReopenAfterReEnumeration(session, reopenSession, progress, cancellationToken);
        }

        return session;
    }

    private FastbootSession ReopenAfterReEnumeration(
        FastbootSession session,
        Func<FastbootSession, FastbootSession?> reopenSession,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_options.ReEnumerateSettleMs > 0)
        {
            Thread.Sleep(_options.ReEnumerateSettleMs);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fresh = reopenSession(session);
        if (fresh is null)
        {
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
        var targets = CollectTargets(table);
        if (targets.Count == 0)
        {
            Report(progress, "skip", null, "no target partitions in this table");
            return;
        }

        RunWithRetry($"flash {table.Format}", action.Retry, action.SkipFail, progress, null, () =>
        {
            using var stream = File.OpenRead(tablePath);
            session.DownloadData(stream, stream.Length, null, cancellationToken).ThrowIfFailed();
            session.ExecuteCommand(FastbootConstants.CommandFlash + table.Format).ThrowIfFailed();
            return true;
        });

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlashPartition(session, target, progress, cancellationToken);
        }
    }

    private List<(SpacemiTPartitionEntry Entry, string ImagePath, long Length)> CollectTargets(SpacemiTPartitionTable table)
    {
        var targets = new List<(SpacemiTPartitionEntry, string, long)>();
        foreach (var entry in table.Partitions)
        {
            if (entry.Images.Count == 0)
            {
                continue;
            }

            if (_options.OnlyPartitions is { Count: > 0 } only && !only.Contains(entry.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (_options.SkipPartitions.Contains(entry.Name, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var image in entry.Images)
            {
                var resolved = _package.ResolveFile(image);
                if (resolved is null)
                {
                    continue;
                }

                var fileSize = new FileInfo(resolved).Length;
                var length = entry.Size > 0 ? Math.Min(fileSize, entry.Size) : fileSize;
                if (length > 0)
                {
                    targets.Add((entry, resolved, length));
                }
            }
        }

        return targets;
    }

    private void FlashPartition(
        FastbootSession session,
        (SpacemiTPartitionEntry Entry, string ImagePath, long Length) target,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (entry, imagePath, length) = target;
        RunWithRetry($"flash {entry.Name}", 1, skipFail: false, progress, entry.Name, () =>
        {
            using var stream = File.OpenRead(imagePath);
            var maxDownload = session.GetMaxDownloadSize();
            if (HasSparseHeader(stream))
            {
                if (length > maxDownload)
                {
                    FlashResparseChunks(session, imagePath, entry.Name, maxDownload, progress, cancellationToken);
                    return true;
                }

                Report(progress, "flash-partition", entry.Name, $"flashing sparse image ({length} bytes)");
                session.DownloadData(stream, length, CreateByteProgress(progress, "flash-partition", entry.Name, length), cancellationToken)
                    .ThrowIfFailed();
                session.ExecuteCommand(FastbootConstants.CommandFlash + entry.Name).ThrowIfFailed();
                return true;
            }

            if (length <= maxDownload)
            {
                Report(progress, "flash-partition", entry.Name, $"flashing {length} bytes");
                session.DownloadData(stream, length, CreateByteProgress(progress, "flash-partition", entry.Name, length), cancellationToken)
                    .ThrowIfFailed();
                session.ExecuteCommand(FastbootConstants.CommandFlash + entry.Name).ThrowIfFailed();
                return true;
            }

            FlashGzipChunks(session, entry.Name, stream, length, maxDownload, progress, cancellationToken);
            return true;
        });
    }

    private void FlashGzipChunks(
        FastbootSession session,
        string partition,
        FileStream stream,
        long length,
        long maxDownload,
        IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "flash-partition", partition,
            $"image exceeds max-download-size {maxDownload}; flashing {length} bytes as gzip chunks");

        var chunkBytes = (int)Math.Min(Math.Min(maxDownload, MaxGzipChunkBytes) - GzipChunkHeadroom, int.MaxValue);
        if (chunkBytes <= 0)
        {
            throw new FastbootException(FastbootResponseState.Fail,
                $"max-download-size {maxDownload} is too small for gzip chunking");
        }

        var buffer = new byte[chunkBytes];
        var compressionLevel = _options.GzipLevel switch
        {
            <= 2 => 1,
            <= 6 => 6,
            _ => 9,
        };
        var compressionOptions = new CompressionOptions { Level = compressionLevel };

        long sent = 0;
        while (sent < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = (int)Math.Min(chunkBytes, length - sent);
            var read = stream.Read(buffer, 0, raw);
            if (read <= 0)
            {
                throw new EndOfStreamException($"image ended early at {sent}/{length}");
            }

            sent += read;
            using var compressed = new MemoryStream();
            CompressionService.Compress(
                new MemoryStream(buffer, 0, read, writable: false),
                compressed,
                CompressionFormat.Gzip,
                compressionOptions);
            compressed.Position = 0;
            session.DownloadData(compressed, compressed.Length, null, cancellationToken).ThrowIfFailed();
            session.ExecuteCommand(FastbootConstants.CommandFlash + partition).ThrowIfFailed();
            Report(progress, "flash-partition", partition, $"gzip chunk sent, {sent}/{length} bytes");
        }
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
                session.DownloadData(chunk, chunkLength, CreateByteProgress(progress, "flash-partition", partition, totalBytes), cancellationToken)
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
            session.DownloadData(stream, stream.Length, CreateByteProgress(progress, "flash-partition", action.Partition, stream.Length), cancellationToken)
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

    private static IProgress<long>? CreateByteProgress(
        IProgress<SpacemiTFlashProgress>? progress, string phase, string? partition, long total) =>
        progress is null ? null : new ByteProgressAdapter(progress, phase, partition, total);

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