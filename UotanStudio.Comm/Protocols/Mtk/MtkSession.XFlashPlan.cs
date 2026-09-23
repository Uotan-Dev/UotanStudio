namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Executes a preflighted one-shot plan under one session operation lease. Failure stops
    /// subsequent operations without replay or rollback. Progress callbacks must not wait
    /// synchronously for another operation on this session.
    /// </summary>
    /// <param name="plan">A retained plan prepared against this session's exact storage snapshot.</param>
    /// <param name="progress">Optional phase-local progress; success is indicated by the returned result.</param>
    /// <param name="eraseTimeoutMs">The deadline for each erase range.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Completion totals, excluding sparse holes.</returns>
    public async Task<MtkFlashPlanResult> ExecuteXFlashPlanAsync(MtkFlashPlan plan,
        IProgress<MtkFlashPlanProgress>? progress = null, int eraseTimeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eraseTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(eraseTimeoutMs, 86400000);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(plan.Storage, XFlashStorage))
            {
                throw new InvalidOperationException("The plan was prepared against a different storage snapshot.");
            }
            var operations = plan.BeginExecution();
            var succeeded = false;
            try
            {
                // Recheck measurable source extents before the first destructive command.
                foreach (var prepared in operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (prepared.Source is { } source && (!source.CanRead || !source.CanSeek
                        || prepared.SourceOffset > source.Length || prepared.Description.Length > source.Length - prepared.SourceOffset))
                    {
                        throw new InvalidDataException("A retained plan source no longer contains its requested range.");
                    }
                }
                if (plan.GptSnapshot is { } expected)
                {
                    await RevalidateXFlashGptOwnedAsync(expected, cancellationToken).ConfigureAwait(false);
                }
                long written = 0;
                long erased = 0;
                long verified = 0;
                for (var index = 0; index < operations.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var prepared = operations[index];
                    var operation = prepared.Description;
                    var phase = operation.IsErase ? MtkFlashPhase.Erasing : MtkFlashPhase.Writing;
                    var reporter = new PlanByteProgress(progress, index, operations.Count, operation, phase);
                    reporter.Report(0);
                    if (operation.IsErase)
                    {
                        await EraseXFlashStorageOwnedAsync(operation.PartitionId, operation.Offset, operation.Length,
                            eraseTimeoutMs, cancellationToken).ConfigureAwait(false);
                        erased = checked(erased + operation.Length);
                    }
                    else
                    {
                        var source = prepared.Source ?? throw new InvalidDataException("A write operation has no retained source.");
                        source.Position = prepared.SourceOffset;
                        await WriteXFlashStorageOwnedAsync(operation.PartitionId, operation.Offset, operation.Length,
                            source, reporter, cancellationToken).ConfigureAwait(false);
                        written = checked(written + operation.Length);
                        if (operation.Verify)
                        {
                            source.Position = prepared.SourceOffset;
                            var verificationProgress = new PlanByteProgress(progress, index, operations.Count, operation, MtkFlashPhase.Verifying);
                            verificationProgress.Report(0);
                            using var comparison = new StorageComparisonStream(source);
                            await ReadXFlashStorageOwnedAsync(operation.PartitionId, operation.Offset, operation.Length,
                                comparison, verificationProgress, cancellationToken).ConfigureAwait(false);
                            if (comparison.FirstMismatchOffset is { } mismatch)
                            {
                                throw new InvalidDataException($"Readback mismatch in step {operation.StepIndex} at physical byte {checked(operation.Offset + mismatch)}.");
                            }
                            verified = checked(verified + operation.Length);
                        }
                    }
                    progress?.Report(new MtkFlashPlanProgress(index, operations.Count, operation.StepIndex,
                        MtkFlashPhase.OperationCompleted, operation.Length, operation.Length));
                }
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                succeeded = true;
                return new MtkFlashPlanResult(operations.Count, written, erased, verified);
            }
            finally
            {
                plan.FinishExecution(succeeded);
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private sealed class PlanByteProgress(IProgress<MtkFlashPlanProgress>? progress, int index, int count,
        MtkFlashOperation operation, MtkFlashPhase phase) : IProgress<long>
    {
        public void Report(long value) => progress?.Report(new MtkFlashPlanProgress(index, count, operation.StepIndex,
            phase, value, operation.Length));
    }
}