using System.Diagnostics;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Identifies independently measured phases of filesystem build/extraction work.
/// <para>标识文件系统构建/解压工作中各自独立计量的阶段。</para>
/// </summary>
public enum FileSystemWorkStage
{
    /// <summary>Staging work that runs before image content is read or written. <para>在读取或写入镜像内容之前进行的准备性工作。</para></summary>
    Preparing = 0,

    /// <summary>Writing extracted file bytes from an image to disk. <para>把镜像中的文件字节解压写出到磁盘。</para></summary>
    Extracting = 1,

    /// <summary>Reading and comparing image-level regions for integrity. <para>读取并比对镜像级区域以校验完整性。</para></summary>
    VerifyingImage = 3,

    /// <summary>Reading and comparing extracted files for integrity. <para>读取并比对已解压文件以校验完整性。</para></summary>
    VerifyingFiles = 4,

    /// <summary>Reading filesystem metadata structures. <para>读取文件系统元数据结构。</para></summary>
    ReadingMetadata = 5,

    /// <summary>Writing a rebuilt filesystem image. <para>写出重建后的文件系统镜像。</para></summary>
    BuildingImage = 6,

    /// <summary>Writing block-image payload output. <para>写出块镜像载荷输出。</para></summary>
    WritingDat = 7,

    /// <summary>Compressing finished output. <para>压缩已生成的输出。</para></summary>
    Compressing = 8,
}

/// <summary>
/// Reports actual bytes processed and the total for the current phase, not image counts.
/// <para>报告当前阶段实际处理的字节数与总量，而非镜像条目数。</para>
/// </summary>
/// <param name="Stage">The phase the measurements belong to. <para>该次计量所属的阶段。</para></param>
/// <param name="Processed">Bytes handled so far in this phase. <para>本阶段已处理的字节数。</para></param>
/// <param name="Total">Bytes expected for this phase, or <c>0</c> when the size is unknown. <para>本阶段预期的字节数；大小未知时为 <c>0</c>。</para></param>
public readonly record struct FileSystemByteProgress(FileSystemWorkStage Stage, long Processed, long Total)
{
    /// <summary>
    /// Tags byte measurements with their current extraction phase.
    /// <para>为字节计量标注其当前解压阶段。</para>
    /// </summary>
    /// <param name="progress">The sink receiving staged reports; <see langword="null"/> discards them. <para>接收带阶段标注报告的目标；为 <see langword="null"/> 时丢弃报告。</para></param>
    /// <param name="stage">The phase attached to every report. <para>附加到每条报告上的阶段。</para></param>
    /// <returns>
    /// A progress adapter that forwards raw byte tuples tagged with <paramref name="stage"/>.
    /// <para>一个进度适配器，把原始字节元组加上 <paramref name="stage"/> 标注后转发。</para>
    /// </returns>
    public static IProgress<(long Processed, long Total)> ForStage(IProgress<FileSystemByteProgress>? progress, FileSystemWorkStage stage)
        => new FileSystemStageProgress(progress, stage);
}

/// <summary>Aggregates parallel writes and bounds notification frequency without estimating byte counts. <para>聚合并行写入，限制通知频率，且不估算字节总数。</para></summary>
/// <remarks>
/// The counter is updated with one interlocked add and the throttle window is claimed with one compare-exchange,
/// so a write never waits for another worker's write. A monitor around this path would serialize every extracted
/// block of every worker: the extraction writes one block at a time, so a lock here costs one contended
/// acquisition per block and is the difference between near-linear and flat parallel scaling.
/// <para>计数器以一次互锁加法更新，节流窗口以一次比较交换抢占，因此一次写入从不等候另一个工作者的写入。
/// 若在该路径上加监视器，会把每个工作者的每个解出块串行化：解包每次只写一个块，因此这里的锁会让每块多出一次
/// 争用获取，直接决定并行扩展是接近线性还是持平。</para>
/// </remarks>
internal sealed class ExtractionByteCounter
{
    private readonly IProgress<(long Processed, long Total)>? progress;
    private readonly CancellationToken token;
    private readonly long total;
    private long processed;
    private long lastReport = Stopwatch.GetTimestamp();

    internal ExtractionByteCounter(long total, IProgress<(long Processed, long Total)>? progress, CancellationToken token = default)
    {
        this.total = total;
        this.progress = progress;
        this.token = token;
        token.ThrowIfCancellationRequested();
        progress?.Report((0, total));
    }

    internal Stream Wrap(Stream output) => new CountingWriteStream(output, long.MaxValue, leaveOpen: true,
        cancellationToken: token, bytesWritten: Add);

    internal void Add(int count) => Add((long)count);

    /// <summary>
    /// Adds processed bytes and notifies at most once per throttle window. The reported value never exceeds the
    /// expected total, because a writer may rewrite metadata regions or padding that the total does not model.
    /// <para>累加已处理字节，且每个节流窗口最多通知一次。上报值不会超过预期总量，因为写出方可能重写总量未计入的
    /// 元数据区域或填充区。</para>
    /// </summary>
    /// <param name="count">Bytes to add. <para>要累加的字节数。</para></param>
    internal void Add(long count)
    {
        token.ThrowIfCancellationRequested();
        var processedNow = Interlocked.Add(ref processed, count);
        var stamp = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref lastReport);
        if (Stopwatch.GetElapsedTime(last, stamp).TotalMilliseconds < 100)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastReport, stamp, last) != last)
        {
            return;
        }

        progress?.Report((Math.Min(processedNow, total), total));
    }

    internal void Complete()
        => progress?.Report((Math.Min(Volatile.Read(ref processed), total), total));
}

/// <summary>Tags synchronous backend measurements before the UI schedules their delivery. <para>在 UI 安排交付之前标记同步后端测量值。</para></summary>
internal sealed class FileSystemStageProgress(IProgress<FileSystemByteProgress>? progress, FileSystemWorkStage stage)
    : IProgress<(long Processed, long Total)>
{
    public void Report((long Processed, long Total) value) => progress?.Report(new(stage, value.Processed, value.Total));
}

/// <summary>
/// Forwards staged measurements while a surrounding container owns one stage itself. A sparse container reports
/// the image-level verification from its own streams, so the inner filesystem it cascades into must not also
/// report that stage against its own denominator; every other stage passes through unchanged.
/// <para>转发分阶段计量，但由上层容器自行负责某一个阶段。稀疏容器用自己的流上报镜像级校验，因此它级联进来的
/// 内部文件系统不得再用自己的分母上报同一阶段；其余阶段原样通过。</para>
/// </summary>
internal sealed class StageSuppressingProgress(IProgress<FileSystemByteProgress>? progress, FileSystemWorkStage suppressed)
    : IProgress<FileSystemByteProgress>
{
    /// <summary>Reports a measurement unless it belongs to the stage the surrounding owner controls.
    /// <para>除非该计量属于上层拥有者的阶段，否则上报该计量。</para></summary>
    /// <param name="value">Staged measurement produced by the inner format.
    /// <para>内部格式产生的分阶段计量。</para></param>
    public void Report(FileSystemByteProgress value)
    {
        if (progress is not null && value.Stage != suppressed)
        {
            progress.Report(value);
        }
    }
}