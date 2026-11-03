using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Firmware.Core.Text;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Drives the vendor bulkusb-style driver interface through its private IOCTLs and plain ReadFile/WriteFile.
/// <para>通过私有 IOCTL 与普通 ReadFile/WriteFile 驱动厂商 bulkusb 风格驱动接口。</para>
/// </summary>
/// <remarks>
/// The handle is opened for synchronous I/O, so the per-call <c>timeoutMs</c> a chunk method receives cannot
/// be enforced by the OS; a stuck driver blocks the caller until the I/O completes or the device disappears,
/// and only the driver's own timeout errors surface as <c>UsbChunkStatus.Timeout</c>.
/// <para>句柄以同步 I/O 打开，因此分块方法收到的每次调用超时无法由操作系统强制；驱动卡住时调用方会阻塞到
/// I/O 完成或设备消失，只有驱动自身返回的超时错误才会表现为 <c>UsbChunkStatus.Timeout</c>。</para>
/// </remarks>
internal class LegacyUsbDevice : UsbDevice
{
    private const int IoTimeoutMs = 30000;
    private const int ERROR_SEM_TIMEOUT = 121;
    private const int ERROR_TIMEOUT = 146;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    private const int ERROR_NO_SUCH_DEVICE = 433;
    private const int ERROR_DEVICE_REMOVED = 1617;
    public override int DefaultTimeoutMs => IoTimeoutMs;
    public static uint IoGetSerialCode => CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static uint IoGetDescriptorCode => CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static uint IoControlTransferCode => CTL_CODE(FILE_DEVICE_UNKNOWN, 0x803, METHOD_BUFFERED, FILE_READ_ACCESS);

    private SafeFileHandle _fileHandle = new(new IntPtr(-1), ownsHandle: true);
    private bool _disposed;

    public IntPtr Handle => _fileHandle.IsInvalid ? INVALID_HANDLE_VALUE : _fileHandle.DangerousGetHandle();

    public override int CreateHandle()
    {
        _fileHandle = new SafeFileHandle(SimpleCreateHandle(DevicePath), ownsHandle: true);
        if (_fileHandle.IsInvalid)
        {
            return Marshal.GetLastWin32Error();
        }

        if (!CheckInterface())
        {
            _fileHandle.Dispose();
            return -1;
        }

        GetSerialNumber();
        return 0;
    }

    private bool CheckInterface()
    {
        var buffer = new byte[256];
        return DeviceIoControl(_fileHandle.DangerousGetHandle(), IoGetSerialCode, null, 0, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
    }

    public override int GetSerialNumber()
    {
        var buffer = new byte[256];
        if (DeviceIoControl(_fileHandle.DangerousGetHandle(), IoGetSerialCode, null, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero))
        {
            SerialNumber = System.Text.Encoding.Unicode.GetStringTrimNul(buffer, 0, (int)returned);
            return 0;
        }
        return Marshal.GetLastWin32Error();
    }

    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_disposed || _fileHandle.IsInvalid)
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

        var setupSize = Marshal.SizeOf<WinUSBAPI.WINUSB_SETUP_PACKET>();
        var nativeSetup = new WinUSBAPI.WINUSB_SETUP_PACKET
        {
            RequestType = setupPacket.RequestType,
            Request = setupPacket.Request,
            Value = setupPacket.Value,
            Index = setupPacket.Index,
            Length = (ushort)length
        };

        var totalInSize = setupSize + length;
        var inBuffer = Marshal.AllocHGlobal(totalInSize);
        try
        {
            Marshal.StructureToPtr(nativeSetup, inBuffer, false);

            if (length > 0 && buffer != null && (setupPacket.RequestType & 0x80) == 0)
            {
                Marshal.Copy(buffer, offset, new IntPtr(inBuffer.ToInt64() + setupSize), length);
            }

            var outBuffer = new byte[setupSize + length];

            if (!DeviceIoControl(_fileHandle.DangerousGetHandle(), IoControlTransferCode, inBuffer, (uint)totalInSize, outBuffer, (uint)outBuffer.Length, out var returned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var transferred = (int)returned;
            if ((setupPacket.RequestType & 0x80) != 0 && buffer != null && transferred > 0)
            {
                var copyLen = Math.Min(transferred, length);
                Buffer.BlockCopy(outBuffer, 0, buffer, offset, copyLen);
                return copyLen;
            }

            return transferred;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
        }
    }

    public override byte[] Read(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var buffer = new byte[length];
        var count = ReadInto(buffer, 0, length, IoTimeoutMs);
        if (count == length)
        {
            return buffer;
        }

        if (count == 0)
        {
            return [];
        }

        var result = new byte[count];
        Buffer.BlockCopy(buffer, 0, result, 0, count);
        return result;
    }

    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, IoTimeoutMs);

    protected override string BackendName => "winusb-legacy";

    protected override bool IsOpen => !_disposed && !_fileHandle.IsInvalid;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => !_disposed && !_fileHandle.IsInvalid;

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (ReadFile(_fileHandle.DangerousGetHandle(), buffer, (uint)length, out var bytesRead, IntPtr.Zero))
        {
            return UsbChunkResult.Success((int)bytesRead);
        }

        var error = Marshal.GetLastWin32Error();
        return error is ERROR_SEM_TIMEOUT or ERROR_TIMEOUT
            ? UsbChunkResult.Timeout(error)
            : UsbChunkResult.Fatal(error);
    }

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (WriteFile(_fileHandle.DangerousGetHandle(), buffer, (uint)length, out var written, IntPtr.Zero))
        {
            return UsbChunkResult.Success((int)written);
        }

        var error = Marshal.GetLastWin32Error();
        return error is ERROR_SEM_TIMEOUT or ERROR_TIMEOUT
            ? UsbChunkResult.Timeout(error)
            : UsbChunkResult.Fatal(error);
    }

    protected override bool IsDisconnectionError(int nativeError)
        => nativeError is ERROR_DEVICE_NOT_CONNECTED or ERROR_NO_SUCH_DEVICE or ERROR_DEVICE_REMOVED;

    protected override Exception CreateReadFatalException(int nativeError)
        => IsDisconnectionError(nativeError) ? base.CreateReadFatalException(nativeError) : new Win32Exception(nativeError);

    protected override Exception CreateWriteFatalException(int nativeError)
        => IsDisconnectionError(nativeError) ? base.CreateWriteFatalException(nativeError) : new Win32Exception(nativeError);

    public override long Write(byte[] data, int length) => Write(data, length, IoTimeoutMs);

    public override void Reset()
    {
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_fileHandle.IsInvalid)
        {
            _fileHandle.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}