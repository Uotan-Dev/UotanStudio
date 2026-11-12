using System.Diagnostics;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend;

internal abstract partial class UsbDevice
{
    /// <summary>
    /// Reads data from the device using the default timeout.
    /// <para>使用默认超时时间从设备读取数据。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <returns>The received data. <para>接收到的数据。</para></returns>
    public abstract byte[] Read(int length);

    /// <summary>
    /// Reads data from the device with a specified timeout.
    /// <para>使用指定超时时间从设备读取数据。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The received data. <para>接收到的数据。</para></returns>
    public virtual byte[] Read(int length, int timeoutMs) => Read(length);

    /// <summary>Reads data from the device directly into the specified buffer with a specified timeout.
    /// <para>使用指定超时时间将数据直接读入指定的缓冲区。</para><b>Timeout semantics:</b> the timeout applies per chunk (see <see cref="MaxChunkSize"/>), not to the whole operation - a large read spanning several chunks can take up to chunkCount × timeoutMs. Use <c>ReadExact</c> when a total-budget read is needed.<para><b>超时语义：</b>超时作用于每块（见 <see cref="MaxChunkSize"/>）而非整个操作—— 跨多块的大读取最多耗时 块数 × timeoutMs。需要整体预算读取时使用 <c>ReadExact</c>。</para></summary>
    /// <param name="buffer">The target buffer.<para>目标缓冲区。</para></param>
    /// <param name="offset">The byte offset within the buffer.<para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes to read.<para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds.<para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes actually read.<para>实际读取的字节数。</para></returns>
    public virtual int ReadInto(byte[] buffer, int offset, int length, int timeoutMs) => ReadPacket(buffer, offset, length, timeoutMs).Count;

#if NET8_0_OR_GREATER
    /// <summary>
    /// Reads bytes into a caller-provided span with a timeout (net8.0+ only).
    /// <para>在指定超时时间内将数据读取到调用方提供的跨度（仅 net8.0+）。</para>
    /// The span is bridged through a temporary array; use the byte[] overloads for
    /// zero-copy transfers on hot paths.
    /// <para>跨度通过临时数组桥接；热路径的零拷贝传输请使用 byte[] 重载。</para>
    /// </summary>
    /// <param name="buffer">The destination span. <para>目标跨度。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes read. <para>实际读取的字节数。</para></returns>
    public virtual int ReadInto(Span<byte> buffer, int timeoutMs)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var temp = new byte[buffer.Length];
        var count = ReadInto(temp, 0, temp.Length, timeoutMs);
        if (count > 0)
        {
            temp.AsSpan(0, count).CopyTo(buffer);
        }

        return count;
    }
