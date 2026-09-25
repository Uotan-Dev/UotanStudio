using System.ComponentModel;
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