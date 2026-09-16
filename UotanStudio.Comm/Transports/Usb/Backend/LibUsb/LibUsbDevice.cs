using System.Runtime.InteropServices;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

internal partial class LibUsbDevice : UsbDevice
{
    private const int PlatformDefaultTimeoutMs = UsbTransferPolicies.DefaultTimeoutMs;

    // Reusable chunk buffers avoid a per-chunk allocation during large transfers. The
    // read/write gates cover both synchronous and asynchronous native completion.
    // <para>可复用的分块缓冲避免大传输时每次分块分配；读写缓冲与门闩分离，
    // 每个方向的门闩覆盖同步和异步原生传输完成，防止在飞缓冲被覆盖。</para>
    private byte[]? _readChunkBuffer;
    private byte[]? _writeChunkBuffer;
    private readonly SemaphoreSlim _readChunkGate = new(1, 1);
    private readonly SemaphoreSlim _writeChunkGate = new(1, 1);

    // P/Invoke to libusb_clear_halt (LibUsbDotNet's NativeMethods is internal).
    // On Windows winusb this maps to WinUsb_ResetPipe, aborting pending transfers
    // and resetting the pipe state after a failed read/write. <para>直接 P/Invoke
    // libusb_clear_halt；Windows winusb 上映射到 WinUsb_ResetPipe，在传输失败后
    // 中止未决传输并重置管道状态。</para>
    [LibraryImport("libusb-1.0")]
    private static partial int libusb_clear_halt(IntPtr devHandle, byte endpoint);

    private UsbContext? _context;
    private IUsbDevice? _usbDevice;
    private bool _interfaceClaimed;
    private UsbEndpointReader? _reader;
    private UsbEndpointWriter? _writer;
    // Vid/Pid delegate to the base UsbDevice.VendorId/ProductId so that both the
    // shorthand used by CreateHandle() and the projection in
    // UsbDeviceInfoFactory (which reads VendorId/ProductId) see the same values.
    // <para>Vid/Pid 委托到基类 UsbDevice.VendorId/ProductId，使 CreateHandle() 使用的简写
    // 与 UsbDeviceInfoFactory 投影读取的 VendorId/ProductId 保持同一份数据。</para>
    public ushort Vid { get => VendorId; set => VendorId = value; }
    public ushort Pid { get => ProductId; set => ProductId = value; }
    public byte BusNumber { get; set; }
    public byte DeviceAddress { get; set; }
    public byte InterfaceId { get; set; } = 0;
    public byte ReadEndpointId { get; set; }
    public byte WriteEndpointId { get; set; }

    public override byte EndpointIn => ReadEndpointId;
    public override byte EndpointOut => WriteEndpointId;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => _usbDevice != null;

    public override int DefaultTimeoutMs => PlatformDefaultTimeoutMs;

    private static string BuildDevicePath(LibUsbDotNet.LibUsb.UsbDevice device)
        => $"Bus {device.BusNumber} Device {device.Address}: {device.VendorId:X4}:{device.ProductId:X4}";

