using System.ComponentModel;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.WinUSBAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal partial class WinUSBDevice
{
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
            return IsDisconnectionError(ex.NativeErrorCode) ? UsbChunkResult.Fatal(ex.NativeErrorCode) : UsbChunkResult.Error(ex.NativeErrorCode);
        }
    }

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
            return IsDisconnectionError(ex.NativeErrorCode) ? UsbChunkResult.Fatal(ex.NativeErrorCode) : UsbChunkResult.Error(ex.NativeErrorCode);
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
                _ = CancelIoEx(file, pointer);
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