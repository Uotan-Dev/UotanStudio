using System.Diagnostics;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>Identifies independently measured phases of filesystem extraction.</summary>
public enum FileSystemWorkStage { Preparing, Extracting, Preserving, VerifyingImage, VerifyingFiles, ReadingMetadata }

/// <summary>Reports actual bytes processed and the total for the current phase, not image counts.</summary>
public readonly record struct FileSystemByteProgress(FileSystemWorkStage Stage, long Processed, long Total)
{
    /// <summary>Tags byte measurements with their current extraction phase.</summary>
    public static IProgress<(long Processed, long Total)> ForStage(IProgress<FileSystemByteProgress>? progress, FileSystemWorkStage stage)
        => new FileSystemStageProgress(progress, stage);
}

/// <summary>Aggregates parallel writes and bounds notification frequency without estimating byte counts.</summary>
internal sealed class ExtractionByteCounter
{
    private readonly object gate = new();
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

    internal void Add(int count)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            processed = checked(processed + count);
            if (Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds < 100) return;
            lastReport = Stopwatch.GetTimestamp();
            progress?.Report((processed, total));
        }
    }

    internal void Complete()
    {
        lock (gate) progress?.Report((processed, total));
    }
}

/// <summary>Tags synchronous backend measurements before the UI schedules their delivery.</summary>
internal sealed class FileSystemStageProgress(IProgress<FileSystemByteProgress>? progress, FileSystemWorkStage stage)
    : IProgress<(long Processed, long Total)>
{
    public void Report((long Processed, long Total) value) => progress?.Report(new(stage, value.Processed, value.Total));
}