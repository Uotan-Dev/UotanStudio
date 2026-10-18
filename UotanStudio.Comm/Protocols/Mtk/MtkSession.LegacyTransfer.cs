using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Hash.Sums;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Reports the serial speed negotiated through the Legacy speed command together with the
/// USB speed the agent confirms afterwards.
/// <para>报告经 Legacy 速度命令协商的串行速度，以及随后代理确认的 USB 速度。</para></summary>
/// <param name="Baudrate">The baudrate that was negotiated with the agent.
/// <para>与代理协商的波特率。</para></param>
/// <param name="UsbSpeed">The speed code reported by the existing USB status query afterwards:
/// 0 full, 1 high, 2 ultra-high. The reference reads that single byte after the status command
/// (Library/DA/legacy/dalegacy_lib.py:807-813, whose selector byte 0x72 is
/// Library/DA/legacy/dalegacy_param.py:27) and names the codes in its own reconnect test
/// (Library/DA/legacy/dalegacy_lib.py:625).
/// <para>随后由既有 USB 状态查询上报的速度码：0 全速、1 高速、2 超高速。参考在发送状态命令后读取该
/// 单字节（Library/DA/legacy/dalegacy_lib.py:807-813，其选择符 0x72 见
/// Library/DA/legacy/dalegacy_param.py:27），并在自身的重连判断中注明各取值的含义
/// （Library/DA/legacy/dalegacy_lib.py:625）。</para></param>
public sealed record MtkLegacyTransferSpeedResult(int Baudrate, byte UsbSpeed);

