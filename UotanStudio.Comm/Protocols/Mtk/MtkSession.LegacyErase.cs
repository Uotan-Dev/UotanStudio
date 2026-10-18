using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Explicitly erases a validated Legacy eMMC range and requires both final acknowledgements.
    /// <para>显式擦除经过校验的 Legacy eMMC 范围，并要求两个最终确认。</para></summary>
    /// <remarks>Progress is bounded and monotonic. An uncertain result faults the session without retry; acknowledgement does not imply a readback check or rollback capability.
    /// <para>进度有界且单调。结果不确定时会话故障且不重试；确认不意味着回读检查或回滚能力。</para></remarks>
    /// <param name="partitionId">The physical eMMC region, excluding RPMB.
    /// <para>eMMC 物理区域，不含 RPMB。</para></param>
    /// <param name="offset">The nonnegative byte offset aligned to 512.
    /// <para>按 512 对齐的非负字节偏移。</para></param>
    /// <param name="length">The positive byte count aligned to 512.<para>按 512 对齐的正数字节数。</para></param>
    /// <param name="timeoutMs">The cooperative operation deadline, from 1 ms through 24 hours.
    /// <para>操作的协作式截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="progress">The reported percentage, where 100 still requires final acknowledgements.
    /// <para>上报的百分比；100 仍需最终确认。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation.<para>操作取消令牌。</para></param>
    /// <returns>A task completing after the final erase acknowledgements.
    /// <para>最终擦除确认后完成的任务。</para></returns>
    public async Task EraseLegacyStorageAsync(byte partitionId, long offset, long length, int timeoutMs = 120000,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EraseLegacyStorageOwnedAsync(partitionId, offset, length, timeoutMs, progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task EraseLegacyStorageOwnedAsync(byte partitionId, long offset, long length, int timeoutMs,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.LegacyStorageReady);
        LegacyStorage!.ValidateRange(partitionId, offset, length);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        try
        {
            _ = await QueryLegacyUsbSpeedOwnedAsync(token).ConfigureAwait(false);
            await SwitchLegacyPartitionOwnedAsync(partitionId, token).ConfigureAwait(false);
            var header = new byte[21];
            header[0] = 0xD4;
            header[1] = 2;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(5), (ulong)offset);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(13), (ulong)length);
            await WriteAsync(header, _timeoutMs, token).ConfigureAwait(false);
            var previous = -1;
            for (var reports = 0; reports < 4096; reports++)
            {
                await RequireLegacyByteAsync(0x5A, "erase progress", token).ConfigureAwait(false);
                await RequireLegacyByteAsync(0x5A, "erase progress header", token).ConfigureAwait(false);
                var status = await ReadLegacyWordAsync(token).ConfigureAwait(false);
                if (status is not (0 or 0xBD1))
                {
                    throw new MtkProtocolException("The Legacy erase progress contains a device error.", status);
                }
                var percentage = (await ReadAsync(1, _timeoutMs, token).ConfigureAwait(false))[0];
                if (percentage > 100 || percentage < previous)
                {
                    throw new MtkProtocolException("Legacy erase progress is out of range or decreased.");
                }
                previous = percentage;
                await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
                if (percentage == 100)
                {
                    await RequireLegacyByteAsync(0x5A, "erase completion", token).ConfigureAwait(false);
                    await RequireLegacyByteAsync(0x5A, "erase final completion", token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    progress?.Report(100);
                    return;
                }
                progress?.Report(percentage);
            }
            throw new MtkProtocolException("The Legacy erase progress report limit was exceeded.");
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            FaultLegacy();
            throw new TimeoutException("The Legacy erase deadline expired; the target result is unknown.", error);
        }
        catch
        {
            FaultLegacy();
            throw;
        }
    }
}