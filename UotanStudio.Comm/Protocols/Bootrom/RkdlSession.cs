using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Implements the Rockchip RKDL (Rockchip Download) protocol over USB control and bulk transfers.
/// <para>在 USB 控制和批量传输之上实现 Rockchip RKDL 协议。</para>
/// Protocol flow:
/// 1. Download first-stage loader via vendor request 0x471
/// 2. Device re-enumerates, download second-stage loader via vendor request 0x472
/// 3. Device enters rockusb mode; use bulk transfers for flash operations
/// 4. Commands: read/write LBA, write GPT, erase flash, reset
/// <para>协议流程：通过 vendor request 0x471 下载第一阶段 loader → 设备重新枚举 →
/// 通过 vendor request 0x472 下载第二阶段 loader → 设备进入 rockusb 模式 →
/// 使用批量传输进行 flash 操作。</para>
/// </summary>
public sealed class RkdlSession : IDisposable
{
    private readonly IUsbDeviceSession _transport;
    private readonly int _timeoutMs;
    private bool _disposed;

    /// <summary>Gets the USB vendor ID. USB 厂商 ID。</summary>
    public ushort VendorId => _transport.DeviceInfo.VendorId;

    /// <summary>Gets the USB product ID. USB 产品 ID。</summary>
    public ushort ProductId => _transport.DeviceInfo.ProductId;

    /// <summary>Gets whether the device is in rockusb mode (ready for flash operations).
    /// 设备是否处于 rockusb 模式（就绪可刷写）。</summary>
    public bool IsRockusbMode => ProductId is 0x320A or 0x330C or 0x350B or 0x320B or 0x300A or 0x330A or 0x350A or 0x350D or 0x350E;

    /// <summary>Creates a new RKDL session. 创建新的 RKDL 会话。</summary>
    public RkdlSession(IUsbDeviceSession transport, int? timeoutMs = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeoutMs = timeoutMs ?? RkdlConstants.DefaultTimeoutMs;
    }

    /// <summary>
    /// Checks if this is a Rockchip bootrom device (maskrom or loader mode).
    /// <para>检查是否为 Rockchip bootrom 设备（maskrom 或 loader 模式）。</para>
    /// </summary>
    public bool IsRockchipDevice()
    {
        if (VendorId != RkdlConstants.RockchipVid) return false;
        return ProductId is 0x350B or 0x320B or 0x330C or 0x320A or 0x330A or 0x350A or 0x300A or 0x350D or 0x350E or 0x330D;
    }

    /// <summary>
    /// Downloads the first-stage loader via vendor request 0x471.
    /// <para>通过 vendor request 0x471 下载第一阶段 loader。</para>
    /// </summary>
    public int DownloadBootLoader471(byte[] loaderData)
    {
        ThrowIfDisposed();
        if (loaderData == null || loaderData.Length == 0)
            throw new RkdlException(RkdlError.InvalidParameter, "Loader data cannot be null or empty.");

        // Vendor request 0x471: wValue = load address, wIndex = block count
        var setupPacket = new UsbSetupPacket
        {
            RequestType = 0x40, // Vendor, device-to-host = OUT
            Request = RkdlConstants.RkdlRequest471,
            Value = 0x0000,
            Index = 0x0000,
            Length = 0
        };

        var result = _transport.ControlTransfer(setupPacket, loaderData, 0, loaderData.Length, _timeoutMs);
        return result;
    }

    /// <summary>
    /// Downloads the second-stage loader via vendor request 0x472.
    /// <para>通过 vendor request 0x472 下载第二阶段 loader。</para>
    /// </summary>
    public int DownloadBootLoader472(byte[] loaderData)
    {
        ThrowIfDisposed();
        if (loaderData == null || loaderData.Length == 0)
            throw new RkdlException(RkdlError.InvalidParameter, "Loader data cannot be null or empty.");

        var setupPacket = new UsbSetupPacket
        {
            RequestType = 0x40,
            Request = RkdlConstants.RkdlRequest472,
            Value = 0x0000,
            Index = 0x0000,
            Length = 0
        };

        var result = _transport.ControlTransfer(setupPacket, loaderData, 0, loaderData.Length, _timeoutMs);
        return result;
    }

    /// <summary>
    /// Reads chip info from the device.
    /// <para>从设备读取芯片信息。</para>
    /// </summary>
    public RkChipInfo ReadChipInfo()
    {
        ThrowIfDisposed();
        var setupPacket = new UsbSetupPacket
        {
            RequestType = 0xC0, // Vendor, device-to-device = IN
            Request = RkdlConstants.RkdlRequestChipInfo,
            Value = 0x0000,
            Index = 0x0000,
            Length = 16
        };

        var buffer = new byte[16];
        var result = _transport.ControlTransfer(setupPacket, buffer, 0, buffer.Length, _timeoutMs);
        if (result < 8)
            throw new RkdlException(RkdlError.ChipInfoFailed, $"Chip info read failed: {result} bytes.");

        return new RkChipInfo
        {
            ChipId = System.BitConverter.ToUInt32(buffer, 0),
            Revision = System.BitConverter.ToUInt32(buffer, 4)
        };
    }

