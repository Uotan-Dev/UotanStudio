using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Explicit operations supported by an XFlash plan.</summary>
public enum MtkFlashStepKind
{
    /// <summary>Write an exact raw source range.</summary>
    Raw,
    /// <summary>Write RAW/FILL sparse extents while preserving DONT_CARE ranges.</summary>
    Sparse,
    /// <summary>Request device-side erase for the exact range.</summary>
    Erase
}

/// <summary>One explicitly selected operation with byte-based addressing.</summary>
/// <param name="Kind">The operation type; image type is not guessed from a filename.</param>
/// <param name="PartitionId">The discovered XFlash physical partition identifier.</param>
/// <param name="Offset">The target byte offset.</param>
/// <param name="Length">The raw/erase byte count or sparse target capacity.</param>
/// <param name="SourcePath">The raw/sparse source path; null for erase.</param>
/// <param name="SourceOffset">The raw source byte offset; must be zero for sparse/erase.</param>
/// <param name="Verify">Whether written extents must be read back; invalid for erase.</param>
public sealed record MtkFlashStep(MtkFlashStepKind Kind, uint PartitionId, long Offset, long Length,
    string? SourcePath = null, long SourceOffset = 0, bool Verify = false);

/// <summary>A fully preflighted physical operation; sparse holes do not appear in this list.</summary>
/// <param name="StepIndex">The zero-based originating step index.</param>
/// <param name="PartitionId">The XFlash physical partition identifier.</param>
/// <param name="Offset">The target byte offset.</param>
/// <param name="Length">The exact byte count.</param>
/// <param name="IsErase">Whether the operation erases rather than writes.</param>
/// <param name="Verify">Whether a completed write requires readback.</param>
public sealed record MtkFlashOperation(int StepIndex, uint PartitionId, long Offset, long Length, bool IsErase, bool Verify);

/// <summary>One-shot lifecycle for a retained flash plan.</summary>
public enum MtkFlashPlanState
{
    /// <summary>Prepared and not yet started.</summary>
    Prepared,
    /// <summary>Owned by an executing session; disposal is rejected.</summary>
    Running,
    /// <summary>Every operation and requested readback completed.</summary>
    Completed,
    /// <summary>Execution failed or was cancelled after starting; the plan cannot be replayed.</summary>
    Failed,
    /// <summary>Retained sources have been released.</summary>
    Disposed
}

/// <summary>
/// A bounded preflight result retaining all selected image handles. Preparation performs no
/// device I/O. Dispose after execution or an abandoned preview; source contents are not immutable snapshots.
/// </summary>
public sealed partial class MtkFlashPlan : IDisposable
{
    private readonly List<Stream> _sources = [];
    private readonly List<PreparedOperation> _prepared = [];
    private bool _disposed;
    private int _sparseChunkCount;
    private readonly object _lifecycleGate = new();
    private MtkFlashPlanState _state;

    private MtkFlashPlan(MtkXFlashStorageInfo storage) => Storage = storage;

    /// <summary>Gets the exact discovered geometry used to validate this plan.</summary>
    public MtkXFlashStorageInfo Storage { get; }
    /// <summary>Gets the read-only executable ranges after complete preparation.</summary>
    public IReadOnlyList<MtkFlashOperation> Operations { get; private set; } = Array.Empty<MtkFlashOperation>();
    /// <summary>Gets the checked sum of actual write/erase bytes, excluding readback and sparse holes.</summary>
    public long TotalBytes { get; private set; }
    /// <summary>Gets the current one-shot lifecycle state.</summary>
    public MtkFlashPlanState State { get { lock (_lifecycleGate) { return _state; } } }

