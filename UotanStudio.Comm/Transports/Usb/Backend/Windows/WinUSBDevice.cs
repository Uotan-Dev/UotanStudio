using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.WinUSBAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal partial class WinUSBDevice : UsbDevice
{
    private const int WinUsbDefaultTimeoutMs = UsbTransferPolicies.WinUsbDefaultTimeoutMs;
    private const int ERROR_SEM_TIMEOUT = 121;
    private const int ERROR_TIMEOUT = 146;
    private const int ERROR_CRC = 23;
    private const int ERROR_NO_DATA = 232;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    private const int ERROR_NO_SUCH_DEVICE = 433;
    private const int ERROR_DEVICE_REMOVED = 1617;
    private const int ERROR_NOT_FOUND = 122;

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
    /// RAW_IO is an IN-pipe-only WinUSB policy; it is never applied to the bulk OUT pipe because
    /// the corresponding <c>WinUsb_SetPipePolicy</c> call would always fail there.
    /// <para>批量 IN 管道是否使用 WinUSB RAW_IO（通过
    /// <see cref="UsbSessionOptions.EnableRawIo"/> / <see cref="UsbSessionOptions.AllowPartialReads"/> 开启）。
    /// RAW_IO 是 WinUSB 仅 IN 管道的策略；绝不应用到批量 OUT 管道，因为对应的
    /// <c>WinUsb_SetPipePolicy</c> 调用在 OUT 管道上必然失败。</para>
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
    private int _configuredReadPipeTimeoutMs = -1;
    private int _configuredWritePipeTimeoutMs = -1;
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

        if (!WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _readBulkID, AUTO_CLEAR_STALL, 1, ref bTrue))
        {
            UsbTrace.Log($"WinUSB: AUTO_CLEAR_STALL policy on bulk IN pipe 0x{_readBulkID:X2} failed err={Marshal.GetLastWin32Error()}.");
        }

        if (!WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _writeBulkID, AUTO_CLEAR_STALL, 1, ref bTrue))
        {
            UsbTrace.Log($"WinUSB: AUTO_CLEAR_STALL policy on bulk OUT pipe 0x{_writeBulkID:X2} failed err={Marshal.GetLastWin32Error()}.");
        }

        SetReadPipeTimeout(SessionOptions?.ReadPipeTimeoutMs ?? DefaultTimeoutMs);
        SetWritePipeTimeout(SessionOptions?.WritePipeTimeoutMs ?? DefaultTimeoutMs);

        var readRawIo = RawIoEnabled ? bTrue : bFalse;
        if (!WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _readBulkID, RAW_IO, 1, ref readRawIo))
        {
            UsbTrace.Log($"WinUSB: RAW_IO policy on bulk IN pipe 0x{_readBulkID:X2} failed err={Marshal.GetLastWin32Error()}.");
        }

        if (!WinUsb_SetPipePolicy(_winUsbHandle.DangerousGetHandle(), _writeBulkID, SHORT_PACKET_TERMINATE, 1, ref bFalse))
        {
            UsbTrace.Log($"WinUSB: SHORT_PACKET_TERMINATE policy on bulk OUT pipe 0x{_writeBulkID:X2} failed err={Marshal.GetLastWin32Error()}.");
        }

        return 0;
    }

    /// <summary>
    /// Adds an explicit reference to the WinUSB interface handle and reports the raw pointer,
    /// or returns <see langword="false"/> when the handle is already invalid or disposed. Every
    /// successful acquisition must be paired with <see cref="ReleaseWinUsbHandle"/> in a finally
    /// block so Dispose cannot free the interface while a synchronous native call is in flight.
    /// <para>为 WinUSB 接口句柄显式加引用并报告裸指针；句柄已失效或已释放时返回
    /// <see langword="false"/>。每次成功获取都必须在 finally 中与 <see cref="ReleaseWinUsbHandle"/>
    /// 配对，使 Dispose 无法在同步原生调用进行中释放接口。</para>
    /// </summary>
    /// <param name="handle">The raw interface handle when the acquisition succeeded. <para>获取成功时的原生接口句柄。</para></param>
    /// <returns><see langword="true"/> when a reference was taken. <para>成功取得引用时为 <see langword="true"/>。</para></returns>
    private bool TryAcquireWinUsbHandle(out IntPtr handle)
    {
        var held = false;
        try
        {
            _winUsbHandle.DangerousAddRef(ref held);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            held = false;
        }

        if (held && !_winUsbHandle.IsInvalid)
        {
            handle = _winUsbHandle.DangerousGetHandle();
            return true;
        }

        if (held)
        {
            _winUsbHandle.DangerousRelease();
        }

        handle = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// Acquires the WinUSB interface handle for a synchronous native call, converting the
    /// check-then-use window against <see cref="Dispose"/> into an explicit failure instead of
    /// dereferencing a freed interface pointer.
    /// <para>为同步原生调用获取 WinUSB 接口句柄，把与 <see cref="Dispose"/> 之间的
    /// check-then-use 窗口转为显式失败，而不是解引用已释放的接口指针。</para>
    /// </summary>
    /// <returns>The raw interface handle with a reference held. <para>已持有引用的原生接口句柄。</para></returns>
    /// <exception cref="UsbDeviceHandleClosedException">The handle was disposed or became invalid before the reference could be taken. <para>句柄在取得引用前已释放或失效。</para></exception>
    private IntPtr AcquireWinUsbHandle()
        => TryAcquireWinUsbHandle(out var handle)
            ? handle
            : throw new UsbDeviceHandleClosedException("Device handle is closed.");

    /// <summary>
    /// Releases one reference previously taken by <see cref="TryAcquireWinUsbHandle"/> or
    /// <see cref="AcquireWinUsbHandle"/>.
    /// <para>释放此前由 <see cref="TryAcquireWinUsbHandle"/> 或 <see cref="AcquireWinUsbHandle"/>
    /// 取得的一个引用。</para>
    /// </summary>
    private void ReleaseWinUsbHandle() => _winUsbHandle.DangerousRelease();

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
        var effective = timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs ? 0 : timeoutMs;
        if (effective < 0)
        {
            return;
        }

        if (Volatile.Read(ref cache) == effective)
        {
            return;
        }

        if (!TryAcquireWinUsbHandle(out var usb))
        {
            return;
        }

        try
        {
            var timeout = (uint)effective;
            if (!WinUsb_SetPipePolicy(usb, pipeId, PIPE_TRANSFER_TIMEOUT, 4, ref timeout))
            {
                UsbTrace.Log($"WinUSB: PIPE_TRANSFER_TIMEOUT on pipe 0x{pipeId:X2} ({effective} ms) failed err={Marshal.GetLastWin32Error()}.");
            }

            Volatile.Write(ref cache, effective);
        }
        finally
        {
            ReleaseWinUsbHandle();
        }
    }

    public override void Reset()
    {
        if (!_winUsbHandle.IsInvalid)
        {
            WinUsb_ResetPipe(_winUsbHandle.DangerousGetHandle(), _readBulkID);
            WinUsb_ResetPipe(_winUsbHandle.DangerousGetHandle(), _writeBulkID);
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

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Marshal.Copy(buffer, rented, 0, length);
            var descriptor = rented.AsSpan(0, length);
            var pos = 0;
            UsbInterfaceInfo? currentInterface = null;
            List<UsbEndpointInfo>? currentEndpoints = null;
            while (pos + 2 <= length)
            {
                int bLength = descriptor[pos];
                int bDescriptorType = descriptor[pos + 1];
                if (bLength < 2 || pos + bLength > length)
                {
                    break;
                }

                switch (bDescriptorType)
                {
                    case 4 when bLength >= 8:
                        if (currentInterface != null)
                        {
                            result.Add(currentInterface);
                        }

                        currentEndpoints = [];
                        currentInterface = new UsbInterfaceInfo
                        {
                            InterfaceNumber = descriptor[pos + 2],
                            Class = descriptor[pos + 5],
                            SubClass = descriptor[pos + 6],
                            Protocol = descriptor[pos + 7],
                            Endpoints = currentEndpoints
                        };
                        break;

                    case 5 when bLength >= 7:
                        currentEndpoints?.Add(new UsbEndpointInfo
                        {
                            EndpointAddress = descriptor[pos + 2],
                            Attributes = descriptor[pos + 3],
                            MaxPacketSize = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[(pos + 4)..]),
                            Interval = descriptor[pos + 6]
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
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return result;
    }
}