    private static bool HasBulkInterface(LibUsbDotNet.LibUsb.UsbDevice device)
    {
        try
        {
            foreach (var config in device.Configs)
            {
                foreach (var ifc in config.Interfaces)
                {
                    var hasIn = false;
                    var hasOut = false;
                    foreach (var endpoint in ifc.Endpoints)
                    {
                        if ((endpoint.Attributes & 0x03) != 0x02)
                        {
                            continue;
                        }

                        if ((endpoint.EndpointAddress & 0x80) != 0)
                        {
                            hasIn = true;
                        }
                        else
                        {
                            hasOut = true;
                        }
                    }

                    if (hasIn && hasOut)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            UsbTrace.Log("LibUsbDevice.HasBulkInterface: failed to enumerate interface endpoints.");
            return false;
        }

        return false;
    }

    public override int CreateHandle()
    {
        _context = new UsbContext();

        LibUsbDotNet.LibUsb.UsbDevice? device;
        using (var candidates = _context.List())
        {
            var candidateList = candidates.OfType<LibUsbDotNet.LibUsb.UsbDevice>().ToList();

            device = null;
            if (BusNumber != 0 || DeviceAddress != 0)
            {
                device = candidateList.FirstOrDefault(d => d.BusNumber == BusNumber && d.Address == DeviceAddress);
            }

            if (device == null && !string.IsNullOrWhiteSpace(DevicePath))
            {
                device = candidateList.FirstOrDefault(d =>
                    string.Equals(BuildDevicePath(d), DevicePath, StringComparison.OrdinalIgnoreCase));
            }

            device ??= candidateList.FirstOrDefault(d =>
                    d.VendorId == Vid &&
                    d.ProductId == Pid &&
                    HasBulkInterface(d));

            if (device != null)
            {
                _usbDevice = device.Clone();
            }
        }

        if (device == null || _usbDevice == null)
        {
            _context.Dispose();
            _context = null;
            return -1;
        }

        try
        {
            _usbDevice.Open();
        }
        catch (Exception ex)
        {
            UsbTrace.LogErrorFormatted(
                $"LibUsbDevice.CreateHandle open failed: {ex.GetType().Name}: {ex.Message}");
            (_usbDevice as IDisposable)?.Dispose();
            _usbDevice = null;
            Dispose();
            return -1;
        }

        try
        {
            if (_usbDevice is LibUsbDotNet.LibUsb.UsbDevice wholeUsbDevice && wholeUsbDevice.Configuration != 1)
            {
                _usbDevice.SetConfiguration(1);
            }
        }
        catch (Exception ex)
        {
            UsbTrace.LogFormatted($"LibUsbDevice.SetConfiguration ignored: {ex.GetType().Name}: {ex.Message}");
        }

        var targetInterfaceId = InterfaceId;
        var inEndpoint = ReadEndpointId;
        var outEndpoint = WriteEndpointId;

        if (inEndpoint == 0 || outEndpoint == 0)
        {
            foreach (var config in _usbDevice.Configs)
            {
                foreach (var ifc in config.Interfaces)
                {
                    byte candidateIn = 0;
                    byte candidateOut = 0;
                    foreach (var endpoint in ifc.Endpoints)
                    {
                        var epType = endpoint.Attributes & 0x03;
                        if (epType is not 0x02 and not 0x03)
                        {
                            continue;
                        }

                        if ((endpoint.EndpointAddress & 0x80) != 0)
                        {
                            if (candidateIn == 0)
                            {
                                candidateIn = endpoint.EndpointAddress;
                            }
                        }
                        else
                        {
                            if (candidateOut == 0)
                            {
                                candidateOut = endpoint.EndpointAddress;
                            }
                        }
                    }

                    if (candidateIn != 0)
                    {
                        targetInterfaceId = (byte)ifc.Number;
                        if (inEndpoint == 0)
                        {
                            inEndpoint = candidateIn;
                        }

                        if (outEndpoint == 0)
                        {
                            outEndpoint = candidateOut;
                        }

                        break;
                    }
                }

                if (inEndpoint != 0 && outEndpoint != 0)
                {
                    break;
                }
            }
        }

        if (inEndpoint == 0 && outEndpoint == 0)
        {
            Dispose();
            return -1;
        }

        InterfaceId = targetInterfaceId;

        var claimed = LibUsbInterfaceClaim.TryClaim(
            () => _usbDevice.ClaimInterface(targetInterfaceId),
            _usbDevice is LibUsbDotNet.LibUsb.UsbDevice libusbDevice
                ? () => libusbDevice.DetachKernelDriver(targetInterfaceId)
                : null,
            out var claimError);
        if (!claimed)
        {
            UsbTrace.LogErrorFormatted($"LibUsbDevice.ClaimInterface {targetInterfaceId} failed: {claimError?.Message}");
            Dispose();
            return -1;
        }

        _interfaceClaimed = true;
        ReadEndpointId = inEndpoint;
        WriteEndpointId = outEndpoint;

        _reader = null;
        _writer = null;

        if (inEndpoint != 0)
        {
            _reader = _usbDevice.OpenEndpointReader((ReadEndpointID)inEndpoint);
        }

        if (outEndpoint != 0)
        {
            _writer = _usbDevice.OpenEndpointWriter((WriteEndpointID)outEndpoint);
        }

        if ((inEndpoint != 0 && _reader == null) || (outEndpoint != 0 && _writer == null))
        {
            Dispose();
            return -1;
        }

        GetSerialNumber();
        return 0;
    }

    public override void Dispose()
    {
        if (_usbDevice != null)
        {
            if (_interfaceClaimed)
            {
                RestoreEndpointHalt(ReadEndpointId);
                RestoreEndpointHalt(WriteEndpointId);
            }

            try
            {
                if (_interfaceClaimed && _usbDevice is LibUsbDotNet.LibUsb.UsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.ReleaseInterface(InterfaceId);
                }
            }
            catch (Exception ex)
            {
                UsbTrace.Log($"LibUsbDevice.ReleaseInterface ignored: {ex.GetType().Name}: {ex.Message}");
            }

            _usbDevice.Close();
            (_usbDevice as IDisposable)?.Dispose();
            _usbDevice = null;
        }

        _interfaceClaimed = false;
        _reader = null;
        _writer = null;
        _context?.Dispose();
        _context = null;
        _readChunkBuffer = null;
        _writeChunkBuffer = null;
    }

    public override int GetSerialNumber()
    {
        if (_usbDevice != null)
        {
            try
            {
                SerialNumber = _usbDevice.Info.SerialNumber;
                return 0;
            }
            catch (Exception ex)
            {
                UsbTrace.LogErrorFormatted(
                    $"LibUsbDevice.GetSerialNumber failed: {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }
        return -1;
    }

    public override void Reset()
    {
        if (_usbDevice != null)
        {
            try
            {
                _usbDevice.ResetDevice();
            }
            catch (Exception ex)
            {
                UsbTrace.LogFormatted($"LibUsbDevice.Reset failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public override int ControlTransfer(Abstractions.UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
        => ControlTransferAsync(setupPacket, buffer, offset, length, timeoutMs).GetAwaiter().GetResult();

    public override byte[] Read(int length) => Read(length, PlatformDefaultTimeoutMs);

    public override byte[] Read(int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return [];
        }

        var buffer = new byte[length];
        var count = ReadInto(buffer, 0, length, timeoutMs);
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

    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, PlatformDefaultTimeoutMs);

    protected override string BackendName => "libusb";

    // A session is open when at least one direction bound successfully; IN-only (HID
    // interrupt) and OUT-only devices are valid sessions even though bulk I/O in the
    // missing direction throws NotSupportedException.
    protected override bool IsOpen => _reader != null || _writer != null;

    protected override bool IsDisconnectionError(int nativeError)
        => nativeError == (int)Error.NoDevice;

    /// <summary>
    /// Maps the library timeout sentinel to libusb's convention: libusb treats 0 as
    /// "never Time out", so <see cref="UsbTransferPolicies.InfiniteTimeoutMs"/> (-1) becomes 0.
    /// <para>将库的超时哨兵映射为 libusb 约定：libusb 将 0 视为"永不超时"，
    /// 因此 <see cref="UsbTransferPolicies.InfiniteTimeoutMs"/>（-1）转换为 0。</para>
    /// </summary>
    private static int ToLibusbTimeout(int timeoutMs)
        => timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs ? 0 : timeoutMs;

    /// <summary>
    /// Best-effort restore of a stalled endpoint pipe after a failed transfer.
    /// A synchronous transfer error (timeout, stall, pipe error) can leave the
    /// winusb pipe in a halted state; clearing the halt aborts any pending transfer
    /// and resets the pipe so the driver releases the interface cleanly on close.
    /// Without this, a failed session read leaves the interface claimed at the
    /// driver level and the device disappears from later enumeration until replug.
    /// <para>传输失败后尽力恢复停滞的端点管道。同步传输错误（超时、stall、管道错误）
    /// 可能使 winusb 管道处于 halted 状态；清除 halt 会中止任何未决传输并重置管道，
    /// 使驱动在关闭时干净地释放接口。否则失败的会话读取会让接口在驱动层保持占用，
    /// 设备会从后续枚举中消失，直到重新拔插。</para>
    /// </summary>
    /// <param name="endpoint">The endpoint address to clear; 0 is skipped.
    /// <para>要清除的端点地址；为 0 时跳过。</para></param>
    private void RestoreEndpointHalt(byte endpoint)
    {
        if (endpoint == 0)
        {
            return;
        }

        try
        {
            if (_usbDevice is LibUsbDotNet.LibUsb.UsbDevice libusbDevice &&
                libusbDevice.DeviceHandle is { IsInvalid: false } deviceHandle)
            {
                var result = libusb_clear_halt(deviceHandle.DangerousGetHandle(), endpoint);
                if (result != 0)
                {
                    UsbTrace.Log($"LibUsbDevice.RestoreEndpointHalt endpoint 0x{endpoint:X2} returned 0x{result:X}.");
                }
            }
        }
        catch (Exception ex)
        {
            UsbTrace.Log($"LibUsbDevice.RestoreEndpointHalt ignored: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        _readChunkGate.Wait();
        try
        {
            if (_reader == null)
            {
                throw new NotSupportedException("The session has no bound IN endpoint for bulk reads (interrupt-only device?).");
            }

            if (_readChunkBuffer == null || _readChunkBuffer.Length < length)
            {
                _readChunkBuffer = new byte[length];
            }

            var chunkBuffer = _readChunkBuffer;
            var error = _reader.Read(chunkBuffer, 0, length, ToLibusbTimeout(timeoutMs), out var readLen);
            var result = ClassifyBulkResult(error, readLen);
            if (readLen > 0) Marshal.Copy(chunkBuffer, 0, buffer, readLen);
            return result;
        }
        finally
        {
            _readChunkGate.Release();
        }
    }

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        _writeChunkGate.Wait();
        try
        {
            if (_writer == null) throw new NotSupportedException("The session has no bound OUT endpoint.");
            if (_writeChunkBuffer == null || _writeChunkBuffer.Length < length) _writeChunkBuffer = new byte[length];
            Marshal.Copy(buffer, _writeChunkBuffer, 0, length);
            var error = _writer.Write(_writeChunkBuffer, 0, length, ToLibusbTimeout(timeoutMs), out var transferred);
            return ClassifyBulkResult(error, transferred);
        }
        finally
        {
            _writeChunkGate.Release();
        }
    }

    private static UsbChunkResult ClassifyBulkResult(Error error, int transferred)
    {
        if (error == Error.NoDevice) return UsbChunkResult.Fatal((int)error);
        if (error is not (Error.Success or Error.Timeout))
            throw new UsbTransferException($"libusb bulk transfer failed: {error}.")
            { Backend = "libusb", NativeErrorCode = (int)error };
        if (transferred > 0 || error == Error.Success) return UsbChunkResult.Success(transferred);
        return UsbChunkResult.Timeout((int)error);
    }

    public override long Write(byte[] data, int length) => Write(data, length, PlatformDefaultTimeoutMs);

    public override void WriteZlp(int timeoutMs)
    {
        if (_writer == null)
        {
            throw new NotSupportedException("The session has no bound OUT endpoint for bulk writes (interrupt-only device?).");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var zero = Array.Empty<byte>();
        var errorCode = _writer.Write(zero, 0, 0, ToLibusbTimeout(effectiveTimeoutMs), out _);
        if (errorCode == Error.NoDevice)
        {
            throw new UsbDeviceDisconnectedException("USB write failed: device disconnected (Error.NoDevice).", (int)errorCode);
        }
        if (errorCode != 0)
        {
            throw new IOException($"USB zero-length write failed with libusb error: {errorCode}");
        }
    }

    public override UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs)
        => ReadInterruptAsync(endpointAddress, buffer, offset, length, timeoutMs).GetAwaiter().GetResult();

    public override long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs)
        => WriteInterruptAsync(endpointAddress, data, offset, length, timeoutMs).GetAwaiter().GetResult();

    public override async Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ValidateBufferRange(buffer, offset, length);
        cancellationToken.ThrowIfCancellationRequested();
        var device = RequireInterruptEndpoint(endpointAddress, read: true);
        if (length == 0) return new(0, false, false);
        var data = new byte[length];
        var result = await LibUsbTransfer.ExecuteAsync(device.DeviceHandle, endpointAddress, TransferType.Interrupt,
            data, timeoutMs, cancellationToken).ConfigureAwait(false);
        var read = LibUsbTransfer.ReadResult(result.Error, result.Count, length);
        data.AsSpan(0, read.Count).CopyTo(buffer.AsSpan(offset, read.Count));
        return read;
    }

    public override async Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ValidateWriteData(data, offset, length);
        cancellationToken.ThrowIfCancellationRequested();
        var device = RequireInterruptEndpoint(endpointAddress, read: false);
        if (length == 0) return 0;
        var result = await LibUsbTransfer.ExecuteAsync(device.DeviceHandle, endpointAddress, TransferType.Interrupt,
            data.AsSpan(offset, length).ToArray(), timeoutMs, cancellationToken).ConfigureAwait(false);
        return LibUsbTransfer.ReadResult(result.Error, result.Count, length).Count;
    }

    private IUsbDevice RequireInterruptEndpoint(byte endpoint, bool read)
    {
        var device = _usbDevice ?? throw new UsbDeviceHandleClosedException("Device handle is closed.");
        if ((endpoint & 0x0f) == 0 || (endpoint & 0x70) != 0 || ((endpoint & 0x80) != 0) != read)
            throw new ArgumentException("Invalid interrupt endpoint address or direction.", nameof(endpoint));
        if (!device.Configs.SelectMany(c => c.Interfaces).Where(i => i.Number == InterfaceId)
            .SelectMany(i => i.Endpoints).Any(e => e.EndpointAddress == endpoint && (e.Attributes & 3) == 3))
            throw new NotSupportedException($"Endpoint 0x{endpoint:X2} is not an interrupt endpoint of the claimed interface.");
        return device;
    }

    protected override Task<UsbChunkResult> ReadChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
        => TransferBulkAsync(buffer, length, timeoutMs, read: true, cancellationToken);

    protected override Task<UsbChunkResult> WriteChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
        => TransferBulkAsync(buffer, length, timeoutMs, read: false, cancellationToken);

    private async Task<UsbChunkResult> TransferBulkAsync(IntPtr buffer, int length, int timeoutMs, bool read, CancellationToken cancellationToken)
    {
        var gate = read ? _readChunkGate : _writeChunkGate;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (read ? _reader == null : _writer == null) throw new NotSupportedException("USB bulk endpoint is not bound.");
            cancellationToken.ThrowIfCancellationRequested();
            var data = read
                ? (_readChunkBuffer is { } input && input.Length >= length ? input : _readChunkBuffer = new byte[length])
                : (_writeChunkBuffer is { } output && output.Length >= length ? output : _writeChunkBuffer = new byte[length]);
            if (!read) Marshal.Copy(buffer, data, 0, length);
            var nativeTimeout = ToLibusbTimeout(timeoutMs);
            var deadline = nativeTimeout == 0 ? long.MaxValue : Environment.TickCount64 + nativeTimeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return UsbChunkResult.Timeout((int)Error.Timeout);
                var slice = cancellationToken.CanBeCanceled ? (int)Math.Min(100, remaining) : nativeTimeout;
                var (error, transferred) = read
                    ? await _reader!.ReadAsync(data, 0, length, slice).ConfigureAwait(false)
                    : await _writer!.WriteAsync(data, 0, length, slice).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (error == Error.Timeout && transferred == 0 && cancellationToken.CanBeCanceled) continue;
                var result = ClassifyBulkResult(error, transferred);
                if (read && transferred > 0) Marshal.Copy(data, 0, buffer, transferred);
                return result;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public override async Task<int> ControlTransferAsync(Abstractions.UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = _usbDevice ?? throw new UsbDeviceHandleClosedException("Device handle is closed.");
        var data = BuildControlBuffer(setupPacket, buffer, offset, length);
        var result = await LibUsbTransfer.ExecuteAsync(device.DeviceHandle, 0, TransferType.Control,
            data, timeoutMs, cancellationToken).ConfigureAwait(false);
        LibUsbTransfer.ThrowOnError(result.Error);
        if ((uint)result.Count > (uint)length) throw new InvalidDataException("libusb returned an invalid control transfer length.");
        if ((setupPacket.RequestType & 0x80) != 0 && result.Count > 0)
            data.AsSpan(8, result.Count).CopyTo(buffer!.AsSpan(offset, result.Count));
        return result.Count;
    }

    internal static byte[] BuildControlBuffer(Abstractions.UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length)
    {
        if (buffer is null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(length, 0);
            ArgumentOutOfRangeException.ThrowIfNotEqual(offset, 0);
        }
        else ValidateBufferRange(buffer, offset, length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, ushort.MaxValue);
        if (setupPacket.Length != length)
            throw new ArgumentException("Control setup wLength must match the supplied data length.", nameof(setupPacket));
        var data = new byte[8 + length];
        data[0] = setupPacket.RequestType; data[1] = setupPacket.Request;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), setupPacket.Value);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), setupPacket.Index);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), setupPacket.Length);
        if ((setupPacket.RequestType & 0x80) == 0 && length > 0)
            buffer!.AsSpan(offset, length).CopyTo(data.AsSpan(8));
        return data;
    }
}