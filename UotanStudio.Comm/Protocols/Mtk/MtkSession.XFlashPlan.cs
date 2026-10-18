namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Executes a preflighted one-shot plan under one session operation lease. Failure stops subsequent operations without replay or rollback. Progress callbacks must not wait synchronously for another operation on this session.
    /// <para>在单个会话操作租约下执行预先校验过的一次性计划。失败会中止后续操作， 且不会重放或回滚。进度回调不得同步等待本会话的另一个操作。</para></summary>
    /// <param name="plan">A retained plan prepared against this session's exact storage snapshot.
    /// <para>基于本会话确切存储快照准备的、已被持有的计划。</para></param>
    /// <param name="progress">Optional phase-local progress; success is indicated by the returned result.
    /// <para>可选的阶段内进度；是否成功以返回结果为准。</para></param>
    /// <param name="eraseTimeoutMs">The deadline for each erase range.<para>每个擦除范围的截止时间。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>Completion totals, excluding sparse holes.<para>完成总量，不含稀疏空洞。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.
    /// <para><paramref name="plan"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="eraseTimeoutMs"/> is outside 1 ms to 24 hours.
    /// <para><paramref name="eraseTimeoutMs"/> 超出 1 毫秒到 24 小时的范围。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready or the plan was prepared against a different storage snapshot.
    /// <para>存储未就绪，或计划针对的是另一份存储快照。</para></exception>
    /// <exception cref="InvalidDataException">A retained source lost its range or a readback mismatch occurred.
    /// <para>被持有的源缺少其请求的范围，或回读比对不匹配。</para></exception>
    /// <exception cref="MtkProtocolException">A storage or erase exchange failed; the session faults.
    /// <para>存储或擦除交换失败；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before or between operations.
    /// <para>调用方在操作之前或操作之间取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
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
            if (StorageCapability is { Source: Storage.MtkStorageDiscoverySource.XFlash } capability
                && !capability.GeometryEquals(plan.Capability))
            {
                throw new InvalidOperationException("The plan capability no longer matches the published storage capability.");
            }
            var operations = plan.BeginExecution();
            var succeeded = false;
            try
            {
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