    /// <summary>
    /// Reads flash info from the device.
    /// <para>从设备读取 flash 信息。</para>
    /// </summary>
    public RkFlashInfo ReadFlashInfo()
    {
        ThrowIfDisposed();
        var setupPacket = new UsbSetupPacket
        {
            RequestType = 0xC0,
            Request = RkdlConstants.RkdlRequestFlashInfo,
            Value = 0x0000,
            Index = 0x0000,
            Length = 16
        };

        var buffer = new byte[16];
        var result = _transport.ControlTransfer(setupPacket, buffer, 0, buffer.Length, _timeoutMs);
        if (result < 8)
            throw new RkdlException(RkdlError.FlashInfoFailed, $"Flash info read failed: {result} bytes.");

        return new RkFlashInfo
        {
            Size = System.BitConverter.ToInt64(buffer, 0),
            SectorSize = System.BitConverter.ToInt32(buffer, 8)
        };
    }

    /// <summary>
    /// Reads flash ID.
    /// <para>读取 flash ID。</para>
    /// </summary>
    public byte[] ReadFlashId()
    {
        ThrowIfDisposed();
        var setupPacket = new UsbSetupPacket
        {
            RequestType = 0xC0,
            Request = RkdlConstants.RkdlRequestFlashId,
            Value = 0x0000,
            Index = 0x0000,
            Length = 5
        };

        var buffer = new byte[5];
        var result = _transport.ControlTransfer(setupPacket, buffer, 0, buffer.Length, _timeoutMs);
        if (result < 5)
            throw new RkdlException(RkdlError.ReadFailed, $"Flash ID read failed: {result} bytes.");

        return buffer;
    }

    /// <summary>
    /// Resets the device.
    /// <para>复位设备。</para>
    /// </summary>
    public int ResetDevice()
    {
        ThrowIfDisposed();
        var setupPacket = new UsbSetupPacket
        {
            RequestType = 0x40,
            Request = RkdlConstants.RkdlRequestReset,
            Value = 0x0000,
            Index = 0x0000,
            Length = 0
        };

        var result = _transport.ControlTransfer(setupPacket, null, 0, 0, _timeoutMs);
        return result;
    }

    /// <summary>
    /// Erases the entire flash.
    /// <para>擦除整个 flash。</para>
    /// </summary>
    public int EraseFlash()
    {
        ThrowIfDisposed();
        var setupPacket = new UsbSetupPacket
        {
            RequestType = 0x40,
            Request = RkdlConstants.RkdlRequestEraseFlash,
            Value = 0x0000,
            Index = 0x0000,
            Length = 0
        };

        var result = _transport.ControlTransfer(setupPacket, null, 0, 0, _timeoutMs);
        return result;
    }

    /// <summary>
    /// Writes data to flash at the specified LBA address (in 512-byte sectors).
    /// <para>将数据写入指定 LBA 地址（512 字节扇区）的 flash。</para>
    /// </summary>
    public long WriteLBA(long address, Stream dataStream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(RkdlConstants.TransferChunkSize);
        try
        {
            long totalWritten = 0;
            while (totalWritten < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(RkdlConstants.TransferChunkSize, length - totalWritten);
                var n = dataStream.Read(buffer, 0, toRead);
                if (n == 0) throw new EndOfStreamException($"Stream ended at {totalWritten}/{length}");

                var written = _transport.Write(buffer, 0, n, _timeoutMs);
                if (written != n)
                    throw new RkdlException(RkdlError.WriteFailed, $"Short write: {written}/{n}");

                totalWritten += written;
                progress?.Report(totalWritten);
            }
            return totalWritten;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Reads data from flash at the specified LBA address.
    /// <para>从指定 LBA 地址读取 flash 数据。</para>
    /// </summary>
    public long ReadLBA(long address, Stream outputStream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(RkdlConstants.TransferChunkSize);
        try
        {
            long totalRead = 0;
            while (totalRead < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(RkdlConstants.TransferChunkSize, length - totalRead);
                var n = _transport.ReadInto(buffer, 0, toRead, _timeoutMs);
                if (n == 0) throw new RkdlException(RkdlError.ReadFailed, $"Device EOF at {totalRead}/{length}");

                outputStream.Write(buffer, 0, n);
                totalRead += n;
                progress?.Report(totalRead);
            }
            return totalRead;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RkdlSession));
    }

    /// <summary>
    /// Disposes the session and the underlying transport.
    /// <para>释放会话和底层传输。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}
