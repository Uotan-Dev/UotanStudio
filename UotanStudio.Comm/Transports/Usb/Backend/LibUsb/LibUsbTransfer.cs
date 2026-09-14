using System.Runtime.InteropServices;
using LibUsbDotNet;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>Owns one native transfer through its completion callback, including cancellation.</summary>
internal static partial class LibUsbTransfer
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void CompletedCallback(IntPtr transfer);

    internal interface INativeApi
    {
        IntPtr Allocate();
        Error Submit(IntPtr transfer);
        Error Cancel(IntPtr transfer);
        void Free(IntPtr transfer);
    }

    private sealed class NativeApi : INativeApi
    {
        public IntPtr Allocate() => libusb_alloc_transfer(0);
        public Error Submit(IntPtr transfer) => (Error)libusb_submit_transfer(transfer);
        public Error Cancel(IntPtr transfer) => (Error)libusb_cancel_transfer(transfer);
        public void Free(IntPtr transfer) => libusb_free_transfer(transfer);
    }

    [LibraryImport("libusb-1.0")] private static partial IntPtr libusb_alloc_transfer(int packets);
    [LibraryImport("libusb-1.0")] private static partial int libusb_submit_transfer(IntPtr transfer);
    [LibraryImport("libusb-1.0")] private static partial int libusb_cancel_transfer(IntPtr transfer);
    [LibraryImport("libusb-1.0")] private static partial void libusb_free_transfer(IntPtr transfer);

    private static readonly INativeApi Native = new NativeApi();

    internal static async Task<(Error Error, int Count)> ExecuteAsync(SafeHandle handle, byte endpoint,
        TransferType type, byte[] buffer, int timeoutMs, CancellationToken cancellationToken, INativeApi? api = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = new Operation(handle, endpoint, type, buffer, timeoutMs, api ?? Native);
        operation.Submit();
        using var registration = cancellationToken.Register(static state => ((Operation)state!).Cancel(), operation);
        // Cancellation only asks libusb to stop. Completion owns the pinned buffer and
        // handle until libusb confirms that it no longer borrows either resource.
        var result = await operation.Completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static int NativeTimeout(int timeoutMs)
    {
        var normalized = UsbTransferPolicies.NormalizeTimeout(timeoutMs, UsbTransferPolicies.DefaultTimeoutMs);
        return normalized == UsbTransferPolicies.InfiniteTimeoutMs ? 0 : normalized;
    }

    internal static Error FromStatus(TransferStatus status) => status switch
    {
        TransferStatus.Completed => Error.Success,
        TransferStatus.TimedOut => Error.Timeout,
        TransferStatus.Stall => Error.Pipe,
        TransferStatus.NoDevice => Error.NoDevice,
        TransferStatus.Overflow => Error.Overflow,
        TransferStatus.Cancelled => Error.Interrupted,
        _ => Error.Io,
    };

    internal static void ThrowOnError(Error error)
    {
        if (error == Error.NoDevice)
            throw new UsbDeviceDisconnectedException("libusb transfer failed: device disconnected.", (int)error);
        if (error != Error.Success)
            throw new UsbTransferException($"libusb transfer failed: {error}.", (int)error) { Backend = "libusb" };
    }

    internal static UsbReadResult ReadResult(Error error, int count, int requested)
    {
        if (error != Error.Timeout) ThrowOnError(error);
        if ((uint)count > (uint)requested) throw new InvalidDataException("libusb returned an invalid transfer length.");
        return new(count, error == Error.Timeout, error == Error.Success && count < requested);
    }

    private sealed unsafe class Operation
    {
        private static readonly CompletedCallback Callback = Complete;
        private static readonly IntPtr CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);
        private readonly Lock _gate = new();
        private readonly INativeApi _api;
        private readonly SafeHandle _handle;
        private GCHandle _buffer;
        private GCHandle _self;
        private bool _retained;
        private IntPtr _transfer;
        internal TaskCompletionSource<(Error Error, int Count)> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Operation(SafeHandle handle, byte endpoint, TransferType type, byte[] buffer, int timeoutMs, INativeApi api)
        {
            _handle = handle; _api = api;
            try
            {
                handle.DangerousAddRef(ref _retained);
                _buffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                _self = GCHandle.Alloc(this);
                _transfer = api.Allocate();
                if (_transfer == IntPtr.Zero) throw new OutOfMemoryException("libusb could not allocate a transfer.");
                var transfer = (Transfer*)_transfer;
                transfer->DevHandle = handle.DangerousGetHandle();
                transfer->Endpoint = endpoint;
                transfer->Type = (byte)type;
                transfer->Timeout = (uint)NativeTimeout(timeoutMs);
                transfer->Buffer = (byte*)_buffer.AddrOfPinnedObject();
                transfer->Length = buffer.Length;
                transfer->Flags = 0;
                transfer->NumIsoPackets = 0;
                transfer->Callback = CallbackPointer;
                transfer->UserData = GCHandle.ToIntPtr(_self);
            }
            catch { Release(); throw; }
        }

        internal void Submit()
        {
            lock (_gate)
            {
                try
                {
                    var error = _api.Submit(_transfer);
                    if (error != Error.Success) { Release(); ThrowOnError(error); }
                }
                catch { Release(); throw; }
            }
        }

        internal void Cancel()
        {
            lock (_gate)
            {
                // A callback may already have completed and freed the transfer. A failed
                // cancellation request never grants permission to free an in-flight transfer.
                if (_transfer != IntPtr.Zero) _api.Cancel(_transfer);
            }
        }

        private static void Complete(IntPtr pointer)
        {
            var transfer = (Transfer*)pointer;
            var operation = (Operation)GCHandle.FromIntPtr(transfer->UserData).Target!;
            lock (operation._gate)
            {
                var result = (FromStatus(transfer->Status), transfer->ActualLength);
                operation.Release();
                operation.Completion.TrySetResult(result);
            }
        }

        private void Release()
        {
            if (_transfer != IntPtr.Zero) { _api.Free(_transfer); _transfer = IntPtr.Zero; }
            if (_buffer.IsAllocated) _buffer.Free();
            if (_self.IsAllocated) _self.Free();
            if (_retained) { _handle.DangerousRelease(); _retained = false; }
        }
    }
}