using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.WinUSBAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal class WinUSBDevice : UsbDevice
{
    private const int WinUsbDefaultTimeoutMs = UsbTransferPolicies.WinUsbDefaultTimeoutMs;
    private const int ERROR_SEM_TIMEOUT = 121;
    private const int ERROR_TIMEOUT = 146;
    private const int ERROR_CRC = 23;
    private const int ERROR_NO_DATA = 232;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    private const int ERROR_NO_SUCH_DEVICE = 433;
    private const int ERROR_DEVICE_REMOVED = 1617;

    /// <summary>
    /// Session tuning options applied when the handle is created; assigned by the finder
    /// BEFORE <see cref="CreateHandle"/> so the pipe policies take effect immediately.
    /// <para>创建句柄时应用的会话调优选项；由 finder 在 <see cref="CreateHandle"/> 之前
    /// 赋值，使管道策略立即生效。</para>
    /// </summary>
    internal UsbSessionOptions? SessionOptions { get; set; }

    public override int DefaultTimeoutMs => SessionOptions?.DefaultTimeoutMs ?? WinUsbDefaultTimeoutMs;

    /// <summary>
    /// Whether the bulk IN pipe uses WinUSB RAW_IO (opt-in via
    /// <see cref="UsbSessionOptions.EnableRawIo"/> / <see cref="UsbSessionOptions.AllowPartialReads"/>).
    /// <para>批量 IN 管道是否使用 WinUSB RAW_IO（通过
    /// <see cref="UsbSessionOptions.EnableRawIo"/> / <see cref="UsbSessionOptions.AllowPartialReads"/> 开启）。</para>
    /// </summary>
    private bool RawIoEnabled =>
        SessionOptions?.EnableRawIo == true || SessionOptions?.AllowPartialReads == true;

    private byte _interfaceNum;
    private byte _readBulkID, _writeBulkID;

    public override byte EndpointIn => _readBulkID;
    public override byte EndpointOut => _writeBulkID;
    private SafeWinUsbHandle _winUsbHandle = new(IntPtr.Zero);
    private SafeFileHandle _fileHandle = new(new IntPtr(-1), ownsHandle: true);
    private USBDeviceDescriptor _usbDeviceDescriptor;
    private USBDeviceConfigDescriptor _usbDeviceConfigDescriptor;
    private USBDeviceInterfaceDescriptor _usbDeviceInterfaceDescriptor;
    private int _configuredReadPipeTimeoutMs = -1; // accessed via Volatile.Read/Write (S5)
    private int _configuredWritePipeTimeoutMs = -1; // accessed via Volatile.Read/Write (S5)
    public override int CreateHandle()
    {
        int Fail(int error)
        {
            _winUsbHandle.Dispose();
            _fileHandle.Dispose();
            return error;
        }

        var hUsb = SimpleCreateHandle(DevicePath, true);
        if (hUsb == new IntPtr(-1))
        {
            return Marshal.GetLastWin32Error();
        }

        _fileHandle = new SafeFileHandle(hUsb, ownsHandle: true);
        if (!WinUsb_Initialize(hUsb, out var winUsbHandle))
        {
            return Fail(Marshal.GetLastWin32Error());
        }

        _winUsbHandle = new SafeWinUsbHandle(winUsbHandle);
        if (!WinUsb_GetCurrentAlternateSetting(_winUsbHandle.DangerousGetHandle(), out _interfaceNum))
        {
            return Fail(Marshal.GetLastWin32Error());
        }

        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<USBDeviceDescriptor>());
        if (!WinUsb_GetDescriptor(_winUsbHandle.DangerousGetHandle(), USB_DEVICE_DESCRIPTOR_TYPE, 0, 0, ptr, (uint)Marshal.SizeOf<USBDeviceDescriptor>(), out _))
        {
            Marshal.FreeHGlobal(ptr);
            return Fail(Marshal.GetLastWin32Error());
        }
        _usbDeviceDescriptor = Marshal.PtrToStructure<USBDeviceDescriptor>(ptr);
        VendorId = _usbDeviceDescriptor.IdVendor;
        ProductId = _usbDeviceDescriptor.IdProduct;
        Marshal.FreeHGlobal(ptr);

        ptr = Marshal.AllocHGlobal(Marshal.SizeOf<USBDeviceConfigDescriptor>());
        if (!WinUsb_GetDescriptor(_winUsbHandle.DangerousGetHandle(), USB_CONFIGURATION_DESCRIPTOR_TYPE, 0, 0, ptr, (uint)Marshal.SizeOf<USBDeviceConfigDescriptor>(), out _))
        {
            Marshal.FreeHGlobal(ptr);
            return Fail(Marshal.GetLastWin32Error());
        }
        _usbDeviceConfigDescriptor = Marshal.PtrToStructure<USBDeviceConfigDescriptor>(ptr);
        uint totalConfigLength = _usbDeviceConfigDescriptor.WTotalLength;
        Marshal.FreeHGlobal(ptr);

        var parsedInterfaces = new List<UsbInterfaceInfo>();
        if (totalConfigLength >= Marshal.SizeOf<USBDeviceConfigDescriptor>())
        {
            ptr = Marshal.AllocHGlobal((int)totalConfigLength);
            try
            {
                if (WinUsb_GetDescriptor(_winUsbHandle.DangerousGetHandle(), USB_CONFIGURATION_DESCRIPTOR_TYPE, 0, 0, ptr, totalConfigLength, out var bytesTransferred) && bytesTransferred >= 9)
                {
                    parsedInterfaces.AddRange(ParseConfigurationDescriptor(ptr, (int)bytesTransferred));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        if (!WinUsb_QueryInterfaceSettings(_winUsbHandle.DangerousGetHandle(), _interfaceNum, out _usbDeviceInterfaceDescriptor))
        {
            return Fail(Marshal.GetLastWin32Error());
        }

        InterfaceClass = _usbDeviceInterfaceDescriptor.BInterfaceClass;
        InterfaceSubClass = _usbDeviceInterfaceDescriptor.BInterfaceSubClass;
        InterfaceProtocol = _usbDeviceInterfaceDescriptor.BInterfaceProtocol;
        InterfaceMetadataObserved = true;

        var endpoints = new List<UsbEndpointInfo>();
        for (byte endpoint = 0; endpoint < _usbDeviceInterfaceDescriptor.BNumEndpoints; endpoint++)
        {
            if (!WinUsb_QueryPipe(_winUsbHandle.DangerousGetHandle(), _interfaceNum, endpoint, out var pipeInfo))
            {
                return Fail(Marshal.GetLastWin32Error());
            }

            endpoints.Add(new UsbEndpointInfo
            {
                EndpointAddress = pipeInfo.PipeID,
                Attributes = (byte)pipeInfo.PipeType,
                MaxPacketSize = pipeInfo.MaximumPacketSize,
                Interval = pipeInfo.Interval
            });
            if (pipeInfo.PipeType == WinUSBPipeType.UsbdPipeTypeBulk)
            {
                if ((pipeInfo.PipeID & USB_ENDPOINT_DIRECTION_MASK) != 0)
                {
                    if (_readBulkID == 0)
                    {
                        _readBulkID = pipeInfo.PipeID;
                    }
                }
                else
                {
                    if (_writeBulkID == 0)
                    {
                        _writeBulkID = pipeInfo.PipeID;
                    }
                }
            }
        }

        Interfaces =
        [
            new UsbInterfaceInfo
            {
                InterfaceNumber = _interfaceNum,
                Class = _usbDeviceInterfaceDescriptor.BInterfaceClass,
                SubClass = _usbDeviceInterfaceDescriptor.BInterfaceSubClass,
                Protocol = _usbDeviceInterfaceDescriptor.BInterfaceProtocol,
                Endpoints = endpoints
            }
        ];

        if (_readBulkID == 0 || _writeBulkID == 0)
        {
            return Fail(-1);
        }

        uint speedLength = sizeof(uint);
        if (WinUsb_QueryDeviceInformation(_winUsbHandle.DangerousGetHandle(), DEVICE_SPEED, ref speedLength, out var deviceSpeed))
        {
            Speed = deviceSpeed switch
            {
                1 => UsbDeviceSpeed.Low,
                2 => UsbDeviceSpeed.Full,
                3 => UsbDeviceSpeed.High,
                4 => UsbDeviceSpeed.Super,
                5 => UsbDeviceSpeed.SuperPlus,
                _ => UsbDeviceSpeed.Unknown
            };
        }

        GetSerialNumber();

        byte bTrue = 1;
        byte bFalse = 0;

        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _readBulkID, AUTO_CLEAR_STALL, 1, ref bTrue);
        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _writeBulkID, AUTO_CLEAR_STALL, 1, ref bTrue);
        SetReadPipeTimeout(SessionOptions?.ReadPipeTimeoutMs ?? DefaultTimeoutMs);
        SetWritePipeTimeout(SessionOptions?.WritePipeTimeoutMs ?? DefaultTimeoutMs);

        var readRawIo = RawIoEnabled ? bTrue : bFalse;
        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _readBulkID, RAW_IO, 1, ref readRawIo);
        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _writeBulkID, RAW_IO, 1, ref bFalse);

        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _writeBulkID, SHORT_PACKET_TERMINATE, 1, ref bFalse);

        return 0;
    }

    public IntPtr Handle => !_winUsbHandle.IsInvalid ? _winUsbHandle.DangerousGetHandle() : _fileHandle.DangerousGetHandle();

    /// <summary>
    /// Applies the PIPE_TRANSFER_TIMEOUT policy to the bulk IN pipe (cached per direction).
    /// <para>向批量 IN 管道应用 PIPE_TRANSFER_TIMEOUT 策略（按方向缓存）。</para>
    /// </summary>
    private void SetReadPipeTimeout(int timeoutMs) => SetPipeTimeout(_readBulkID, timeoutMs, ref _configuredReadPipeTimeoutMs);

    /// <summary>
    /// Applies the PIPE_TRANSFER_TIMEOUT policy to the bulk OUT pipe (cached per direction).
    /// <para>向批量 OUT 管道应用 PIPE_TRANSFER_TIMEOUT 策略（按方向缓存）。</para>
    /// </summary>
    private void SetWritePipeTimeout(int timeoutMs) => SetPipeTimeout(_writeBulkID, timeoutMs, ref _configuredWritePipeTimeoutMs);

    private void SetPipeTimeout(byte pipeId, int timeoutMs, ref int cache)
    {
        if (_winUsbHandle.IsInvalid)
        {
            return;
        }

        var effective = timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs ? 0 : timeoutMs;
        if (effective < 0)
        {
            return;
        }

        if (Volatile.Read(ref cache) == effective)
        {
            return;
        }

        var timeout = (uint)effective;
        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), pipeId, PIPE_TRANSFER_TIMEOUT, 4, ref timeout);
        Volatile.Write(ref cache, effective);
    }

    public override void Reset()
    {
        if (!_winUsbHandle.IsInvalid)
        {
            WinUsb_ResetPipe(_winUsbHandle.DangerousGetHandle(), _readBulkID);
            WinUsb_ResetPipe(_winUsbHandle.DangerousGetHandle(), _writeBulkID);
        }
    }

    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        if (buffer == null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(length, 0);
        }
        else
        {
            ValidateBufferRange(buffer, offset, length);
        }

        var transferPacket = new WINUSB_SETUP_PACKET
        {
            RequestType = setupPacket.RequestType,
            Request = setupPacket.Request,
            Value = setupPacket.Value,
            Index = setupPacket.Index,
            Length = (ushort)Math.Max(0, length)
        };

        byte[]? transferBuffer = null;
        var isInDirection = (setupPacket.RequestType & 0x80) != 0;

        if (length > 0)
        {
            if (buffer != null && offset == 0 && length == buffer.Length)
            {
                transferBuffer = buffer;
            }
            else
            {
                transferBuffer = new byte[length];
                if (!isInDirection && buffer != null)
                {
                    Buffer.BlockCopy(buffer, offset, transferBuffer, 0, length);
                }
            }
        }

        if (!WinUsb_ControlTransfer(_winUsbHandle.DangerousGetHandle(), transferPacket, transferBuffer, (uint)length, out var bytesTransferred, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (isInDirection && buffer != null && transferBuffer != null && transferBuffer != buffer)
        {
            Buffer.BlockCopy(transferBuffer, 0, buffer, offset, (int)bytesTransferred);
        }

        return (int)bytesTransferred;
    }

    public override int GetSerialNumber()
    {
        uint bytes_get;
        uint descriptorSize = 64;
        const uint maxDescriptorSize = 4096;
        var ptr = Marshal.AllocHGlobal((int)descriptorSize);
        while (!WinUsb_GetDescriptor(_winUsbHandle.DangerousGetHandle(), USB_STRING_DESCRIPTOR_TYPE,
            _usbDeviceDescriptor.ISerialNumber, 0x0409,
            ptr, descriptorSize, out bytes_get))
        {
            if ((uint)Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
            {
                return Marshal.GetLastWin32Error();
            }

            descriptorSize *= 2;
            if (descriptorSize > maxDescriptorSize)
            {
                Marshal.FreeHGlobal(ptr);
                return -1;
            }
            Marshal.FreeHGlobal(ptr);
            ptr = Marshal.AllocHGlobal((int)descriptorSize);
        }
        if (bytes_get <= 2)
        {
            Marshal.FreeHGlobal(ptr);
            return -1;
        }
        SerialNumber = Marshal.PtrToStringUni(ptr + 2, (int)(bytes_get - 2) / 2)?.TrimEnd('\0');
        Marshal.FreeHGlobal(ptr);
        return 0;
    }

    public override byte[] Read(int length) => Read(length, DefaultTimeoutMs);

    public override byte[] Read(int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return [];
        }

        var data = new byte[length];
        var totalBytesRead = ReadInto(data, 0, length, timeoutMs);
        if (totalBytesRead == length)
        {
            return data;
        }

        if (totalBytesRead == 0)
        {
            return [];
        }

        var realData = new byte[totalBytesRead];
        Buffer.BlockCopy(data, 0, realData, 0, totalBytesRead);
        return realData;
    }

    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, DefaultTimeoutMs);

    protected override string BackendName => "winusb";

    protected override bool IsOpen => !_winUsbHandle.IsInvalid;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => !_winUsbHandle.IsInvalid;

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        SetReadPipeTimeout(timeoutMs);
        if (WinUsb_ReadPipe(_winUsbHandle.DangerousGetHandle(), _readBulkID, buffer, (uint)length, out var bytesRead, IntPtr.Zero))
        {
            return UsbChunkResult.Success((int)bytesRead);
        }

        var err = Marshal.GetLastWin32Error();

        return err == ERROR_NO_DATA && RawIoEnabled
            ? UsbChunkResult.Success(0)
            : err is ERROR_SEM_TIMEOUT or ERROR_TIMEOUT ? UsbChunkResult.Timeout(err) : UsbChunkResult.Fatal(err);
    }

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        SetWritePipeTimeout(timeoutMs);
        if (WinUsb_WritePipe(_winUsbHandle.DangerousGetHandle(), _writeBulkID, buffer, (uint)length, out var bytesWritten, IntPtr.Zero))
        {
            return UsbChunkResult.Success((int)bytesWritten);
        }

        var err = Marshal.GetLastWin32Error();
        return err is ERROR_SEM_TIMEOUT or ERROR_TIMEOUT ? UsbChunkResult.Timeout(err) : UsbChunkResult.Fatal(err);
    }

    protected override bool IsDisconnectionError(int nativeError)
        => nativeError is ERROR_DEVICE_NOT_CONNECTED or ERROR_NO_SUCH_DEVICE or ERROR_DEVICE_REMOVED;

    protected override Exception CreateReadFatalException(int nativeError)
        => IsDisconnectionError(nativeError) ? base.CreateReadFatalException(nativeError) : new Win32Exception(nativeError);

    protected override Exception CreateWriteFatalException(int nativeError)
    {
        if (IsDisconnectionError(nativeError))
        {
            return base.CreateWriteFatalException(nativeError);
        }

        if (nativeError == ERROR_CRC)
        {
            return new Win32Exception(nativeError,
                "USB 写数据循环冗余校验（CRC）失败。请检查线缆/端口，或避免与 adb server 同时占用同一 ADB 接口。"
                + " USB write CRC failure. Check the cable/port, or avoid sharing the ADB interface with an adb server.");
        }

        return new Win32Exception(nativeError);
    }

    public override long Write(byte[] data, int length) => Write(data, length, DefaultTimeoutMs);

    public override void WriteZlp(int timeoutMs)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        SetWritePipeTimeout(timeoutMs);
        if (!WinUsb_WritePipe(_winUsbHandle.DangerousGetHandle(), _writeBulkID, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            if (err is not ERROR_SEM_TIMEOUT and not ERROR_TIMEOUT)
            {
                throw new Win32Exception(err);
            }
        }
    }

    public override UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs)
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

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (WinUsb_ReadPipe(_winUsbHandle.DangerousGetHandle(), endpointAddress,
                new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset), (uint)length, out var bytesRead, IntPtr.Zero))
            {
                return new UsbReadResult((int)bytesRead, isTimeout: false, isShortPacket: bytesRead < length);
            }

            var err = Marshal.GetLastWin32Error();
            return err is ERROR_SEM_TIMEOUT or ERROR_TIMEOUT
                ? new UsbReadResult(0, isTimeout: true, isShortPacket: false)
                : new UsbReadResult(0, isTimeout: false, isShortPacket: false);
        }
        finally
        {
            handle.Free();
        }
    }

    public override long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs)
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

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            return WinUsb_WritePipe(_winUsbHandle.DangerousGetHandle(), endpointAddress,
                new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset), (uint)length, out var bytesWritten, IntPtr.Zero)
                ? bytesWritten
                : 0;
        }
        finally
        {
            handle.Free();
        }
    }

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

    public override void Dispose()
    {
        _winUsbHandle.Dispose();
        _fileHandle.Dispose();
    }

    /// <summary>
    /// Parses a raw USB configuration descriptor buffer into interface/endpoint metadata.
    /// <para>将原始 USB 配置描述符缓冲区解析为接口/端点元数据。</para>
    /// Walks the descriptor chain (configuration → interface → endpoint) so multi-interface
    /// devices report every interface instead of only the WinUSB-bound one.
    /// <para>遍历描述符链（配置 → 接口 → 端点），使多接口设备报告全部接口，
    /// 而非仅 WinUSB 绑定的接口。</para>
    /// </summary>
    /// <param name="buffer">Pointer to the raw configuration descriptor. <para>原始配置描述符指针。</para></param>
    /// <param name="length">Total bytes available. <para>可用总字节数。</para></param>
    /// <returns>The parsed interfaces with their endpoints. <para>解析出的接口及其端点。</para></returns>
    private static List<UsbInterfaceInfo> ParseConfigurationDescriptor(IntPtr buffer, int length)
    {
        var result = new List<UsbInterfaceInfo>();
        if (buffer == IntPtr.Zero || length < 9)
        {
            return result;
        }

        var pos = 0;
        UsbInterfaceInfo? currentInterface = null;
        List<UsbEndpointInfo>? currentEndpoints = null;
        while (pos + 2 <= length)
        {
            int bLength = Marshal.ReadByte(buffer, pos);
            int bDescriptorType = Marshal.ReadByte(buffer, pos + 1);
            if (bLength < 2 || pos + bLength > length)
            {
                break;
            }

            switch (bDescriptorType)
            {
                case 4: // USB_INTERFACE_DESCRIPTOR_TYPE
                    if (currentInterface != null)
                    {
                        result.Add(currentInterface);
                    }

                    currentEndpoints = [];
                    currentInterface = new UsbInterfaceInfo
                    {
                        InterfaceNumber = Marshal.ReadByte(buffer, pos + 2),
                        Class = Marshal.ReadByte(buffer, pos + 5),
                        SubClass = Marshal.ReadByte(buffer, pos + 6),
                        Protocol = Marshal.ReadByte(buffer, pos + 7),
                        Endpoints = currentEndpoints
                    };
                    break;

                case 5: // USB_ENDPOINT_DESCRIPTOR_TYPE
                    currentEndpoints?.Add(new UsbEndpointInfo
                    {
                        EndpointAddress = Marshal.ReadByte(buffer, pos + 2),
                        Attributes = Marshal.ReadByte(buffer, pos + 3),
                        MaxPacketSize = (ushort)(Marshal.ReadByte(buffer, pos + 4) | (Marshal.ReadByte(buffer, pos + 5) << 8)),
                        Interval = Marshal.ReadByte(buffer, pos + 6)
                    });
                    break;
                default:
                    break;
            }

            pos += bLength;
        }

        if (currentInterface != null)
        {
            result.Add(currentInterface);
        }

        return result;
    }


}