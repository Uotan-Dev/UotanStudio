using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads a Legacy eMMC range to a caller-owned stream after validating each packet checksum.
    /// <para>校验每个数据包的校验和后，将 Legacy eMMC 范围读取到调用方持有的流。</para></summary>
    /// <remarks>The stream may contain partial output on failure; file publishers must use temporary files.
    /// RPMB is not ordinary block storage. No retry or protocol fallback occurs.
    /// <para>失败时流可能包含部分输出；发布文件必须使用临时文件。RPMB 不是普通块存储。不重试，也不回退协议。</para></remarks>
    /// <param name="partitionId">The discovered physical region from 1 through 8, excluding RPMB.
    /// <para>已发现的 1 至 8 物理区域，不含 RPMB。</para></param>
    /// <param name="offset">The byte offset aligned to 512.
    /// <para>按 512 对齐的字节偏移。</para></param>
    /// <param name="length">The positive byte count aligned to 512.
    /// <para>按 512 对齐的正数字节数。</para></param>
    /// <param name="destination">The writable stream, which remains open.
    /// <para>保持打开的可写流。</para></param>
    /// <param name="timeoutMs">The cooperative whole-transfer deadline, from 1 ms through 24 hours.
    /// <para>完整传输的协作式截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="progress">Cumulative bytes stored and acknowledged.
    /// <para>累计已保存并确认的字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task completing after the last packet has been verified and acknowledged.
    /// <para>最后一个数据包经校验并确认后完成的任务。</para></returns>
    public Task ReadLegacyStorageAsync(byte partitionId, long offset, long length, Stream destination,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        TransferLegacyStorageAsync(false, partitionId, offset, length, destination, timeoutMs, progress, cancellationToken);

    /// <summary>Writes exactly a Legacy eMMC range without padding, retry or implied readback verification.
    /// <para>精确写入 Legacy eMMC 范围，不填充、不重试，也不隐含回读校验。</para></summary>
    /// <param name="partitionId">The discovered physical region from 1 through 8, excluding RPMB.
    /// <para>已发现的 1 至 8 物理区域，不含 RPMB。</para></param>
    /// <param name="offset">The byte offset aligned to 512.
    /// <para>按 512 对齐的字节偏移。</para></param>
    /// <param name="length">The positive byte count aligned to 512.
    /// <para>按 512 对齐的正数字节数。</para></param>
    /// <param name="source">The readable stream positioned at the first selected byte; it remains open.
    /// <para>定位到首个选定字节的可读流；保持打开。</para></param>
    /// <param name="timeoutMs">The cooperative whole-transfer deadline, from 1 ms through 24 hours.
    /// <para>完整传输的协作式截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="progress">Cumulative bytes acknowledged by the agent.
    /// <para>累计已被代理确认的字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task completing after the final packet receives its CONT acknowledgement.
    /// <para>最后一个数据包收到 CONT 确认后完成的任务。</para></returns>
    public Task WriteLegacyStorageAsync(byte partitionId, long offset, long length, Stream source,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        TransferLegacyStorageAsync(true, partitionId, offset, length, source, timeoutMs, progress, cancellationToken);

    private async Task TransferLegacyStorageAsync(bool write, byte partitionId, long offset, long length, Stream stream,
        int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken, bool nor = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (write ? !stream.CanRead : !stream.CanWrite)
        {
            throw new ArgumentException("The stream does not support the selected transfer direction.", nameof(stream));
        }
        ValidateXmlSetupTimeout(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (write && stream.CanSeek && (stream.Position > stream.Length || length > stream.Length - stream.Position))
        {
            throw new EndOfStreamException("The selected storage source is shorter than the requested transfer.");
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            if (nor)
            {
                var geometry = LegacyStorage!.Nor ?? throw new NotSupportedException("The Legacy session is not initialized for NOR.");
                geometry.ValidateRange(offset, length, write);
            }
            else
            {
                LegacyStorage!.ValidateRange(partitionId, offset, length);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var options = _legacyOptions!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                if (!write)
                {
                    _ = await QueryLegacyUsbSpeedOwnedAsync(token).ConfigureAwait(false);
                    if (!nor)
                    {
                        await SwitchLegacyPartitionOwnedAsync(partitionId, token).ConfigureAwait(false);
                    }
                }
                var header = new byte[23];
                header[0] = write ? (byte)0x62 : (byte)0xD6;
                header[1] = options.HostOs == MtkXmlHostOs.Windows ? (byte)0x0B : (byte)0x0C;
                header[2] = nor ? (byte)0 : (byte)2;
                if (write)
                {
                    header[1] = nor ? (byte)0x20 : (byte)1;
                    header[2] = partitionId;
                }
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(3), (ulong)offset);
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(11), (ulong)length);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(19), (uint)options.StoragePacketLength);
                await WriteAsync(header, _timeoutMs, token).ConfigureAwait(false);
                await RequireLegacyByteAsync(0x5A, "storage header", token).ConfigureAwait(false);
                var buffer = write ? new byte[(int)Math.Min(length, options.StoragePacketLength)] : null;
                long transferred = 0;
                while (transferred < length)
                {
                    var count = (int)Math.Min(length - transferred, options.StoragePacketLength);
                    if (write)
                    {
                        await stream.ReadExactlyAsync(buffer!.AsMemory(0, count), token).ConfigureAwait(false);
                        await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
                        await WriteAsync(buffer!, count, _timeoutMs, token).ConfigureAwait(false);
                        var checksum = new byte[2];
                        BinaryPrimitives.WriteUInt16BigEndian(checksum, LegacySum(buffer.AsSpan(0, count)));
                        await WriteAsync(checksum, _timeoutMs, token).ConfigureAwait(false);
                        await RequireLegacyByteAsync(0x69, "storage packet", token).ConfigureAwait(false);
                    }
                    else
                    {
                        var data = await ReadAsync(count, _timeoutMs, token).ConfigureAwait(false);
                        var checksum = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(2, _timeoutMs, token).ConfigureAwait(false));
                        if (checksum != LegacySum(data))
                        {
                            throw new MtkProtocolException("The Legacy storage packet checksum does not match.");
                        }
                        await stream.WriteAsync(data, token).ConfigureAwait(false);
                        await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    transferred += count;
                    progress?.Report(transferred);
                }
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultLegacy();
                throw new TimeoutException("The Legacy storage transfer deadline expired; the session cannot be replayed.", error);
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

    private async Task SwitchLegacyPartitionOwnedAsync(byte partitionId, CancellationToken token)
    {
        await WriteAsync([0x60], _timeoutMs, token).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "partition-switch request", token).ConfigureAwait(false);
        await WriteAsync([partitionId], _timeoutMs, token).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "partition-switch completion", token).ConfigureAwait(false);
    }

    private async Task<byte> QueryLegacyUsbSpeedOwnedAsync(CancellationToken token)
    {
        await WriteAsync([0x72], _timeoutMs, token).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "USB status", token).ConfigureAwait(false);
        var speed = (await ReadAsync(1, _timeoutMs, token).ConfigureAwait(false))[0];
        if (speed > 2)
        {
            throw new MtkProtocolException("The Legacy agent reported an unknown USB speed.");
        }
        return speed;
    }

    private static ushort LegacySum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        foreach (var value in bytes)
        {
            sum += value;
        }
        return (ushort)(sum & 0xFFFF);
    }
}