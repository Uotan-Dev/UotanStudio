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
    // session serializes same-direction transfers (read gate / write gate), and reads and
    // writes use separate buffers, so no additional locking is required.
    // <para>可复用的分块缓冲避免大传输时每次分块分配；会话串行化同向传输
    // （读/写门闩）且读/写缓冲分离，无需额外加锁。</para>
    private byte[]? _readChunkBuffer;
    private byte[]? _writeChunkBuffer;

    // P/Invoke to libusb_clear_halt (LibUsbDotNet's NativeMethods is internal).
    // On Windows winusb this maps to WinUsb_ResetPipe, aborting pending transfers
    // and resetting the pipe state after a failed read/write. <para>直接 P/Invoke
    // libusb_clear_halt；Windows winusb 上映射到 WinUsb_ResetPipe，在传输失败后
    // 中止未决传输并重置管道状态。</para>
    [LibraryImport("libusb-1.0")]
    private static partial int libusb_clear_halt(IntPtr devHandle, byte endpoint);

    private UsbContext? _context;
    private IUsbDevice? _usbDevice;
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

        // UsbContext.List() returns a UsbDeviceCollection that MUST be disposed: the
        // collection owns the native device handles, and leaving it undisposed lets the
        // devices' SafeHandle finalizers run against memory already freed by
        // libusb_exit, crashing with 0xC0000005 in UnrefDevice. Clone the matched
        // device (as the LibUsbDotNet docs recommend) so the session keeps a valid
        // handle after the collection is disposed.
        // <para>UsbContext.List() 返回的 UsbDeviceCollection 必须释放：集合持有原生设备
        // 句柄，不释放会导致设备 SafeHandle 终结器对 libusb_exit 已释放的内存执行 unref，
        // 在 UnrefDevice 处以 0xC0000005 崩溃。按 LibUsbDotNet 文档建议 Clone 匹配到的
        // 设备，使会话在集合释放后仍持有有效句柄。</para>
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
            // Open() failed (e.g. the interface is claimed by another session or process).
            // Never Close() a device that was never opened: LibUsbDotNet's Close() on an
            // unopened device corrupts the native refcount and the device's finalizer
            // later crashes with 0xC0000005 in UnrefDevice. Dispose() the clone (releases
            // the SafeHandle) and release only the _context.
            // <para>Open() 失败（例如接口已被其他会话或进程声明）。绝不对从未成功打开的设备
            // 调用 Close()：LibUsbDotNet 对未打开设备调用 Close() 会破坏原生引用计数，
            // 设备终结器随后在 UnrefDevice 处以 0xC0000005 崩溃。对克隆调用 Dispose()
            // （释放 SafeHandle），并仅释放上下文。</para>
            UsbTrace.LogErrorFormatted(
                $"LibUsbDevice.CreateHandle open failed: {ex.GetType().Name}: {ex.Message}");
            (_usbDevice as IDisposable)?.Dispose();
            _usbDevice = null;
            Dispose();
            return -1;
        }

        // On Windows winusb.sys, sending SET_CONFIGURATION (even with the current value)
        // re-enumerates interfaces and invalidates open handles; AOSP adb never calls
        // set_configuration because the OS has already configured the device. So only
        // set it when the device is not already in configuration 1.
        // <para>Windows winusb.sys 上发送 SET_CONFIGURATION（即使是当前值）会重新枚举
        // 接口并使已打开句柄失效；AOSP adb 从不调用 set_configuration。因此仅当设备
        // 尚未处于配置 1 时才设置。</para>
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

        // Auto-discover a bulk IN/OUT pair only when the finder did not already bind both.
        // IN-only (HID interrupt) and OUT-only devices must NOT be rejected here: the
        // session supports single-direction binding, and ReadInterrupt/WriteInterrupt
        // target explicit endpoints regardless of the bulk pair.
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
                        // Collect bulk endpoints first (the session I/O path), then interrupt
                        // endpoints as fallback candidates so devices with only interrupt pipes
                        // (e.g. HID) can still be opened when the filter requests explicit
                        // endpoint addresses (interrupt-test).
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

        // Only reject when the session would have NO endpoint at all; single-direction
        // sessions are valid (the missing direction's bulk helpers throw NotSupportedException).
        if (inEndpoint == 0 && outEndpoint == 0)
        {
            Dispose();
            return -1;
        }

        InterfaceId = targetInterfaceId;

        // usbhid (and other kernel drivers) bind HID interrupt devices immediately, and
        // udev may rebind right after a detach; retry detach+claim a few times instead of
        // failing on the first contention (mirrors libusb's auto-detach behaviour).
        Exception? claimError = null;
        var claimed = false;
        try
        {
            _usbDevice.ClaimInterface(targetInterfaceId);
            claimed = true;
        }
        catch (Exception ex)
        {
            claimError = ex;
        }

        if (!claimed && _usbDevice is LibUsbDotNet.LibUsb.UsbDevice libusbDevice)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    libusbDevice.DetachKernelDriver(targetInterfaceId);
                }
                catch (Exception detachEx)
                {
                    claimError = detachEx;
                }

                try
                {
                    _usbDevice.ClaimInterface(targetInterfaceId);
                    claimed = true;
                    break;
                }
                catch (Exception ex)
                {
                    claimError = ex;
                    Thread.Sleep(50);
                }
            }
        }

        if (!claimed)
        {
            UsbTrace.LogFormatted($"LibUsbDevice.ClaimInterface failed: {claimError?.GetType().Name}: {claimError?.Message}");
        }

        _reader = null;
        _writer = null;

        // Open each direction independently: interrupt-only HID devices (e.g. QEMU
        // usb-tablet) expose no OUT pipe, so a missing direction must not fail the
        // session — the session's ReadInterrupt/WriteInterrupt target explicit endpoints.
        if (inEndpoint != 0)
        {
            _reader = _usbDevice.OpenEndpointReader((ReadEndpointID)inEndpoint);
        }

        if (outEndpoint != 0)
        {
            _writer = _usbDevice.OpenEndpointWriter((WriteEndpointID)outEndpoint);
        }

        // Do NOT flush the IN endpoint at session open: protocol devices that greet
        // immediately (e.g. Qualcomm EDL's Sahara HELLO) would have their first
        // packet consumed here. The WinUSB backend has no flush, so libusb must match.
        // <para>会话打开时不要冲刷 IN 端点：对"打开即主动上报"的协议设备
        // （如 Qualcomm EDL 的 Sahara HELLO），此处会恰好吞掉首包。WinUSB 后端
        // 无冲刷，libusb 应保持一致。</para>

        // A session is usable as long as every direction it was asked to bind actually
        // opened. IN-only (HID interrupt) and OUT-only devices are valid; the missing
        // direction's bulk helpers throw NotSupportedException when invoked.
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
            // Clear any halted pipes before releasing the interface; on Windows winusb
            // a stalled pipe keeps the interface claimed at the driver level, so later
            // opens/enumeration fail until a replug.
            // <para>释放接口前清除停滞的管道；Windows winusb 上停滞的管道会使接口在驱动层
            // 保持占用，导致后续打开/枚举失败直到重新拔插。</para>
            RestoreEndpointHalt(ReadEndpointId);
            RestoreEndpointHalt(WriteEndpointId);

            // Release the claimed interface before closing the handle: winusb.sys allows
            // only one open handle per interface, so a leftover claim blocks later opens.
            // <para>关闭句柄前显式释放已 claim 的接口：winusb.sys 每接口仅允许一个句柄。</para>
            try
            {
                if (_usbDevice is LibUsbDotNet.LibUsb.UsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.ReleaseInterface(InterfaceId);
                }
            }
            catch (Exception ex)
            {
                UsbTrace.Log($"LibUsbDevice.ReleaseInterface ignored: {ex.GetType().Name}: {ex.Message}");
            }

            // Close() releases the device handle; Dispose() additionally releases the
            // underlying Device SafeHandle. Calling only Close() leaves the SafeHandle
            // to the finalizer, which then UnrefDevices memory already freed by
            // libusb_exit -> 0xC0000005.
            // <para>Close() 释放设备句柄；Dispose() 额外释放底层 Device SafeHandle。
            // 仅调用 Close() 会把 SafeHandle 留给终结器，终结器会对 libusb_exit 已释放的
            // 内存执行 UnrefDevice，导致 0xC0000005。</para>
            _usbDevice.Close();
            (_usbDevice as IDisposable)?.Dispose();
            _usbDevice = null;
        }

        _context?.Dispose();
        _context = null;
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
                // Info.SerialNumber reads the string descriptor; a device without a
                // serial or with a stalled control pipe throws UsbException. Expose the
                // reason to the global log instead of silently swallowing it.
                // <para>Info.SerialNumber 会读取字符串描述符；无序列号的设备或控制
                // 管道停滞的设备会抛出 UsbException。将原因写入全局日志而非静默吞掉。</para>
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
            // The device may already be disconnected; restoring the pipe is best-effort.
            UsbTrace.Log($"LibUsbDevice.RestoreEndpointHalt ignored: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
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

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (_writer == null) throw new NotSupportedException("The session has no bound OUT endpoint.");
        if (_writeChunkBuffer == null || _writeChunkBuffer.Length < length) _writeChunkBuffer = new byte[length];
        Marshal.Copy(buffer, _writeChunkBuffer, 0, length);
        var error = _writer.Write(_writeChunkBuffer, 0, length, ToLibusbTimeout(timeoutMs), out var transferred);
        return ClassifyBulkResult(error, transferred);
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
        // The write API exposes a count only; return partial progress on timeout,
        // but never silently classify STALL, overflow or disconnection as zero bytes.
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
        if (read ? _reader == null : _writer == null) throw new NotSupportedException("USB bulk endpoint is not bound.");
        cancellationToken.ThrowIfCancellationRequested();
        var data = new byte[length];
        if (!read) Marshal.Copy(buffer, data, 0, length);
        var nativeTimeout = ToLibusbTimeout(timeoutMs);
        var deadline = nativeTimeout == 0 ? long.MaxValue : Environment.TickCount64 + nativeTimeout;
        // LibUsbDotNet 3.x exposes no cancellation handle for ReadAsync/WriteAsync.
        // Use bounded native transfers and await their actual completion before
        // observing cancellation, so no transfer outlives the caller's buffer.
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

    public override async Task<int> ControlTransferAsync(Abstractions.UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = _usbDevice ?? throw new UsbDeviceHandleClosedException("Device handle is closed.");
        var data = BuildControlBuffer(setupPacket, buffer, offset, length);
        // A control request is submitted exactly once: even a zero-data timeout
        // does not prove that the device has not already performed the request.
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
