using System.ComponentModel;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.WinUSBAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal partial class WinUSBDevice
{
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

}