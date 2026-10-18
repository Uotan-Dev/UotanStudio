using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk.Storage;
using UotanStudio.Hash.Sums;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads an aligned physical storage range to a caller-owned stream. The destination may contain partial data on failure; callers publishing files must use temporary files. NOR ranges smaller than the status-frame width are read through a bounded aligned window.
    /// <para>将对齐的物理存储范围读取到调用方拥有的流中。失败时目标流可能包含部分 数据；发布文件的调用方必须使用临时文件。小于状态帧宽度的 NOR 范围通过有界对齐窗口读取。</para></summary>
    /// <remarks><see cref="XFlashStorageTransferTimeoutMs"/> bounds the entire cooperative exchange. At most 64 consecutive success-status frames may arrive without data.
    /// <para><see cref="XFlashStorageTransferTimeoutMs"/> 限制整次协作式交换的时间。 未收到数据时最多接受连续 64 个成功状态帧。</para></remarks>
    /// <param name="partitionId">The discovered XFlash physical partition identifier, not a GPT index.
    /// <para>已发现的 XFlash 物理分区标识符，不是 GPT 索引。</para></param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.
    /// <para>物理分区内按块对齐的字节偏移。</para></param>
    /// <param name="length">The positive block-aligned byte count.<para>正数、按块对齐的字节数。</para></param>
    /// <param name="destination">A writable stream which remains open.<para>保持打开的可写流。</para></param>
    /// <param name="progress">Optional cumulative bytes stored and acknowledged.
    /// <para>可选的累计已写入并已确认字节数。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the final read-completion status is acknowledged.
    /// <para>在最终的读取完成状态被确认后返回的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.
    /// <para><paramref name="destination"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.
    /// <para><paramref name="destination"/> 不可写。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready or has not been discovered.
    /// <para>存储未就绪或尚未发现。</para></exception>
    /// <exception cref="MtkProtocolException">A frame violates the range, packet size or protocol; the session faults.
    /// <para>某帧违反范围、包大小或协议约定；会话进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">A frame or the whole cooperative exchange exceeded its deadline.
    /// <para>某帧或整次协作式交换超出截止时间。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task ReadXFlashStorageAsync(uint partitionId, long offset, long length, Stream destination,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The storage destination is not writable.", nameof(destination));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadXFlashStorageOwnedAsync(partitionId, offset, length, destination, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private Task ReadXFlashStorageOwnedAsync(uint partitionId, long offset, long length, Stream destination,
        IProgress<long>? progress, CancellationToken cancellationToken) =>
        RunXFlashStorageTransferAsync(token => ReadXFlashStorageExchangeAsync(
            partitionId, offset, length, destination, progress, token), cancellationToken);

    private async Task ReadXFlashStorageExchangeAsync(uint partitionId, long offset, long length, Stream destination,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XFlashStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        storage.ValidateRange(partitionId, offset, length);
        if (storage.Kind == MtkXFlashStorageKind.Nor && ((offset | length) & 7) != 0)
        {
            var alignedOffset = offset & ~7L;
            var end = checked(offset + length);
            var alignedEnd = checked(end + ((8 - (end & 7)) & 7));
            using var window = new NorReadWindowStream(destination, offset - alignedOffset, length);
            await ReadXFlashStorageExchangeAsync(partitionId, alignedOffset, alignedEnd - alignedOffset,
                window, null, cancellationToken).ConfigureAwait(false);
            progress?.Report(length);
            return;
        }
        await ReadXFlashRangeAsync(CreateXFlashStorageParameters(storage, partitionId, offset, length),
            storage.ReadPacketLength, length, destination, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadXFlashRangeAsync(byte[] parameters, int maximumPacketLength, long length, Stream destination,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
        try
        {
            await BeginXFlashTransferAsync(channel, false, parameters, cancellationToken).ConfigureAwait(false);
            long transferred = 0;
            var idleFrames = 0;
            while (transferred < length)
            {
                var frame = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                if (frame.DataType != 1)
                {
                    throw new MtkProtocolException("Storage data arrived in a non-protocol frame.");
                }
                if (frame.Payload.Length == 4)
                {
                    var status = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
                    if (status == 0)
                    {
                        if (++idleFrames > 64)
                        {
                            throw new MtkProtocolException("Storage read exceeded the consecutive no-progress status limit.");
                        }
                        continue;
                    }

                    throw new MtkProtocolException("Storage read failed with a device status frame.", status);
                }
                if (frame.Payload.Length < 5)
                {
                    throw new MtkProtocolException("Storage read ended before the requested data was received.");
                }
                if (frame.Payload.Length > maximumPacketLength || frame.Payload.Length > length - transferred)
                {
                    throw new MtkProtocolException("The storage data frame violates the requested range or negotiated packet size.");
                }
                await destination.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, 0, cancellationToken).ConfigureAwait(false);
                transferred += frame.Payload.Length;
                idleFrames = 0;
                progress?.Report(transferred);
            }
            await ReadXFlashStatusAsync(channel, "storage read completion", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
        }
        catch
        {
            FaultXFlashStorage();
            throw;
        }
    }

    /// <summary>Writes exactly an aligned byte range from a caller-owned stream. No padding, retry, rollback, vendor optional-download action or readback verification is implied.
    /// <para>从调用方拥有的流精确写入一段对齐的字节范围。不包含填充、重试、回滚、 厂商可选下载动作或回读校验等任何隐含行为。</para></summary>
    /// <remarks><see cref="XFlashStorageTransferTimeoutMs"/> includes reading the source and waiting for final confirmation.
    /// <para><see cref="XFlashStorageTransferTimeoutMs"/> 包含读取源流与等待最终确认的时间。</para></remarks>
    /// <param name="partitionId">The discovered XFlash physical partition identifier, excluding RPMB.
    /// <para>已发现的 XFlash 物理分区标识符，不含 RPMB。</para></param>
    /// <param name="offset">The block-aligned byte offset within the physical partition.
    /// <para>物理分区内按块对齐的字节偏移。</para></param>
    /// <param name="length">The positive block-aligned byte count.<para>正数、按块对齐的字节数。</para></param>
    /// <param name="source">A readable stream positioned at the first source byte; remains open.
    /// <para>定位在首个源字节处的可读流；保持打开。</para></param>
    /// <param name="progress">Optional cumulative bytes acknowledged by the agent.
    /// <para>可选的累计已被 agent 确认的字节数。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the write-completion status is acknowledged.
    /// <para>在写入完成状态被确认后返回的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.
    /// <para><paramref name="source"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive.
    /// <para><paramref name="length"/> 不是正数。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is unreadable or shorter than the requested range.
    /// <para><paramref name="source"/> 不可读或短于请求的范围。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready or has not been discovered.
    /// <para>存储未就绪或尚未发现。</para></exception>
    /// <exception cref="EndOfStreamException">The source ended before <paramref name="length"/> bytes were read.
    /// <para>源在凑足 <paramref name="length"/> 字节前已结束。</para></exception>
    /// <exception cref="MtkProtocolException">A packet or the completion was rejected; the session faults.
    /// <para>某个数据包或完成状态被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">A frame or the whole cooperative exchange exceeded its deadline.
    /// <para>某帧或整次协作式交换超出截止时间。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task WriteXFlashStorageAsync(uint partitionId, long offset, long length, Stream source,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!source.CanRead || (source.CanSeek && source.Length - source.Position < length))
        {
            throw new ArgumentException("The storage source is unreadable or shorter than the requested range.", nameof(source));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteXFlashStorageOwnedAsync(partitionId, offset, length, source, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private Task WriteXFlashStorageOwnedAsync(uint partitionId, long offset, long length, Stream source,
        IProgress<long>? progress, CancellationToken cancellationToken) =>
        RunXFlashStorageTransferAsync(token => WriteXFlashStorageExchangeAsync(
            partitionId, offset, length, source, progress, token), cancellationToken);

    private async Task WriteXFlashStorageExchangeAsync(uint partitionId, long offset, long length, Stream source,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XFlashStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        storage.ValidateRange(partitionId, offset, length);
        await WriteXFlashRangeAsync(CreateXFlashStorageParameters(storage, partitionId, offset, length),
            storage.WritePacketLength, length, source, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteXFlashRangeAsync(byte[] parameters, int packetLength, long length, Stream source,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
        try
        {
            await BeginXFlashTransferAsync(channel, true, parameters, cancellationToken).ConfigureAwait(false);
            var buffer = new byte[packetLength];
            long transferred = 0;
            while (transferred < length)
            {
                var count = (int)Math.Min(buffer.Length, length - transferred);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                uint checksum = ByteSum16.Compute(buffer.AsSpan(0, count));
                await SendXFlashWordAsync(channel, 0, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, checksum & 0xFFFF, cancellationToken).ConfigureAwait(false);
                using var payload = new MemoryStream(buffer, 0, count, writable: false);
                await channel.SendStreamAsync(1, payload, count, null, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "storage write packet", cancellationToken).ConfigureAwait(false);
                transferred += count;
                progress?.Report(transferred);
            }
            await ReadXFlashStatusAsync(channel, "storage write completion", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
        }
        catch
        {
            FaultXFlashStorage();
            throw;
        }
    }

    private async Task BeginXFlashTransferAsync(MtkXFlashChannel channel, bool write, byte[] parameters,
        CancellationToken cancellationToken)
    {
        await SendXFlashWordAsync(channel, write ? 0x010004u : 0x010005u, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "storage request", cancellationToken).ConfigureAwait(false);
        await channel.SendAsync(1, parameters, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "storage parameters", cancellationToken).ConfigureAwait(false);
        if (!write)
        {
            await ReadXFlashStatusAsync(channel, "storage read start", cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] CreateXFlashStorageParameters(MtkXFlashStorageInfo storage, uint partitionId, long offset, long length)
    {
        var parameters = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, storage.Kind switch
        {
            MtkXFlashStorageKind.Emmc => 1u,
            MtkXFlashStorageKind.Ufs => 0x30u,
            MtkXFlashStorageKind.Nor => 0x20u,
            _ => throw new ArgumentOutOfRangeException(nameof(storage))
        });
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(4), partitionId);
        BinaryPrimitives.WriteUInt64LittleEndian(parameters.AsSpan(8), (ulong)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(parameters.AsSpan(16), (ulong)length);
        return parameters;
    }

    private void FaultXFlashStorage()
    {
        _state = MtkSessionState.Faulted;
        _xflash = null;
        XFlashStorage = null;
        ClearStorageCapability(MtkStorageDiscoverySource.XFlash);
    }
}