#endif

    /// <summary>
    /// Upper bound on consecutive empty completions one read absorbs before it reports the empty answer. A target that
    /// keeps completing URBs with no payload must still surface as a timeout instead of spinning inside the backend.
    /// <para>单次读取在报告空应答之前吸收的连续空完成上限。持续以无负载完成 URB 的目标仍须上报超时，而不是在后端内空转。</para>
    /// </summary>
    private const int MaximumEmptyCompletions = 64;

    /// <summary>
    /// Pause between two reads that were re-issued after an empty completion, so a backend answering them instantly
    /// cannot spend the whole retry budget before the target produces the payload.
    /// <para>两次因空完成而重新发起的读取之间的停顿，使瞬间返回空完成的后端无法在目标产出负载之前耗光整段重试预算。</para>
    /// </summary>
    private const int EmptyCompletionPauseMs = 20;

    /// <summary>Reads bytes into the specified buffer and reports the transfer outcome.
    /// <para>将数据读取到指定缓冲区并报告传输结果。</para><see cref="UsbReadResult.IsTimeout"/> is set when no data arrived before the deadline; <see cref="UsbReadResult.IsShortPacket"/> is set when the device ended the transfer with fewer bytes than requested (the USB Message boundary). Protocol layers use these flags to distinguish "waiting for more data" from "Message complete".<para><see cref="UsbReadResult.IsTimeout"/> 表示在期限内没有数据到达； <see cref="UsbReadResult.IsShortPacket"/> 表示设备以少于请求的字节数结束传输 （USB 消息边界）。协议层用这些标志区分"等待更多数据"与"消息已完成"。</para></summary>
    /// <param name="buffer">The destination buffer.<para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset.<para>目标偏移量。</para></param>
    /// <param name="length">The number of bytes to read.<para>读取字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds.<para>超时时间（毫秒）。</para></param>
    /// <returns>The read result with byte count and outcome flags.
    /// <para>包含字节数与结果标志的读取结果。</para></returns>
    public virtual UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs)
        => ReadPacket(buffer, offset, length, timeoutMs, progress: null);

    /// <summary>
    /// Reads bytes into the specified buffer and reports the transfer outcome, reporting
    /// cumulative bytes read after each completed chunk when <paramref name="progress"/> is set.
    /// <para>将数据读取到指定缓冲区并报告传输结果；设置 <paramref name="progress"/> 时
    /// 在每个分块完成后报告累计读取字节数。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>读取字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="progress">Receives the cumulative transferred byte count after each chunk. <para>每块完成后接收累计传输字节数。</para></param>
    /// <returns>The read result with byte count and outcome flags. <para>包含字节数与结果标志的读取结果。</para></returns>
    public virtual UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs, IProgress<long>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        int? lastError = null;
        var outcome = UsbTransferOutcome.Success;

        if (!IsOpen)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        if (length <= 0)
        {
            return new UsbReadResult(0, false, false);
        }

        ValidateBufferRange(buffer, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        var lenRemaining = length;
        var count = 0;
        var retryCount = 0;

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            while (lenRemaining > 0)
            {
                var lenToRead = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset + count);

                var chunk = ReadChunk(ptr, lenToRead, effectiveTimeoutMs);
                retryCount += chunk.RetryCount;

                if (chunk.Status == UsbChunkStatus.Timeout)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Timeout;
                    break;
                }

                if (chunk.Status == UsbChunkStatus.FatalError)
                {
                    lastError = chunk.NativeError;
                    EmitTransfer(UsbTransferOperation.Read, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.FatalError, CapturePayload(buffer, offset, count));
                    throw CreateReadFatalException(chunk.NativeError);
                }

                if (chunk.Status == UsbChunkStatus.Error)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Error;
                    break;
                }

                var transferred = chunk.Transferred;
                count += transferred;
                lenRemaining -= transferred;
                progress?.Report(count);

                if (transferred < lenToRead)
                {
                    break;
                }
            }
        }
        finally
        {
            handle.Free();
        }

        if (outcome == UsbTransferOutcome.Success && count > 0 && count < length)
        {
            outcome = UsbTransferOutcome.ShortTransfer;
        }

        EmitTransfer(UsbTransferOperation.Read, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, outcome, CapturePayload(buffer, offset, count));
        return new UsbReadResult(
            count,
            isTimeout: outcome == UsbTransferOutcome.Timeout,
            isShortPacket: outcome == UsbTransferOutcome.ShortTransfer);
    }

    /// <summary>
    /// Asynchronously reads data from the device.
    /// <para>异步从设备读取数据。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous read operation. <para>表示异步读取操作的任务。</para></returns>
    public virtual async Task<byte[]> ReadAsync(int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length > UsbTransferPolicies.MaxReadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"length exceeds the safety cap of {UsbTransferPolicies.MaxReadLength} bytes; clamp device-provided frame lengths.");
        }

        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        var count = await ReadIntoAsync(buffer, 0, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (count == length)
        {
            return buffer;
        }

        if (count == 0)
        {
            return [];
        }

        var realData = new byte[count];
        Buffer.BlockCopy(buffer, 0, realData, 0, count);
        return realData;
    }

    /// <summary>
    /// Asynchronously reads data directly into the specified buffer.
    /// <para>异步将数据直接读入指定的缓冲区。</para>
    /// </summary>
    /// <param name="buffer">The target buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The byte offset within the buffer. <para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous read operation with the number of bytes read. <para>表示异步读取操作并返回已读取字节数的任务。</para></returns>
    public virtual async Task<int> ReadIntoAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var result = await ReadPacketAsync(buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        return result.Count;
    }

    /// <summary>
    /// Asynchronously reads bytes into the specified buffer and reports the transfer outcome.
    /// <para>异步将数据读取到指定缓冲区并报告传输结果。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>读取字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that resolves to the read result. <para>返回读取结果的任务。</para></returns>
    public virtual Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => ReadPacketAsync(buffer, offset, length, timeoutMs, progress: null, cancellationToken);

    /// <summary>
    /// Asynchronously reads bytes into the specified buffer and reports the transfer outcome,
    /// reporting cumulative bytes read after each completed chunk.
    /// <para>异步将数据读取到指定缓冲区并报告传输结果，在每个分块完成后报告累计读取字节数。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>读取字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="progress">Receives the cumulative transferred byte count after each chunk. <para>每块完成后接收累计传输字节数。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that resolves to the read result. <para>返回读取结果的任务。</para></returns>
    public virtual async Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int? lastError = null;
        var outcome = UsbTransferOutcome.Success;
        var retryCount = 0;

        if (!IsOpen)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        if (length <= 0)
        {
            return new UsbReadResult(0, false, false);
        }

        ValidateBufferRange(buffer, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        var lenRemaining = length;
        var count = 0;
        var emptyCompletions = 0;
        var readDeadline = effectiveTimeoutMs == UsbTransferPolicies.InfiniteTimeoutMs
            ? long.MaxValue
            : Environment.TickCount64 + effectiveTimeoutMs;

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            while (lenRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lenToRead = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset + count);

                var chunk = await ReadChunkAsync(ptr, lenToRead, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
                retryCount += chunk.RetryCount;

                // A successful completion that carried no bytes is not the end of the transfer: several kernels and
                // host controllers complete one URB of a multi-packet message empty before the payload arrives (usbfs
                // on xhci, and libusb on the same stack, both do it), and the caller asked for a whole read. Reporting
                // that as a zero-byte answer makes a protocol loop give up one packet into a healthy exchange, so
                // re-issue the read while the caller's deadline still has room instead.
                // The pause matters: an empty completion can arrive in well under a millisecond, so spinning on it
                // would burn the whole budget of consecutive retries before the target ever had a chance to answer.
                // 停顿是关键：空完成可能远在一毫秒内到达，空转会在目标有机会应答之前就耗光连续重试的预算。
                if (chunk.Status == UsbChunkStatus.Success && chunk.Transferred == 0 && count == 0)
                {
                    var remainingBudget = readDeadline - Environment.TickCount64;
                    if (remainingBudget > 0 && emptyCompletions < MaximumEmptyCompletions)
                    {
                        emptyCompletions++;
                        EmitTransfer(UsbTransferOperation.Read, length, 0, effectiveTimeoutMs, retryCount,
                            chunk.NativeError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.Success,
                            CapturePayload(buffer, offset, count));
                        var pause = (int)Math.Min(EmptyCompletionPauseMs, remainingBudget);
                        if (pause > 0)
                        {
                            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }

                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Timeout;
                    break;
                }

                if (chunk.Status == UsbChunkStatus.Timeout)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Timeout;
                    break;
                }

                if (chunk.Status == UsbChunkStatus.FatalError)
                {
                    lastError = chunk.NativeError;
                    EmitTransfer(UsbTransferOperation.Read, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.FatalError, CapturePayload(buffer, offset, count));
                    throw CreateReadFatalException(chunk.NativeError);
                }

                if (chunk.Status == UsbChunkStatus.Error)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Error;
                    break;
                }

                var transferred = chunk.Transferred;
                count += transferred;
                lenRemaining -= transferred;
                progress?.Report(count);

                if (transferred < lenToRead)
                {
                    break;
                }
            }
        }
        finally
        {
            handle.Free();
        }

        if (outcome == UsbTransferOutcome.Success && count > 0 && count < length)
        {
            outcome = UsbTransferOutcome.ShortTransfer;
        }

        EmitTransfer(UsbTransferOperation.Read, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, outcome, CapturePayload(buffer, offset, count));
        return new UsbReadResult(
            count,
            isTimeout: outcome == UsbTransferOutcome.Timeout,
            isShortPacket: outcome == UsbTransferOutcome.ShortTransfer);
    }
}