public sealed partial class MtkSession
{
    /// <summary>Reads a Legacy eMMC range to a caller-owned stream after validating each packet checksum.
    /// <para>校验每个数据包的校验和后，将 Legacy eMMC 范围读取到调用方持有的流。</para></summary>
    /// <remarks>The stream may contain partial output on failure; file publishers must use temporary files. RPMB is not ordinary block storage. No retry or protocol fallback occurs.
    /// <para>失败时流可能包含部分输出；发布文件必须使用临时文件。RPMB 不是普通块存储。不重试，也不回退协议。</para></remarks>
    /// <param name="partitionId">The discovered physical region from 1 through 8, excluding RPMB.
    /// <para>已发现的 1 至 8 物理区域，不含 RPMB。</para></param>
    /// <param name="offset">The byte offset aligned to 512.<para>按 512 对齐的字节偏移。</para></param>
    /// <param name="length">The positive byte count aligned to 512.<para>按 512 对齐的正数字节数。</para></param>
    /// <param name="destination">The writable stream, which remains open.<para>保持打开的可写流。</para></param>
    /// <param name="timeoutMs">The cooperative whole-transfer deadline, from 1 ms through 24 hours.
    /// <para>完整传输的协作式截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="progress">Cumulative bytes stored and acknowledged.
    /// <para>累计已保存并确认的字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation.<para>操作取消令牌。</para></param>
    /// <returns>A task completing after the last packet has been verified and acknowledged.
    /// <para>最后一个数据包经校验并确认后完成的任务。</para></returns>
    public Task ReadLegacyStorageAsync(byte partitionId, long offset, long length, Stream destination,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        TransferLegacyStorageAsync(false, partitionId, offset, length, destination, timeoutMs, progress, cancellationToken);

    /// <summary>Writes exactly a Legacy eMMC range without padding, retry or implied readback verification.
    /// <para>精确写入 Legacy eMMC 范围，不填充、不重试，也不隐含回读校验。</para></summary>
    /// <param name="partitionId">The discovered physical region from 1 through 8, excluding RPMB.
    /// <para>已发现的 1 至 8 物理区域，不含 RPMB。</para></param>
    /// <param name="offset">The byte offset aligned to 512.<para>按 512 对齐的字节偏移。</para></param>
    /// <param name="length">The positive byte count aligned to 512.<para>按 512 对齐的正数字节数。</para></param>
    /// <param name="source">The readable stream positioned at the first selected byte; it remains open.
    /// <para>定位到首个选定字节的可读流；保持打开。</para></param>
    /// <param name="timeoutMs">The cooperative whole-transfer deadline, from 1 ms through 24 hours.
    /// <para>完整传输的协作式截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="progress">Cumulative bytes acknowledged by the agent.
    /// <para>累计已被代理确认的字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation.<para>操作取消令牌。</para></param>
    /// <returns>A task completing after the final packet receives its CONT acknowledgement.
    /// <para>最后一个数据包收到 CONT 确认后完成的任务。</para></returns>
    public Task WriteLegacyStorageAsync(byte partitionId, long offset, long length, Stream source,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        TransferLegacyStorageAsync(true, partitionId, offset, length, source, timeoutMs, progress, cancellationToken);

    private async Task TransferLegacyStorageAsync(bool write, byte partitionId, long offset, long length, Stream stream,
        int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken, bool nor = false)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TransferLegacyStorageOwnedAsync(write, partitionId, offset, length, stream, timeoutMs, progress,
                cancellationToken, nor).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task TransferLegacyStorageOwnedAsync(bool write, byte partitionId, long offset, long length,
        Stream stream, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken, bool nor)
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

    /// <summary>Negotiates a new serial baudrate through the Legacy speed command and reports the
    /// speed the agent confirms afterwards.
    /// <para>通过 Legacy 速度命令协商新的串行波特率，并报告随后代理确认的速度。</para></summary>
    /// <remarks>
    /// The exchange mirrors <c>set_speed</c> of Library/DA/legacy/dalegacy_lib.py:446-469: the
    /// SPEED_CMD selector (0xD2, Library/DA/legacy/dalegacy_param.py:82), the four-byte big-endian
    /// baudrate, a 0x5A acknowledgement, the 0xC0 resynchronisation loop the reference repeats ten
    /// times at the new rate, a 0x5A marker pair and the 256-value byte loopback that proves the
    /// link survived the switch. The reference hard-codes 921600; the baudrate is a parameter here
    /// with the same default. After the loopback the existing USB status query (0x72, unchanged by
    /// this call) reports the USB speed the agent actually applies, so the caller sees the effective
    /// link state instead of only the request. Any failure faults the Legacy family because the
    /// channel may be running at a rate the host no longer matches.
    /// <para>本交换与 Library/DA/legacy/dalegacy_lib.py:446-469 的 <c>set_speed</c> 一致：SPEED_CMD
    /// 选择器（0xD2，Library/DA/legacy/dalegacy_param.py:82）、四字节大端波特率、0x5A 确认、参考在新速率下
    /// 重复十次的 0xC0 重同步循环、一对 0x5A 标记，以及证明链路在切换后仍存活的 256 值字节回环。参考把
    /// 921600 硬编码；这里把它作为参数并沿用同一默认值。回环之后，既有的 USB 状态查询（0x72，本次调用不
    /// 修改）上报代理实际应用的 USB 速度，因此调用方看到的是生效的链路状态而不仅是请求。任何失败都会使
    /// Legacy 家族进入 fault，因为链路可能运行在主机已不再匹配的速率上。</para>
    /// </remarks>
    /// <param name="baudrate">The positive serial baudrate to request; the reference default is
    /// 921600. <para>要请求的正数串行波特率；参考默认值为 921600。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The negotiated baudrate and the USB speed code the agent reports afterwards.
    /// <para>协商的波特率与随后代理上报的 USB 速度码。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baudrate"/> is not positive;
    /// no byte was sent. <para><paramref name="baudrate"/> 不是正数；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready.
    /// <para>Legacy 存储未就绪。</para></exception>
    /// <exception cref="MtkProtocolException">The acknowledgement, resynchronisation or loopback
    /// failed; the Legacy family faults. <para>确认、重同步或回环失败；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkLegacyTransferSpeedResult> SetLegacyTransferSpeedAsync(int baudrate = 921600,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baudrate);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await SetLegacySpeedOwnedAsync(baudrate, cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("legacy-speed", CommandOutcomeKind.Completed,
                    $"Legacy SPEED_CMD negotiated {result.Baudrate} baud; the agent reports USB speed " +
                    $"{result.UsbSpeed}.");
                return result;
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

    private async Task<MtkLegacyTransferSpeedResult> SetLegacySpeedOwnedAsync(int baudrate,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)baudrate);
        await WriteAsync([0xD2], _timeoutMs, cancellationToken).ConfigureAwait(false);
        await WriteAsync(header, _timeoutMs, cancellationToken).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "speed change", cancellationToken).ConfigureAwait(false);
        var synced = false;
        for (var attempt = 0; attempt < 10 && !synced; attempt++)
        {
            await WriteAsync([0xC0], _timeoutMs, cancellationToken).ConfigureAwait(false);
            synced = (await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false))[0] == 0xC0;
        }
        if (!synced)
        {
            throw new MtkProtocolException(
                "The Legacy agent did not resynchronize at the new speed within ten attempts.");
        }
        await WriteAsync([0x5A], _timeoutMs, cancellationToken).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "speed loopback marker", cancellationToken).ConfigureAwait(false);
        for (var value = 0; value < 256; value++)
        {
            await WriteAsync([(byte)value], _timeoutMs, cancellationToken).ConfigureAwait(false);
            var echoed = (await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false))[0];
            if (echoed != (byte)value)
            {
                throw new MtkProtocolException(
                    $"The Legacy speed loopback failed at byte 0x{value:X2}.", echoed);
            }
        }
        var usbSpeed = await QueryLegacyUsbSpeedOwnedAsync(cancellationToken).ConfigureAwait(false);
        return new MtkLegacyTransferSpeedResult(baudrate, usbSpeed);
    }

    private static ushort LegacySum(ReadOnlySpan<byte> bytes) => ByteSum16.Compute(bytes);
}