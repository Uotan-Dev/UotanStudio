namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const int LegacyPlanTransferTimeoutMs = 120000;

    /// <summary>Executes a preflighted one-shot Legacy plan under a single session operation lease. Failure stops the remaining operations without replay or rollback.
    /// <para>在单个会话操作租约下执行已预检的一次性 Legacy 计划。失败会中止剩余操作， 且不会重放或回滚。</para></summary>
    /// <remarks>
    /// The plan must reference this session's exact storage inventory. A plan derived from a GPT observation re-reads that table before the first device write and refuses to start when the metadata changed. Progress callbacks must not wait synchronously for another operation on this session.
    /// <para>计划必须引用本会话确切的存储清单。由 GPT 观测结果构建的计划会在首次设备写入前重新读取该表， 元数据变化时拒绝开始。进度回调不得同步等待本会话的另一个操作。</para>
    /// </remarks>
    /// <param name="plan">A retained plan prepared against this session's Legacy inventory.
    /// <para>基于本会话 Legacy 清单准备的、已被持有的计划。</para></param>
    /// <param name="progress">Optional phase-local progress; success is indicated by the returned result.
    /// <para>可选的阶段内进度；是否成功以返回结果为准。</para></param>
    /// <param name="eraseTimeoutMs">The deadline for each erase range.<para>每个擦除范围的截止时间。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>Completion totals, excluding readback.<para>完成总量，不含回读。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.
    /// <para><paramref name="plan"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="eraseTimeoutMs"/> is outside 1 ms to 24 hours.
    /// <para><paramref name="eraseTimeoutMs"/> 超出 1 毫秒至 24 小时的范围。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready, or the plan belongs to a different storage inventory.
    /// <para>Legacy 存储未就绪，或计划属于另一份存储清单。</para></exception>
    /// <exception cref="InvalidDataException">A retained source lost its range or a readback mismatch occurred.
    /// <para>被持有的源缺少其范围，或回读比对不匹配。</para></exception>
    /// <exception cref="MtkProtocolException">A storage or erase exchange failed; the session faults.
    /// <para>某次存储或擦除交换失败；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before or between operations.
    /// <para>调用方在操作之前或操作之间取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkFlashPlanResult> ExecuteLegacyPlanAsync(MtkLegacyFlashPlan plan,
        IProgress<MtkFlashPlanProgress>? progress = null, int eraseTimeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eraseTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(eraseTimeoutMs, 86400000);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(plan.Storage, LegacyStorage))
            {
                throw new InvalidOperationException(
                    "The Legacy plan was prepared against a different storage inventory.");
            }

            var operations = plan.BeginExecution();
            var succeeded = false;
            try
            {
                foreach (var prepared in operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (prepared.Source is { } source && (!source.CanRead || !source.CanSeek
                        || prepared.SourceOffset > source.Length
                        || prepared.Description.Length > source.Length - prepared.SourceOffset))
                    {
                        throw new InvalidDataException(
                            "A retained Legacy plan source no longer contains its requested range.");
                    }
                }

                if (plan.GptSnapshot is { } expected)
                {
                    await RevalidateLegacyGptOwnedAsync(expected, cancellationToken).ConfigureAwait(false);
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
                    var reporter = new LegacyPlanByteProgress(progress, index, operations.Count, operation, phase);
                    reporter.Report(0);
                    if (operation.IsErase)
                    {
                        await EraseLegacyStorageOwnedAsync(operation.PartitionId, operation.Offset, operation.Length,
                            eraseTimeoutMs, null, cancellationToken).ConfigureAwait(false);
                        erased = checked(erased + operation.Length);
                    }
                    else
                    {
                        var source = prepared.Source ?? throw new InvalidDataException(
                            "A Legacy write operation has no retained source.");
                        source.Position = prepared.SourceOffset;
                        await TransferLegacyStorageOwnedAsync(true, operation.PartitionId, operation.Offset,
                            operation.Length, source, LegacyPlanTransferTimeoutMs, reporter, cancellationToken,
                            nor: false).ConfigureAwait(false);
                        written = checked(written + operation.Length);
                        if (operation.Verify)
                        {
                            source.Position = prepared.SourceOffset;
                            var verificationProgress = new LegacyPlanByteProgress(progress, index, operations.Count,
                                operation, MtkFlashPhase.Verifying);
                            verificationProgress.Report(0);
                            using var comparison = new StorageComparisonStream(source);
                            await TransferLegacyStorageOwnedAsync(false, operation.PartitionId, operation.Offset,
                                operation.Length, comparison, LegacyPlanTransferTimeoutMs, verificationProgress,
                                cancellationToken, nor: false).ConfigureAwait(false);
                            if (comparison.FirstMismatchOffset is { } mismatch)
                            {
                                throw new InvalidDataException(
                                    $"Legacy readback mismatch in step {operation.StepIndex} at physical byte {checked(operation.Offset + mismatch)}.");
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

    private async Task RevalidateLegacyGptOwnedAsync(MtkLegacyGptSnapshot expected,
        CancellationToken cancellationToken)
    {
        var current = await ReadLegacyGptOwnedAsync(expected.PartitionId, cancellationToken)
            .ConfigureAwait(false);
        if (current.Table.MetadataSha256 != expected.Table.MetadataSha256
            || current.Table.BlockSize != expected.Table.BlockSize
            || current.Table.BlockCount != expected.Table.BlockCount)
        {
            throw new InvalidDataException(
                "The Legacy GPT metadata changed after preparation; no partition operation was started.");
        }
    }

    private sealed class LegacyPlanByteProgress(IProgress<MtkFlashPlanProgress>? progress, int index, int count,
        MtkLegacyFlashOperation operation, MtkFlashPhase phase) : IProgress<long>
    {
        public void Report(long value) => progress?.Report(new MtkFlashPlanProgress(index, count, operation.StepIndex,
            phase, value, operation.Length));
    }
}