    internal IReadOnlyList<PreparedOperation> BeginExecution()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != MtkFlashPlanState.Prepared)
            {
                throw new InvalidOperationException("A flash plan can be executed only once.");
            }
            _state = MtkFlashPlanState.Running;
            return _prepared;
        }
    }

    internal void FinishExecution(bool succeeded)
    {
        lock (_lifecycleGate)
        {
            _state = succeeded ? MtkFlashPlanState.Completed : MtkFlashPlanState.Failed;
        }
    }

    internal IReadOnlyList<PreparedOperation> Prepared
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _prepared;
        }
    }

    /// <summary>Validates all steps, opens their sources and expands bounded sparse extents without device commands.</summary>
    /// <param name="storage">Validated device geometry for the intended session.</param>
    /// <param name="steps">At most 256 explicitly ordered steps; no hidden erase or padding is added.</param>
    /// <param name="cancellationToken">The preflight cancellation token.</param>
    /// <returns>An owner of source handles and at most 65536 executable extents.</returns>
    public static MtkFlashPlan Prepare(MtkXFlashStorageInfo storage, IReadOnlyList<MtkFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "A flash plan requires between one and 256 steps.");
        }
        var snapshot = steps.ToArray();
        var plan = new MtkFlashPlan(storage);
        try
        {
            for (var index = 0; index < snapshot.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = snapshot[index] ?? throw new ArgumentException("A flash step is null.", nameof(steps));
                if (!Enum.IsDefined(step.Kind))
                {
                    throw new ArgumentOutOfRangeException(nameof(steps), "An operation type is unsupported.");
                }
                storage.ValidateRange(step.PartitionId, step.Offset, step.Length);
                ArgumentOutOfRangeException.ThrowIfNegative(step.SourceOffset);
                if (step.Kind == MtkFlashStepKind.Erase)
                {
                    if (step.SourcePath is not null || step.SourceOffset != 0 || step.Verify)
                    {
                        throw new ArgumentException("Erase steps cannot carry a source or claim zero-fill verification.", nameof(steps));
                    }
                    plan.Add(index, step, step.Offset, step.Length, null, 0);
                    continue;
                }
                ArgumentException.ThrowIfNullOrWhiteSpace(step.SourcePath);
                var source = new FileStream(step.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                plan._sources.Add(source);
                if (step.Kind == MtkFlashStepKind.Raw)
                {
                    if (step.SourceOffset > source.Length || step.Length > source.Length - step.SourceOffset)
                    {
                        throw new InvalidDataException("The raw image is shorter than the selected source range.");
                    }
                    plan.Add(index, step, step.Offset, step.Length, source, step.SourceOffset);
                    continue;
                }
                if (step.SourceOffset != 0)
                {
                    throw new InvalidDataException("Sparse plans do not support raw source offsets.");
                }
                var image = SparseReader.FromStream(source, true, false, 65536, cancellationToken, step.Length);
                var logical = new SparseReadStream(image, requireExactRawData: true);
                plan._sources.Add(logical);
                plan._sparseChunkCount = checked(plan._sparseChunkCount + image.Chunks.Count);
                if (plan._sparseChunkCount > 65536)
                {
                    throw new InvalidDataException("The plan exceeds the aggregate sparse-chunk limit.");
                }
                var logicalLength = checked((long)image.Header.TotalBlocks * image.Header.BlockSize);
                if (source.Position != source.Length || logicalLength <= 0 || logicalLength > step.Length
                    || logicalLength % storage.BlockSize != 0)
                {
                    throw new InvalidDataException("The sparse image has trailing bytes or an invalid target extent.");
                }
                foreach (var chunk in image.Chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = checked((long)chunk.StartBlock * image.Header.BlockSize);
                    var length = checked((long)chunk.Header.ChunkSize * image.Header.BlockSize);
                    if (offset % storage.BlockSize != 0 || length % storage.BlockSize != 0)
                    {
                        throw new InvalidDataException("Sparse chunk boundaries are not device-block aligned.");
                    }
                    if (chunk.Header.ChunkType == (ushort)ChunkType.DontCare || length == 0)
                    {
                        continue;
                    }
                    plan.Add(index, step, checked(step.Offset + offset), length, logical, offset);
                }
            }
            plan.Operations = Array.AsReadOnly(plan._prepared.Select(operation => operation.Description).ToArray());
            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    private void Add(int index, MtkFlashStep step, long offset, long length, Stream? source, long sourceOffset)
    {
        if (_prepared.Count >= 65536)
        {
            throw new InvalidDataException("The plan exceeds the aggregate executable-extent limit.");
        }
        Storage.ValidateRange(step.PartitionId, offset, length);
        TotalBytes = checked(TotalBytes + length);
        _prepared.Add(new PreparedOperation(new MtkFlashOperation(index, step.PartitionId, offset, length,
            step.Kind == MtkFlashStepKind.Erase, step.Verify), source, sourceOffset));
    }

    /// <summary>Closes retained sources; the plan must not be disposed concurrently with execution.</summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }
            if (_state == MtkFlashPlanState.Running)
            {
                throw new InvalidOperationException("An executing plan cannot be disposed.");
            }
            _disposed = true;
            _state = MtkFlashPlanState.Disposed;
            for (var index = _sources.Count - 1; index >= 0; index--)
            {
                _sources[index].Dispose();
            }
            _sources.Clear();
        }
    }

    internal sealed record PreparedOperation(MtkFlashOperation Description, Stream? Source, long SourceOffset);
}