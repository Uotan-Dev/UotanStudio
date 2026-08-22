using System.ComponentModel;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Microsoft.Win32.SafeHandles;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;
using UotanStudio.Comm.Transports.Usb.Backend;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal class LegacyUsbDevice : UsbDevice
{
    private const int IoTimeoutMs = 30000;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    private const int ERROR_NO_SUCH_DEVICE = 433;
    private const int ERROR_DEVICE_REMOVED = 1617;
    public override int DefaultTimeoutMs => IoTimeoutMs;
    public static uint IoGetSerialCode => CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static uint IoGetDescriptorCode => CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static uint IoControlTransferCode => CTL_CODE(FILE_DEVICE_UNKNOWN, 0x803, METHOD_BUFFERED, FILE_READ_ACCESS);

    private SafeFileHandle fileHandle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
    private bool _disposed;

    public IntPtr Handle => fileHandle.IsInvalid ? INVALID_HANDLE_VALUE : fileHandle.DangerousGetHandle();

    public override int CreateHandle()
    {
        fileHandle = new SafeFileHandle(SimpleCreateHandle(DevicePath), ownsHandle: true);
        if (fileHandle.IsInvalid)
            return Marshal.GetLastWin32Error();

        if (!CheckInterface())
        {
            fileHandle.Dispose();
            return -1;
        }

        GetSerialNumber();
        return 0;
    }

    private bool CheckInterface()
    {
        var buffer = new byte[256];
        uint returned;
        return DeviceIoControl(fileHandle.DangerousGetHandle(), IoGetSerialCode, null, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero);
    }

    public override int GetSerialNumber()
    {
        var buffer = new byte[256];
        uint returned;
        if (DeviceIoControl(fileHandle.DangerousGetHandle(), IoGetSerialCode, null, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero))
        {
            SerialNumber = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)returned).TrimEnd('\0');
            return 0;
        }
        return Marshal.GetLastWin32Error();
    }

    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_disposed || fileHandle.IsInvalid)
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
            uint returned;

            if (!DeviceIoControl(fileHandle.DangerousGetHandle(), IoControlTransferCode, inBuffer, (uint)totalInSize, outBuffer, (uint)outBuffer.Length, out returned, IntPtr.Zero))
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

        // Synchronous ReadFile (no Task.Run): the previous implementation parked a thread-pool
        // thread on the blocking read and could not cancel it after the wait timed out, leaking
        // the thread until the driver eventually returned. ReadFile blocks the caller instead;
        // timeout behaviour is delegated to the legacy driver (ReadChunk ignores timeoutMs).
        var buffer = new byte[length];
        var count = ReadInto(buffer, 0, length, IoTimeoutMs);
        if (count == length) return buffer;
        if (count == 0) return [];

        var result = new byte[count];
        Buffer.BlockCopy(buffer, 0, result, 0, count);
        return result;
    }

    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, IoTimeoutMs);

    protected override string BackendName => "winusb-legacy";

    protected override bool IsOpen => !_disposed && !fileHandle.IsInvalid;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => !_disposed && !fileHandle.IsInvalid;

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        uint bytesRead;
        if (!ReadFile(fileHandle.DangerousGetHandle(), buffer, (uint)length, out bytesRead, IntPtr.Zero))
        {
            return UsbChunkResult.Fatal(Marshal.GetLastWin32Error());
        }
        return UsbChunkResult.Success((int)bytesRead);
    }

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        uint written;
        if (!WriteFile(fileHandle.DangerousGetHandle(), buffer, (uint)length, out written, IntPtr.Zero))
        {
            return UsbChunkResult.Fatal(Marshal.GetLastWin32Error());
        }
        return UsbChunkResult.Success((int)written);
    }

    protected override bool IsDisconnectionError(int nativeError)
        => nativeError == ERROR_DEVICE_NOT_CONNECTED || nativeError == ERROR_NO_SUCH_DEVICE || nativeError == ERROR_DEVICE_REMOVED;

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
        if (_disposed) return;
        _disposed = true;

        if (!fileHandle.IsInvalid)
        {
            fileHandle.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
