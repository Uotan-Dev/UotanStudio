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
    /// <remarks>A non-disconnection native error collapses to <c>UsbChunkResult.Error</c>, which the base read loop treats as a short transfer with the error code only reaching the transfer trace; the synchronous <c>ReadChunk</c> instead raises a fatal error (<c>UsbChunkStatus.FatalError</c>) for the same error. The collapse is kept because turning it into a fatal failure changes per-protocol retry behavior and needs real-device regression before it can be flipped; the native error is logged here so the collapse is never silent at the backend layer.
    /// <para>非断连类原生错误折叠为 <c>UsbChunkResult.Error</c>，基类读取循环将其 视为短传输且错误码只进入传输 trace；同样的错误在同步 <c>ReadChunk</c> 会报致命错误 （<c>UsbChunkStatus.FatalError</c>）并抛出。保留折叠是因为改为致命失败会改变各协议的 重试行为，需真机回归后才能翻转；此处记录原生错误码，使后端层的折叠不再静默。</para></remarks>
    protected override async Task<UsbChunkResult> ReadChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        try
        {
            var transferred = await OverlappedTransferAsync(buffer, length, _readBulkID, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (transferred > 0)
            {
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

            UsbTrace.LogError($"WinUSB: async bulk IN chunk failed with native error {ex.NativeErrorCode}; collapsed to a retryable error outcome.");
            return UsbChunkResult.Error(ex.NativeErrorCode);
        }
    }

    /// <summary>Asynchronously writes one bulk OUT chunk through overlapped I/O.
    /// <para>通过重叠 I/O 异步写入一个批量 OUT 分块。</para></summary>
    /// <remarks>A non-disconnection native error collapses to <c>UsbChunkResult.Error</c>, which the base write loop treats as a short write indistinguishable from a device-side short completion, while the synchronous <c>WriteChunk</c> raises a fatal failure for the same error. The collapse is kept pending per-protocol retry review and real-device regression; the native error is logged here so the collapse is never silent at the backend layer.
    /// <para>非断连类原生错误折叠为 <c>UsbChunkResult.Error</c>，基类写入循环将其 视为与设备侧短完成不可区分的短写，而同样的错误在同步 <c>WriteChunk</c> 会报致命失败。 保留折叠待逐协议重试策略复核与真机回归后再定；此处记录原生错误码，使后端层的折叠不再静默。</para></remarks>
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
        using var evt = new EventWaitHandle(false, EventResetMode.ManualReset);
        var ovHandle = GCHandle.Alloc(new OVERLAPPED { HEvent = evt.SafeWaitHandle.DangerousGetHandle() }, GCHandleType.Pinned);
        var fileHeld = false;
        var usbHeld = false;
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
                    UsbTrace.LogError(cancelError == ERROR_NOT_FOUND
                        ? "WinUSB: CancelIoEx found no pending operation to cancel; the overlapped transfer already completed and the completion wait returns immediately."
                        : $"WinUSB: CancelIoEx failed with native error {cancelError}; the completion wait below stays unbounded by design (UAF guard) until the driver signals the OVERLAPPED.");
                }

                await completed.Task.ConfigureAwait(false);
            }
            ok = WinUsb_GetOverlappedResult(usb, pointer, out transferred, false);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            cancellationToken.ThrowIfCancellationRequested();
            if (ok) return transferred;
            if (error == ERROR_SEM_TIMEOUT || (timedOut && error == 995)) return 0;
            throw new Win32Exception(error);
        }
        finally
        {
            waiter?.Unregister(null);
            ovHandle.Free();
            if (usbHeld) _winUsbHandle.DangerousRelease();
            if (fileHeld) _fileHandle.DangerousRelease();
        }
    }
}