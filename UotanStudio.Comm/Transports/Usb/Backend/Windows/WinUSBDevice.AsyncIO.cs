using System.ComponentModel;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.WinUSBAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal partial class WinUSBDevice
{
    /// <summary>Asynchronously reads one bulk IN chunk through overlapped I/O.
    /// <para>通过重叠 I/O 异步读取一个批量 IN 分块。</para></summary>
    /// <remarks>A disconnection or an aborted overlapped transfer is fatal: neither can be resumed on the same pipe, and the base read loop would otherwise collapse the error into a zero-byte idle read that a protocol loop keeps waiting on. Any other native error still collapses to <c>UsbChunkResult.Error</c>, which the base read loop treats as a short transfer with the error code only reaching the transfer trace; the synchronous <c>ReadChunk</c> raises a fatal error (<c>UsbChunkStatus.FatalError</c>) for it, and that difference stays until per-protocol retry behavior has real-device regression. The native error is logged here so the collapse is never silent at the backend layer.
    /// <para>断连与重叠传输被中止都是致命错误：两者都无法在同一管道上继续，而基类读取循环会把它折叠成零字节的
    /// 空闲读取，使协议循环继续等待。其它原生错误仍折叠为 <c>UsbChunkResult.Error</c>，基类读取循环将其视为短
    /// 传输且错误码只进入传输 trace；同样的错误在同步 <c>ReadChunk</c> 会报致命错误（<c>UsbChunkStatus.FatalError</c>）
    /// 并抛出，该差异保留到各协议重试策略经过真机回归为止。此处记录原生错误码，使后端层的折叠不再静默。</para></remarks>
    protected override async Task<UsbChunkResult> ReadChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        try
        {
            var clampedLength = ClampRawIoReadLength(length);
            var transferred = await OverlappedTransferAsync(buffer, clampedLength, _readBulkID, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (transferred > 0)
            {
                Interlocked.Exchange(ref _consecutiveAbortedTransfers, 0);
                return UsbChunkResult.Success((int)transferred);
            }

            return RawIoEnabled
                ? UsbChunkResult.Success(0)
                : UsbChunkResult.Timeout(ERROR_SEM_TIMEOUT);
        }
        catch (Win32Exception ex)
        {
            if (IsDisconnectionError(ex.NativeErrorCode))
            {
                return UsbChunkResult.Fatal(ex.NativeErrorCode);
            }

            if (IsAbortedTransferError(ex.NativeErrorCode))
            {
                return RecoverAbortedTransfer(ex.NativeErrorCode, _readBulkID);
            }

            UsbTrace.LogError($"WinUSB: async bulk IN chunk failed with native error {ex.NativeErrorCode}; collapsed to a retryable error outcome.");
            return UsbChunkResult.Error(ex.NativeErrorCode);
        }
    }

    /// <summary>Asynchronously writes one bulk OUT chunk through overlapped I/O.
    /// <para>通过重叠 I/O 异步写入一个批量 OUT 分块。</para></summary>
    /// <remarks>A disconnection or an aborted overlapped transfer is fatal because neither can be resumed on the same pipe. Any other native error collapses to <c>UsbChunkResult.Error</c>, which the base write loop treats as a short write indistinguishable from a device-side short completion, while the synchronous <c>WriteChunk</c> raises a fatal failure for it; that difference stays pending per-protocol retry review and real-device regression. The native error is logged here so the collapse is never silent at the backend layer.
    /// <para>断连与重叠传输被中止都是致命错误，因为两者都无法在同一管道上继续。其它原生错误折叠为
    /// <c>UsbChunkResult.Error</c>，基类写入循环将其视为与设备侧短完成不可区分的短写，而同样的错误在同步
    /// <c>WriteChunk</c> 会报致命失败；该差异待逐协议重试策略复核与真机回归后再定。此处记录原生错误码，
    /// 使后端层的折叠不再静默。</para></remarks>
    protected override async Task<UsbChunkResult> WriteChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        try
        {
            var transferred = await OverlappedTransferAsync(buffer, length, _writeBulkID, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            return transferred > 0
                ? UsbChunkResult.Success((int)transferred)
                : UsbChunkResult.Timeout(ERROR_SEM_TIMEOUT);
        }
        catch (Win32Exception ex)
        {
            if (IsDisconnectionError(ex.NativeErrorCode))
            {
                return UsbChunkResult.Fatal(ex.NativeErrorCode);
            }

            if (IsAbortedTransferError(ex.NativeErrorCode))
            {
                return RecoverAbortedTransfer(ex.NativeErrorCode, _writeBulkID);
            }

            UsbTrace.LogError($"WinUSB: async bulk OUT chunk failed with native error {ex.NativeErrorCode}; collapsed to a retryable error outcome.");
            return UsbChunkResult.Error(ex.NativeErrorCode);
        }
    }

    public override async Task WriteZlpAsync(int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        await OverlappedTransferAsync(IntPtr.Zero, 0, _writeBulkID, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        if (length <= 0)
        {
            return new UsbReadResult(0, false, false);
        }

        ValidateBufferRange(buffer, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
            var transferred = await OverlappedTransferAsync(ptr, length, endpointAddress, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            return transferred == 0
                ? new UsbReadResult(0, isTimeout: true, isShortPacket: false)
                : new UsbReadResult((int)transferred, isTimeout: false, isShortPacket: transferred < length);
        }
        finally
        {
            handle.Free();
        }
    }

    public override async Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        ValidateWriteData(data, offset, length);
        if (length == 0)
        {
            return 0;
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
            var transferred = await OverlappedTransferAsync(ptr, length, endpointAddress, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            return transferred;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Number of aborted bulk transfers seen in a row since the last completed one.
    /// <para>自上次完成传输以来连续出现的中止批量传输次数。</para></summary>
    private int _consecutiveAbortedTransfers;

    /// <summary>Maximum consecutive aborts before the transfer is reported as fatal instead of retryable.
    /// <para>连续中止次数上限；超过后该传输按致命错误上报。</para></summary>
    private const int MaximumConsecutiveAborts = 3;

    /// <summary>
    /// Recovers one aborted bulk transfer: resets the affected pipe and reports a retryable timeout, so the protocol
    /// loop re-issues the transfer instead of faulting a session that is still healthy. A closed handle or a burst of
    /// consecutive aborts is fatal, because retrying those would spin without ever completing.
    /// <para>恢复一次被中止的批量传输：复位受影响的管道并上报可重试的超时，使协议循环重新发起该传输，而不是让仍然
    /// 健康的会话失败。句柄已关闭或连续多次中止属于致命错误，因为重试只会空转而永不完成。</para>
    /// </summary>
    /// <param name="nativeError">Native abort error code. <para>中止的原生错误码。</para></param>
    /// <param name="pipeId">Bulk pipe that was aborted. <para>被中止的批量管道。</para></param>
    /// <returns>A retryable timeout, or a fatal result for a closed handle or a repeated abort.
    /// <para>可重试的超时；句柄已关闭或反复中止时返回致命结果。</para></returns>
    private UsbChunkResult RecoverAbortedTransfer(int nativeError, byte pipeId)
    {
        if (_winUsbHandle.IsInvalid || _fileHandle.IsInvalid)
        {
            return UsbChunkResult.Fatal(nativeError);
        }

        var aborts = Interlocked.Increment(ref _consecutiveAbortedTransfers);
        if (aborts > MaximumConsecutiveAborts)
        {
            UsbTrace.LogError(
                $"WinUSB: bulk pipe 0x{pipeId:X2} was aborted {aborts} times in a row; reporting a fatal transfer so the session is rebuilt instead of retrying forever.");
            return UsbChunkResult.Fatal(nativeError);
        }

        UsbTrace.Log(
            $"WinUSB: bulk pipe 0x{pipeId:X2} was aborted (native error {nativeError}); resetting the pipe and reporting a retryable timeout ({aborts}/{MaximumConsecutiveAborts}).");
        try
        {
            WinUsb_ResetPipe(_winUsbHandle.DangerousGetHandle(), pipeId);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Win32Exception)
        {
            return UsbChunkResult.Fatal(nativeError);
        }

        return UsbChunkResult.Timeout(nativeError);
    }

    /// <summary>Whether a native bulk error means the overlapped transfer was aborted rather than left pending.
    /// <para>判断批量传输的原生错误是否表示重叠传输被中止，而不是仍在等待。</para></summary>
    /// <remarks>An aborted overlapped transfer cannot be resumed on the same pipe, so it must reach the caller as a
    /// fault; treating it as a short transfer instead turns it into a zero-byte idle read that a protocol loop keeps
    /// waiting on. The timeout path is classified separately because it aborts its own transfer on purpose.
    /// <para>被中止的重叠传输无法在同一管道上继续，因此必须以故障形式上报；把它当成短传输会让协议循环把它
    /// 读作零字节空闲并继续等待。超时路径单独分类，因为它是主动中止自己的传输。</para></remarks>
    /// <param name="nativeError">Native Win32 error code from the bulk transfer.
    /// <para>批量传输返回的 Win32 原生错误码。</para></param>
    /// <returns><see langword="true"/> for <c>ERROR_OPERATION_ABORTED</c>.
    /// <para>错误为 <c>ERROR_OPERATION_ABORTED</c> 时返回 <see langword="true"/>。</para></returns>
    internal static bool IsAbortedTransferError(int nativeError) => nativeError == ERROR_OPERATION_ABORTED;

    /// <summary>
    /// Performs a single chunk transfer with native overlapped (asynchronous) I/O.
    /// <para>使用原生重叠（异步）I/O 执行单次分块传输。</para>
    /// The OVERLAPPED is freed only after the kernel signals completion; on cancellation or
    /// timeout the driver is asked to abort and we wait for the abort to be acknowledged so
    /// the pinned OVERLAPPED is never freed while the kernel still references it (UAF guard).
    /// <para>OVERLAPPED 只在内核发出完成信号后释放；取消或超时时先要求驱动中止并等待
    /// 中止被确认，绝不释放仍被内核引用的固定 OVERLAPPED（UAF 防护）。</para>
    /// </summary>
    private async Task<uint> OverlappedTransferAsync(IntPtr buffer, int length, byte pipeId, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evt = new EventWaitHandle(false, EventResetMode.ManualReset);
        var ovHandle = GCHandle.Alloc(new OVERLAPPED { HEvent = evt.SafeWaitHandle.DangerousGetHandle() }, GCHandleType.Pinned);
        var fileHeld = false;
        var usbHeld = false;
        var quarantined = false;
        RegisteredWaitHandle? waiter = null;
        try
        {
            _fileHandle.DangerousAddRef(ref fileHeld);
            _winUsbHandle.DangerousAddRef(ref usbHeld);
            var file = _fileHandle.DangerousGetHandle();
            var usb = _winUsbHandle.DangerousGetHandle();
            var pointer = ovHandle.AddrOfPinnedObject();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = ThreadPool.RegisterWaitForSingleObject(evt,
                (_, _) => completed.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            var ok = pipeId == _readBulkID
                ? WinUsb_ReadPipe(usb, pipeId, buffer, (uint)length, out var transferred, pointer)
                : WinUsb_WritePipe(usb, pipeId, buffer, (uint)length, out transferred, pointer);
            if (ok)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return transferred;
            }
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_IO_PENDING)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (error == ERROR_SEM_TIMEOUT || (error == ERROR_NO_DATA && RawIoEnabled)) return 0;
                throw new Win32Exception(error);
            }

            var timedOut = false;
            try
            {
                await completed.Task.WaitAsync(timeoutMs < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(timeoutMs),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                timedOut = ex is TimeoutException;
                if (!CancelIoEx(file, pointer))
                {
                    var cancelError = Marshal.GetLastWin32Error();
                    if (cancelError == ERROR_NOT_FOUND)
                    {
                        UsbTrace.Log(
                            "WinUSB: CancelIoEx found no pending operation to cancel; the overlapped transfer completed before the cancel reached the driver, so its completion is read immediately.");
                    }
                    else
                    {
                        UsbTrace.LogError(
                            $"WinUSB: CancelIoEx failed with native error {cancelError}; waiting up to {AbortAcknowledgementTimeout.TotalMilliseconds} ms for the driver to acknowledge the abort.");
                    }
                }

                try
                {
                    await completed.Task.WaitAsync(AbortAcknowledgementTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    UsbTrace.LogError(
                        "WinUSB: the driver did not acknowledge the aborted overlapped transfer; quarantining the OVERLAPPED (never freed while the kernel may reference it) and faulting the session so its device endpoint is released instead of being held forever.");
                    QuarantinedTransfers.Adopt(evt, ovHandle);
                    quarantined = true;
                    throw new Win32Exception(ERROR_OPERATION_ABORTED);
                }
            }
            ok = WinUsb_GetOverlappedResult(usb, pointer, out transferred, false);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            cancellationToken.ThrowIfCancellationRequested();
            if (ok) return transferred;
            if (error == ERROR_SEM_TIMEOUT || timedOut && IsAbortedTransferError(error)) return 0;
            throw new Win32Exception(error);
        }
        finally
        {
            waiter?.Unregister(null);
            if (!quarantined)
            {
                ovHandle.Free();
                evt.Dispose();
            }

            if (usbHeld) _winUsbHandle.DangerousRelease();
            if (fileHeld) _fileHandle.DangerousRelease();
        }
    }

    /// <summary>How long the driver may take to acknowledge <c>CancelIoEx</c> before the transfer is quarantined.
    /// <para>驱动确认 <c>CancelIoEx</c> 的允许时长；超时后该传输被隔离。</para></summary>
    private static readonly TimeSpan AbortAcknowledgementTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Keeps aborted transfers whose completion never arrived alive for the life of the process. The kernel may still
    /// reference the pinned <c>OVERLAPPED</c> and its event, so freeing them would be a use-after-free; the transfer is
    /// abandoned instead, which costs one pinned block per stalled abort and lets the owning session fault so the
    /// exclusive device endpoint returns to the pool.
    /// <para>把完成信号始终未到达的中止传输保留到进程结束。内核仍可能引用固定的 <c>OVERLAPPED</c> 及其事件，
    /// 释放它们会造成释放后使用；因此改为放弃该传输，每次卡住的中止只占一块固定内存，并使所属会话失败，
    /// 让独占设备端点回到可用状态。</para>
    /// </summary>
    private static class QuarantinedTransfers
    {
        private static readonly List<(EventWaitHandle Event, GCHandle Overlapped)> Held = [];
        private static readonly Lock Sync = new();

        internal static void Adopt(EventWaitHandle wait, GCHandle overlapped)
        {
            lock (Sync)
            {
                Held.Add((wait, overlapped));
            }
        }
    }
}