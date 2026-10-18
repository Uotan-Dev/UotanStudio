using System.Diagnostics;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend;

internal abstract partial class UsbDevice
{
    /// <summary>
    /// Writes data to the device using the default timeout.
    /// <para>使用默认超时时间向设备写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <returns>The number of bytes actually written. <para>实际写入的字节数。</para></returns>
    public abstract long Write(byte[] data, int length);

    /// <summary>Writes data to the device with a specified timeout.
    /// <para>使用指定超时时间向设备写入数据。</para><b>Timeout semantics:</b> the timeout applies per chunk (see <see cref="MaxChunkSize"/>), not to the whole operation - a large write spanning several chunks can take up to chunkCount × timeoutMs.<para><b>超时语义：</b>超时作用于每块（见 <see cref="MaxChunkSize"/>）而非整个操作—— 跨多块的大写入最多耗时 块数 × timeoutMs。</para></summary>
    /// <param name="data">The data to write.<para>要写入的数据。</para></param>
    /// <param name="length">The number of bytes to write.<para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds.<para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes actually written.<para>实际写入的字节数。</para></returns>
    public virtual long Write(byte[] data, int length, int timeoutMs) => Write(data, 0, length, timeoutMs);

    /// <summary>Writes data to the device starting at the specified offset, with a specified timeout.
    /// <para>使用指定超时时间从指定偏移量向设备写入数据。</para><b>Timeout semantics:</b> the timeout applies per chunk (see <see cref="MaxChunkSize"/>), not to the whole operation - a large write spanning several chunks can take up to chunkCount × timeoutMs.<para><b>超时语义：</b>超时作用于每块（见 <see cref="MaxChunkSize"/>）而非整个操作—— 跨多块的大写入最多耗时 块数 × timeoutMs。</para></summary>
    /// <param name="data">The data to write.<para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start.
    /// <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write.<para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds.<para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes actually written.<para>实际写入的字节数。</para></returns>
    public virtual long Write(byte[] data, int offset, int length, int timeoutMs)
        => Write(data, offset, length, timeoutMs, progress: null);

    /// <summary>Writes data to the device starting at the specified offset, with a specified timeout, reporting cumulative bytes written after each completed chunk when <paramref name="progress"/> is set.
    /// <para>使用指定超时时间从指定偏移量向设备写入数据；设置 <paramref name="progress"/> 时 在每个分块完成后报告累计写入字节数。</para></summary>
    /// <param name="data">The data to write.<para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start.
    /// <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write.<para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds.<para>超时时间（毫秒）。</para></param>
    /// <param name="progress">Receives the cumulative transferred byte count after each chunk.
    /// <para>每块完成后接收累计传输字节数。</para></param>
    /// <returns>The number of bytes actually written.<para>实际写入的字节数。</para></returns>
    public virtual long Write(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        int? lastError = null;
        var outcome = UsbTransferOutcome.Success;
        var retryCount = 0;

        if (!IsOpen)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        ValidateWriteData(data, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);

        if (length == 0)
        {
            EmitTransfer(UsbTransferOperation.Write, 0, 0, effectiveTimeoutMs, 0, null, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.Success);
            return 0;
        }

        var lenRemaining = length;
        var count = 0;

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            while (lenRemaining > 0)
            {
                var lenToSend = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset + count);

                var chunk = WriteChunk(ptr, lenToSend, effectiveTimeoutMs);
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
                    EmitTransfer(UsbTransferOperation.Write, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.FatalError, CapturePayload(data, offset, count));
                    throw CreateWriteFatalException(chunk.NativeError);
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

                if (transferred < lenToSend)
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

        EmitTransfer(UsbTransferOperation.Write, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, outcome, CapturePayload(data, offset, count));
        return count;
    }

    /// <summary>
    /// Asynchronously writes data to the device.
    /// <para>异步向设备写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous write operation with the number of bytes written. <para>表示异步写入操作并返回已写入字节数的任务。</para></returns>
    public virtual Task<long> WriteAsync(byte[] data, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => WriteAsync(data, 0, length, timeoutMs, cancellationToken);

    /// <summary>
    /// Asynchronously writes data to the device starting at the specified offset.
    /// <para>异步从指定偏移量向设备写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous write operation with the number of bytes written. <para>表示异步写入操作并返回已写入字节数的任务。</para></returns>
    public virtual Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => WriteAsync(data, offset, length, timeoutMs, progress: null, cancellationToken);

    /// <summary>
    /// Asynchronously writes data to the device starting at the specified offset, reporting
    /// cumulative bytes written after each completed chunk.
    /// <para>异步从指定偏移量向设备写入数据，并在每个分块完成后报告累计写入字节数。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="progress">Receives the cumulative transferred byte count after each chunk. <para>每块完成后接收累计传输字节数。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous write operation with the number of bytes written. <para>表示异步写入操作并返回已写入字节数的任务。</para></returns>
    public virtual async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int? lastError = null;
        var outcome = UsbTransferOutcome.Success;
        var retryCount = 0;

        if (!IsOpen)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        ValidateWriteData(data, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);

        if (length == 0)
        {
            EmitTransfer(UsbTransferOperation.Write, 0, 0, effectiveTimeoutMs, 0, null, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.Success);
            return 0;
        }

        var lenRemaining = length;
        var count = 0;

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            while (lenRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lenToSend = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset + count);

                var chunk = await WriteChunkAsync(ptr, lenToSend, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
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
                    EmitTransfer(UsbTransferOperation.Write, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.FatalError, CapturePayload(data, offset, count));
                    throw CreateWriteFatalException(chunk.NativeError);
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

                if (transferred < lenToSend)
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

        EmitTransfer(UsbTransferOperation.Write, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, outcome, CapturePayload(data, offset, count));
        return count;
    }
}