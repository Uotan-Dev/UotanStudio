using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Erases a Legacy eMMC range using the explicitly selected SDK status-led progress profile.
    /// <para>使用显式选定的 SDK 状态进度配置擦除 Legacy eMMC 范围。</para></summary>
    /// <param name="partitionId">The discovered eMMC region, excluding RPMB.
    /// <para>已发现的 eMMC 区域，不含 RPMB。</para></param>
    /// <param name="offset">The nonnegative byte offset aligned to 512.
    /// <para>按 512 对齐的非负字节偏移。</para></param>
    /// <param name="length">The positive byte length aligned to 512.
    /// <para>按 512 对齐的正数字节长度。</para></param>
    /// <param name="options">The explicit SDK wire and completion profile.
    /// <para>显式的 SDK 线协议和完成配置。</para></param>
    /// <param name="timeoutMs">The whole-operation deadline from 1 ms through 24 hours.
    /// <para>1 毫秒至 24 小时的完整操作截止时间。</para></param>
    /// <param name="progress">Percentages; 100 is published only after final completion.
    /// <para>进度百分比；仅在最终完成后发布 100。</para></param>
    /// <param name="cancellationToken">Cancellation for the complete exchange.
    /// <para>完整交换过程的取消令牌。</para></param>
    /// <returns>The validated device statistics, or null if the agent acknowledged without statistics.
    /// <para>经校验的设备统计；代理未提供统计而直接确认时为 null。</para></returns>
    public Task<MtkLegacyEraseStatistics?> EraseLegacyStorageAsync(byte partitionId, long offset, long length,
        MtkLegacyEraseOptions options, int timeoutMs = 120000, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) =>
        EraseLegacySdkStorageAsync(MtkXFlashStorageKind.Emmc, partitionId, offset, length, 512, null,
            options, timeoutMs, progress, cancellationToken);

    /// <summary>Erases a block-aligned Legacy NOR range using the explicit SDK profile.
    /// <para>使用显式 SDK 配置擦除按块对齐的 Legacy NOR 范围。</para></summary>
    /// <remarks>The inventory does not provide erase-block size; the caller must supply a matching device profile.
    /// OTP is excluded. No eMMC partition switch, retry or host readback is performed.
    /// <para>清单不提供擦除块大小；调用方必须提供匹配设备的配置。不包括 OTP。
    /// 不执行 eMMC 分区切换、重试或主机回读。</para></remarks>
    /// <param name="offset">The nonnegative erase-block-aligned byte offset.
    /// <para>按擦除块对齐的非负字节偏移。</para></param>
    /// <param name="length">The positive erase-block-aligned byte length.
    /// <para>按擦除块对齐的正数字节长度。</para></param>
    /// <param name="eraseBlockSize">The caller-selected block size from 1 through 64 MiB.
    /// <para>调用方选择的 1 至 64 MiB 范围内的块大小。</para></param>
    /// <param name="options">The explicit SDK wire and completion profile.
    /// <para>显式的 SDK 线协议和完成配置。</para></param>
    /// <param name="timeoutMs">The whole-operation deadline from 1 ms through 24 hours.
    /// <para>1 毫秒至 24 小时的完整操作截止时间。</para></param>
    /// <param name="progress">Percentages; 100 is published only after final completion.
    /// <para>进度百分比；仅在最终完成后发布 100。</para></param>
    /// <param name="cancellationToken">Cancellation for the complete exchange.
    /// <para>完整交换过程的取消令牌。</para></param>
    /// <returns>The validated device statistics, or null when none were sent.
    /// <para>经校验的设备统计；未发送统计时为 null。</para></returns>
    public Task<MtkLegacyEraseStatistics?> EraseLegacyNorStorageAsync(long offset, long length, int eraseBlockSize,
        MtkLegacyEraseOptions options, int timeoutMs = 120000, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) =>
        EraseLegacySdkStorageAsync(MtkXFlashStorageKind.Nor, 0, offset, length, eraseBlockSize, null,
            options, timeoutMs, progress, cancellationToken);

    /// <summary>Executes the explicitly requested Legacy NAND erase action on whole erase blocks.
    /// <para>对完整擦除块执行显式请求的 Legacy NAND 擦除动作。</para></summary>
    /// <remarks>Force erase and bad-block marking require explicit selection. Device error records fault the session even when the agent offers to continue. Acknowledgement is not proof that bad blocks were erased.
    /// <para>强制擦除和坏块标记必须显式选择。即使代理允许继续，设备错误记录仍会使会话故障。 确认不证明坏块已被擦除。</para></remarks>
    /// <param name="offset">The erase-block-aligned main-area offset.<para>按擦除块对齐的主区偏移。</para></param>
    /// <param name="length">The positive erase-block-aligned main-area span.
    /// <para>正数且按擦除块对齐的主区跨度。</para></param>
    /// <param name="options">The explicit wire, addressing and action policy.
    /// <para>显式的线协议、寻址和动作策略。</para></param>
    /// <param name="timeoutMs">The whole-operation deadline from 1 ms through 24 hours.
    /// <para>1 毫秒至 24 小时的完整操作截止时间。</para></param>
    /// <param name="progress">Percentages; 100 is published only after final completion.
    /// <para>进度百分比；仅在最终完成后发布 100。</para></param>
    /// <param name="cancellationToken">Cancellation for the complete exchange.
    /// <para>完整交换过程的取消令牌。</para></param>
    /// <returns>The validated device statistics, or null when none were sent.
    /// <para>经校验的设备统计；未发送统计时为 null。</para></returns>
    public Task<MtkLegacyEraseStatistics?> EraseLegacyNandStorageAsync(long offset, long length,
        MtkLegacyNandEraseOptions options, int timeoutMs = 120000, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return EraseLegacySdkStorageAsync(MtkXFlashStorageKind.Nand, 0, offset, length, 0, options,
            options.Wire, timeoutMs, progress, cancellationToken);
    }

    private async Task<MtkLegacyEraseStatistics?> EraseLegacySdkStorageAsync(MtkXFlashStorageKind kind, byte partitionId,
        long offset, long length, int blockSize, MtkLegacyNandEraseOptions? nandOptions, MtkLegacyEraseOptions options,
        int timeoutMs, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            var storage = LegacyStorage!;
            if (storage.Kind != kind)
            {
                throw new NotSupportedException("The Legacy erase command does not match the initialized storage.");
            }
            long capacity;
            if (kind == MtkXFlashStorageKind.Emmc)
            {
                storage.ValidateRange(partitionId, offset, length);
                capacity = storage.Regions[partitionId - 1].CapacityBytes;
            }
            else if (kind == MtkXFlashStorageKind.Nand)
            {
                var nand = storage.Nand!;
                capacity = nand.CapacityBytes;
                blockSize = nand.EraseBlockSize;
                var limit = nandOptions!.LogicalCapacityBytes ?? capacity;
                nand.ValidateRange(offset, length, limit);
                if (limit % blockSize != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(nandOptions), "The logical erase bound must be block-aligned.");
                }
            }
            else
            {
                storage.Nor!.ValidateRange(offset, length, false);
                capacity = storage.Nor.CapacityBytes;
            }
            if (blockSize is < 1 or > 64 * 1024 * 1024 || blockSize > capacity
                || offset % blockSize != 0 || length % blockSize != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blockSize), "The Legacy erase range must contain complete selected erase blocks.");
            }
            var header = new byte[21];
            header[0] = 0xD4;
            header[1] = kind switch { MtkXFlashStorageKind.Nor => 0, MtkXFlashStorageKind.Nand => 1, _ => 2 };
            header[2] = (byte)(nandOptions?.Mode ?? MtkLegacyNandEraseMode.Normal);
            header[3] = options.ValidateAfterErase ? (byte)1 : (byte)0;
            header[4] = (byte)(nandOptions?.AddressType ?? MtkLegacyNandAddressType.Logical);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(5), (ulong)offset);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(13), (ulong)length);
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                if (kind == MtkXFlashStorageKind.Emmc)
                {
                    _ = await QueryLegacyUsbSpeedOwnedAsync(token).ConfigureAwait(false);
                    await SwitchLegacyPartitionOwnedAsync(partitionId, token).ConfigureAwait(false);
                }
                await WriteAsync(header, _timeoutMs, token).ConfigureAwait(false);
                await RequireLegacySdkEraseAckOwnedAsync("allowance", timeoutMs, token).ConfigureAwait(false);
                await RequireLegacySdkEraseAckOwnedAsync("range", timeoutMs, token).ConfigureAwait(false);
                await ReadLegacySdkEraseProgressOwnedAsync(timeoutMs, progress, token).ConfigureAwait(false);
                var marker = (await ReadAsync(1, timeoutMs, token).ConfigureAwait(false))[0];
                MtkLegacyEraseStatistics? statistics = null;
                if (marker != 0x5A)
                {
                    if (marker != options.StatisticsMarker)
                    {
                        throw new MtkProtocolException("The Legacy erase completion marker does not match the selected profile.", marker);
                    }
                    var bytes = await ReadAsync(28, timeoutMs, token).ConfigureAwait(false);
                    statistics = MtkLegacyEraseStatistics.Parse(bytes, capacity, blockSize, offset, length,
                        nandOptions?.AddressType == MtkLegacyNandAddressType.Logical);
                    if (statistics.ErrorBlocks != 0)
                    {
                        throw new MtkProtocolException($"The Legacy erase statistics report {statistics.ErrorBlocks} error blocks.");
                    }
                }
                var completion = (await ReadAsync(1, timeoutMs, token).ConfigureAwait(false))[0];
                if (completion == 0xA5)
                {
                    completion = (await ReadAsync(1, timeoutMs, token).ConfigureAwait(false))[0];
                }
                if (completion != 0x5A)
                {
                    throw new MtkProtocolException("The Legacy erase post-process did not finish with ACK.", completion);
                }
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                progress?.Report(100);
                return statistics;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultLegacy();
                throw new TimeoutException("The Legacy SDK erase deadline expired; the target result is unknown.", error);
            }
            catch
            {
                FaultLegacy();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task RequireLegacySdkEraseAckOwnedAsync(string stage, int timeoutMs, CancellationToken token)
    {
        var value = (await ReadAsync(1, timeoutMs, token).ConfigureAwait(false))[0];
        if (value != 0x5A)
        {
            var status = BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, timeoutMs, token).ConfigureAwait(false));
            throw new MtkProtocolException($"Legacy SDK erase {stage} was rejected with 0x{value:X2}.", status);
        }
    }

    private async Task ReadLegacySdkEraseProgressOwnedAsync(int timeoutMs, IProgress<int>? progress, CancellationToken token)
    {
        var previous = -1;
        for (var reports = 0; reports < 4096; reports++)
        {
            var status = BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, timeoutMs, token).ConfigureAwait(false));
            if (status is not (0 or 0xBD1))
            {
                var address = BinaryPrimitives.ReadUInt64BigEndian(await ReadAsync(8, timeoutMs, token).ConfigureAwait(false));
                var disposition = (await ReadAsync(1, timeoutMs, token).ConfigureAwait(false))[0];
                throw new MtkProtocolException(
                    $"Legacy erase failed at address 0x{address:X16}, disposition 0x{disposition:X2}; no continuation was acknowledged.", status);
            }
            var percentage = (await ReadAsync(1, timeoutMs, token).ConfigureAwait(false))[0];
            if (percentage > 100 || percentage < previous)
            {
                throw new MtkProtocolException("Legacy SDK erase progress is out of range or decreased.");
            }
            previous = percentage;
            await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
            if (status == 0)
            {
                return;
            }
            progress?.Report(Math.Min((int)percentage, 99));
        }
        throw new MtkProtocolException("The Legacy SDK erase progress report limit was exceeded.");
    }
}