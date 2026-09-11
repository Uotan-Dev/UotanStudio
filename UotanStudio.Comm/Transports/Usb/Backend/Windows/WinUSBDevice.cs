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
        // Releases the file/interface handles already acquired when a step fails,
        // so callers are not required to Dispose() on a non-zero return.
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

        // Configuration descriptor: a fixed-size struct only captures the 9-byte header,
        // truncating multi-interface devices. Read the header first to learn WTotalLength,
        // then read the full descriptor and parse every interface/endpoint from it.
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

        // WinUSB binds a single interface per handle; report that interface's metadata.
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

        // Negotiated USB speed (UsbLowSpeed=1, UsbFullSpeed=2, UsbHighSpeed=3,
        // UsbSuperSpeed=4, UsbSuperSpeedPlus=5). Failure leaves Speed as Unknown.
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

        // Policy configuration (initial timeout from session options, falling back to the
        // WinUSB 60s default used for large flash operations).
        // <para>策略配置（初始超时取自会话选项，回退到用于大块刷写操作的 WinUSB 60s 默认值）。</para>
        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _readBulkID, AUTO_CLEAR_STALL, 1, ref bTrue);
        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _writeBulkID, AUTO_CLEAR_STALL, 1, ref bTrue);
        SetReadPipeTimeout(SessionOptions?.ReadPipeTimeoutMs ?? DefaultTimeoutMs);
        SetWritePipeTimeout(SessionOptions?.WritePipeTimeoutMs ?? DefaultTimeoutMs);

        // WinUSB RAW_IO can significantly improve stability for large transfers.
        // It requires that the transfer size is a multiple of the packet size (typically 512).
        // When the session opts into RAW_IO (EnableRawIo / AllowPartialReads), the bulk IN
        // pipe is switched to raw mode so reads return as soon as data is buffered and
        // report ERROR_NO_DATA immediately when the device has none (no-data reads return 0
        // instead of blocking until the pipe timeout).
        // <para>WinUSB RAW_IO 可显著提升大块传输的稳定性，要求传输大小是包大小
        // （通常 512）的整数倍。会话开启 RAW_IO（EnableRawIo / AllowPartialReads）时，
        // 批量 IN 管道切换为原始模式：有数据即返回，无数据立即以 ERROR_NO_DATA 报告
        // （无数据读返回 0 而非阻塞至管道超时）。</para>
        var readRawIo = RawIoEnabled ? bTrue : bFalse;
        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _readBulkID, RAW_IO, 1, ref readRawIo);
        WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _writeBulkID, RAW_IO, 1, ref bFalse);

        // Align with AOSP host behavior: avoid forcing ZLP from the host side.
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

        // WinUSB's PIPE_TRANSFER_TIMEOUT treats 0 as "no timeout"; map the -1 sentinel
        // accordingly so an unbounded wait is actually requested from the driver.
        var effective = timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs ? 0 : timeoutMs;
        if (effective < 0)
        {
            return;
        }

        // Cache the configured value so per-chunk calls do not hit WinUsb_SetPipePolicy
        // (a kernel transition) when the timeout has not changed. Read/write via Volatile:
        // ReadChunk and WriteChunk run concurrently on separate direction gates, so the
        // cache must not be a torn/unsynchronized read.
        // <para>缓存已配置的值，避免每分块在超时未变化时触发 WinUsb_SetPipePolicy
        // （内核切换）。经 Volatile 读写：ReadChunk 与 WriteChunk 在各自方向门闩上并发，
        // 缓存读写不得撕裂。</para>
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
                // A hostile/errant device must not drive unbounded descriptor reallocation.
                // <para>恶意/异常设备不得驱动无上限的描述符重复分配。</para>
                Marshal.FreeHGlobal(ptr);
                return -1;
            }
            Marshal.FreeHGlobal(ptr);
            ptr = Marshal.AllocHGlobal((int)descriptorSize);
        }
        // A string descriptor is at least 2 header bytes; guard against a
        // malformed short reply producing a negative PtrToStringUni length.
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

        // With RAW_IO enabled the driver fails an empty read with ERROR_NO_DATA (232)
        // instead of blocking until the pipe timeout. Treat it as a successful 0-byte
        // read (no data yet) — NOT a fatal error — so the caller's polling loop can
        // spin quickly while keeping its own timeout budget.
        // <para>开启 RAW_IO 后，空读由驱动以 ERROR_NO_DATA (232) 失败，而非阻塞至管道
        // 超时。将其视为成功的 0 字节读（暂无数据）——而不是致命错误——使调用方的轮询
        // 循环可快速空转，同时自持超时预算。</para>
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
        => IsDisconnectionError(nativeError) ? base.CreateWriteFatalException(nativeError) : new Win32Exception(nativeError);

    public override long Write(byte[] data, int length) => Write(data, length, DefaultTimeoutMs);

    public override void WriteZlp(int timeoutMs)
    {
        if (_winUsbHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        SetWritePipeTimeout(timeoutMs);
        // A zero-length bulk OUT write transmits a ZLP, ending a transfer whose length is an
        // exact multiple of the endpoint max packet size.
        // <para>零长度批量 OUT 写会发送 ZLP，结束长度恰为端点最大包大小整数倍的传输。</para>
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

            // With RAW_IO a completed 0-byte read means "no data buffered yet", not a
            // timeout — the driver reports ERROR_NO_DATA via the overlapped completion.
            // <para>开启 RAW_IO 后，完成的 0 字节读表示"暂无缓冲数据"，而非超时——
            // 驱动通过 overlapped 完成报告 ERROR_NO_DATA。</para>
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
            // Keep both handles, the OVERLAPPED and the caller's pinned buffer
            // alive until the kernel has completed, including after cancellation.
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
                // CancelIoEx takes the CreateFile handle, NOT the WinUSB interface.
                // Cancellation is only a request; it does not release I/O ownership.
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

                    // Endpoints is declared as IReadOnlyList<T>, so a collection expression
                    // ([]) would materialize an array that cannot be appended to. Keep a
                    // mutable List<T> per interface and attach it to the interface directly.
                    // <para>Endpoints 声明为 IReadOnlyList&lt;T&gt;，集合表达式（[]）会生成
                    // 无法追加的数组；这里为每个接口维护可变 List&lt;T&gt; 并直接挂到接口上。</para>
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