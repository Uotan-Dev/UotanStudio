using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// macOS USB device backed by Apple's official IOUSBHost user-space framework,
/// driven from C# through the ObjC runtime (no native bridge library). Opens the
/// device user client via <c>initWithIOService:</c> (which IS the exclusive open),
/// claims the target interface through an <c>IOUSBHostInterface</c> user client, and
/// performs bulk I/O through <c>IOUSBHostPipe.sendIORequestWithData:</c>.
/// <para>基于苹果官方 IOUSBHost 用户空间框架的 macOS USB 设备，经 ObjC 运行时从 C#
/// 直接驱动（无原生桥接库）。通过 <c>initWithIOService:</c> 创建设备用户客户端
/// （该调用即独占打开），经 <c>IOUSBHostInterface</c> 用户客户端声明目标接口，
/// 并通过 <c>IOUSBHostPipe.sendIORequestWithData:</c> 执行批量 I/O。</para>
/// </summary>
/// <remarks>
/// Error codes are IOReturn values reported through NSError.code. The device-level
/// <c>resetWithError:</c> re-enumerates the device (the session must be re-opened
/// afterwards) — this matches libusb's darwin backend semantics.
/// <para>错误码为经 NSError.code 上报的 IOReturn 值。设备级 <c>resetWithError:</c>
/// 会使设备重新枚举（之后必须重开会话）——与 libusb darwin 后端语义一致。</para>
/// </remarks>
internal class UsbHostDevice : UsbDevice
{
    /// <summary>
    /// The stable IORegistry entry ID, used as a display/identity hint.
    /// <para>稳定的 IORegistry 项 ID，仅作展示/标识用途。</para>
    /// </summary>
    public ulong RegistryEntryId { get; set; }

    private const int PlatformDefaultTimeoutMs = UsbTransferPolicies.DefaultTimeoutMs;

    private IntPtr _device = IntPtr.Zero;
    private IntPtr _interface = IntPtr.Zero;
    private IntPtr _pipeIn = IntPtr.Zero;
    private IntPtr _pipeOut = IntPtr.Zero;

    private byte _bulkIn;
    private byte _bulkOut;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => _device != IntPtr.Zero;

    /// <inheritdoc/>
    protected override string BackendName => "macos-usbhost";

    /// <inheritdoc/>
    protected override bool IsOpen => _interface != IntPtr.Zero;

    /// <inheritdoc/>
    public override byte EndpointIn => _bulkIn;

    /// <inheritdoc/>
    public override byte EndpointOut => _bulkOut;

    /// <inheritdoc/>
    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (_pipeIn == IntPtr.Zero)
        {
            return UsbChunkResult.Fatal(IOKitUsbAPI.KIOReturnNoDevice);
        }

        if (Volatile.Read(ref _disposing) != 0)
        {
            return UsbChunkResult.Fatal(IOKitUsbAPI.KIOReturnError);
        }

        Interlocked.Increment(ref _inFlightTransfers);
        try
        {
            var kr = UsbHostAPI.SendIoRequestRaw(_pipeIn, buffer, length, isWrite: false, timeoutMs, out var transferred);
            return ChunkResult(kr, transferred);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightTransfers);
        }
    }

    /// <inheritdoc/>
    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (_pipeOut == IntPtr.Zero)
        {
            return UsbChunkResult.Fatal(IOKitUsbAPI.KIOReturnNoDevice);
        }

        if (Volatile.Read(ref _disposing) != 0)
        {
            return UsbChunkResult.Fatal(IOKitUsbAPI.KIOReturnError);
        }

        Interlocked.Increment(ref _inFlightTransfers);
        try
        {
            var kr = UsbHostAPI.SendIoRequestRaw(_pipeOut, buffer, length, isWrite: true, timeoutMs, out var transferred);
            return ChunkResult(kr, length);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightTransfers);
        }
    }

    private static UsbChunkResult ChunkResult(int kr, int transferred)
        => kr != IOKitUsbAPI.Ok
            ? IsDisconnection(kr) ? UsbChunkResult.Fatal(kr)
                : kr == IOKitUsbAPI.KIOReturnTimeout ? UsbChunkResult.Timeout(kr)
                : UsbChunkResult.Error(kr)
            : UsbChunkResult.Success(transferred);

    /// <inheritdoc/>
    protected override bool IsDisconnectionError(int nativeError) => IsDisconnection(nativeError);

    private static bool IsDisconnection(int nativeError)
        => nativeError is IOKitUsbAPI.KIOReturnNoDevice
           or IOKitUsbAPI.KIOReturnNotResponding
           or IOKitUsbAPI.KIOReturnAborted;

    /// <inheritdoc/>
    public override byte[] Read(int length) => Read(length, PlatformDefaultTimeoutMs);

    /// <inheritdoc/>
    public override byte[] Read(int length, int timeoutMs)
    {
        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        var count = ReadInto(buffer, 0, length, timeoutMs);
        if (count <= 0)
        {
            return [];
        }

        if (count == length)
        {
            return buffer;
        }

        var result = new byte[count];
        Array.Copy(buffer, result, count);
        return result;
    }

    /// <inheritdoc/>
    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, PlatformDefaultTimeoutMs);

    /// <inheritdoc/>
    public override int ReadInto(byte[] buffer, int offset, int length, int timeoutMs)
    {
        if (_pipeIn == IntPtr.Zero)
        {
            return 0;
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
            var kr = UsbHostAPI.SendIoRequestRaw(_pipeIn, ptr, length, isWrite: false, effectiveTimeoutMs, out var transferred);
            return kr != IOKitUsbAPI.Ok
                ? IsDisconnection(kr)
                    ? throw new UsbDeviceDisconnectedException($"USB device disconnected during read (error: 0x{kr:X}).", kr)
                    : kr == IOKitUsbAPI.KIOReturnTimeout ? 0 : throw new IOException($"USB read failed with error: 0x{kr:X}")
                : transferred;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <inheritdoc/>
    public override long Write(byte[] data, int length) => Write(data, length, PlatformDefaultTimeoutMs);

    /// <inheritdoc/>
    public override long Write(byte[] data, int length, int timeoutMs)
    {
        if (_pipeOut == IntPtr.Zero)
        {
            return -1;
        }

        if (length == 0)
        {
            WriteZlp(timeoutMs);
            return 0;
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var lenRemaining = length;
            var count = 0;
            while (lenRemaining > 0)
            {
                var lenToSend = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + count);
                var kr = UsbHostAPI.SendIoRequestRaw(_pipeOut, ptr, lenToSend, isWrite: true, effectiveTimeoutMs, out var transferred);
                if (kr != IOKitUsbAPI.Ok)
                {
                    if (IsDisconnection(kr))
                    {
                        throw new UsbDeviceDisconnectedException($"USB device disconnected during write (error: 0x{kr:X}).", kr);
                    }

                    throw new IOException($"USB write failed with error: 0x{kr:X}");
                }

                lenRemaining -= transferred;
                count += transferred;
            }

            return count > 0 ? count : -1;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <inheritdoc/>
    public override void WriteZlp(int timeoutMs)
    {
        if (_pipeOut == IntPtr.Zero)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var kr = UsbHostAPI.SendIoRequestZero(_pipeOut, effectiveTimeoutMs);
        if (kr != IOKitUsbAPI.Ok)
        {
            if (IsDisconnection(kr))
            {
                throw new UsbDeviceDisconnectedException($"USB device disconnected during zero-length write (error: 0x{kr:X}).", kr);
            }

            throw new IOException($"USB zero-length write failed with error: 0x{kr:X}");
        }
    }

    /// <inheritdoc/>
    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_device == IntPtr.Zero)
        {
            return 0;
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var request = new ObjcRuntime.UsbHostDeviceRequest(
            setupPacket.RequestType, setupPacket.Request, setupPacket.Value, setupPacket.Index, (ushort)length);

        byte[]? staged = null;
        try
        {
            if (buffer is { Length: > 0 } && length > 0)
            {
                staged = new byte[length];
                Array.Copy(buffer, offset, staged, 0, length);
            }

            var kr = UsbHostAPI.SendDeviceRequest(_device, request, staged, effectiveTimeoutMs, out var transferred);
            if (kr != IOKitUsbAPI.Ok)
            {
                return IsDisconnection(kr)
                    ? throw new UsbDeviceDisconnectedException($"USB device disconnected during control transfer (error: 0x{kr:X}).", kr)
                    : 0;
            }

            if (transferred > 0 && staged != null && (setupPacket.RequestType & 0x80) != 0)
            {
                Array.Copy(staged, 0, buffer!, offset, transferred);
            }

            return transferred;
        }
        finally
        {
            _ = staged;
        }
    }

    /// <inheritdoc/>
    public override int CreateHandle()
    {
        if (_device != IntPtr.Zero)
        {
            return 0;
        }

        if (string.IsNullOrEmpty(DevicePath))
        {
            return -1;
        }

        if (!UsbHostAPI.IsAvailable)
        {
            UsbTrace.Log("UsbHostDevice.CreateHandle: IOUSBHost classes unavailable.");
            return -1;
        }

        UsbTrace.Log($"UsbHostDevice.CreateHandle: DevicePath='{DevicePath}'");
        var service = IOKitUsbAPI.IORegistryEntryFromPath(IntPtr.Zero, DevicePath);
        if (service == IntPtr.Zero)
        {
            UsbTrace.Log("UsbHostDevice.CreateHandle: IORegistryEntryFromPath failed.");
            return -1;
        }

        try
        {
            var device = UsbHostAPI.CreateObject(UsbHostAPI.GetDeviceClass(), (uint)service.ToInt32(), out var kr);
            UsbTrace.Log($"UsbHostDevice.CreateHandle: device user client kr=0x{kr:X} device={device}");
            if (device == IntPtr.Zero)
            {
                return -1;
            }

            _device = device;

            var children = UsbHostAPI.GetInterfaceServices((uint)service.ToInt32());
            UsbTrace.Log($"UsbHostDevice.CreateHandle: interface children={children.Count}");
            foreach (var child in children)
            {
                try
                {
                    if (TryOpenInterface(child))
                    {
                        UsbTrace.Log($"UsbHostDevice.CreateHandle: interface claimed, bulkIn=0x{_bulkIn:X2} bulkOut=0x{_bulkOut:X2}");
                        return 0;
                    }

                    UsbTrace.Log($"UsbHostDevice.CreateHandle: interface child {child:X} not claimable");
                }
                finally
                {
                    IOKitUsbAPI.IOObjectRelease((IntPtr)child);
                }
            }

            UsbTrace.Log("UsbHostDevice.CreateHandle: no interface claimed.");

            UsbHostAPI.Destroy(_device);
            _device = IntPtr.Zero;
            return -1;
        }
        finally
        {
            IOKitUsbAPI.IOObjectRelease(service);
        }
    }

    private bool TryOpenInterface(uint ifcService)
    {
        var usbInterface = UsbHostAPI.CreateObject(UsbHostAPI.GetInterfaceClass(), ifcService, out _);
        if (usbInterface == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var descriptor = UsbHostAPI.GetInterfaceDescriptor(usbInterface);
            byte interfaceNumber = 0;
            byte numEndpoints = 0;
            if (descriptor != IntPtr.Zero)
            {
                interfaceNumber = Marshal.ReadByte(descriptor, 2);
                numEndpoints = Marshal.ReadByte(descriptor, 4);
            }
            UsbTrace.Log($"TryOpenInterface: ifc#{interfaceNumber} endpoints={numEndpoints} desc={descriptor}");

            var config = UsbHostAPI.GetConfigurationDescriptor(usbInterface);
            UsbTrace.Log($"TryOpenInterface: configDescriptor={config}");
            var endpoints = CollectEndpoints(usbInterface, interfaceNumber, numEndpoints);
            UsbTrace.Log($"TryOpenInterface: collected {endpoints.Count} endpoints");
            byte bulkIn = 0;
            byte bulkOut = 0;
            byte interruptIn = 0;
            byte interruptOut = 0;
            foreach (var ep in endpoints)
            {
                var isIn = (ep.Address & 0x80) != 0;
                if (isIn && ep.Type == 0x02 && bulkIn == 0)
                {
                    bulkIn = ep.Address;
                }
                else if (!isIn && ep.Type == 0x02 && bulkOut == 0)
                {
                    bulkOut = ep.Address;
                }
                else if (isIn && ep.Type == 0x03 && interruptIn == 0)
                {
                    interruptIn = ep.Address;
                }
                else if (!isIn && ep.Type == 0x03 && interruptOut == 0)
                {
                    interruptOut = ep.Address;
                }
            }

            var pipeIn = bulkIn != 0 ? bulkIn : interruptIn;
            var pipeOut = bulkOut != 0 ? bulkOut : interruptOut;
            if (pipeIn == 0)
            {
                return false;
            }

            var inPipe = UsbHostAPI.CopyPipe(usbInterface, pipeIn);
            if (inPipe == IntPtr.Zero)
            {
                return false;
            }

            IntPtr outPipe = IntPtr.Zero;
            if (pipeOut != 0)
            {
                outPipe = UsbHostAPI.CopyPipe(usbInterface, pipeOut);
                if (outPipe == IntPtr.Zero)
                {
                    UsbHostAPI.ReleasePipe(inPipe);
                    return false;
                }
            }

            _bulkIn = pipeIn;
            _bulkOut = pipeOut;
            _pipeIn = inPipe;
            _pipeOut = outPipe;
            _interface = usbInterface;
            usbInterface = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (usbInterface != IntPtr.Zero)
            {
                UsbHostAPI.Destroy(usbInterface);
            }
        }
    }

    private static List<(byte Address, byte Type)> CollectEndpoints(IntPtr usbInterface, byte interfaceNumber, byte numEndpoints)
    {
        var endpoints = new List<(byte Address, byte Type)>();

        var config = UsbHostAPI.GetConfigurationDescriptor(usbInterface);
        if (config != IntPtr.Zero)
        {
            var total = (int)Marshal.ReadInt16(config, 2);
            if (total is > 0 and <= 4096)
            {
                var pos = 0;
                var inTargetInterface = false;
                while (pos + 2 <= total)
                {
                    var len = Marshal.ReadByte(config, pos);
                    var type = Marshal.ReadByte(config, pos + 1);
                    if (len < 2 || pos + len > total)
                    {
                        break;
                    }

                    if (type == UsbHostAPI.DescriptorTypeInterface)
                    {
                        inTargetInterface = len >= 9 && Marshal.ReadByte(config, pos + 2) == interfaceNumber;
                    }
                    else if (inTargetInterface && type == UsbHostAPI.DescriptorTypeEndpoint && len >= 7)
                    {
                        endpoints.Add((Marshal.ReadByte(config, pos + 2), Marshal.ReadByte(config, pos + 3)));
                        if (endpoints.Count >= numEndpoints && numEndpoints > 0)
                        {
                            break;
                        }
                    }

                    pos += len;
                }

                if (endpoints.Count > 0)
                {
                    return endpoints;
                }
            }
        }

        for (byte address = 1; address <= 0x0F; address++)
        {
            foreach (var probe in new[] { address, (byte)(address | 0x80) })
            {
                var pipe = UsbHostAPI.CopyPipe(usbInterface, probe);
                if (pipe == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var reported = UsbHostAPI.GetPipeEndpointAddress(pipe);
                    endpoints.Add(((byte)reported, 0x02));
                }
                finally
                {
                    UsbHostAPI.ReleasePipe(pipe);
                }
            }
        }

        return endpoints;
    }

    /// <inheritdoc/>
    public override void Reset()
    {
        if (_device == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = UsbHostAPI.ResetDevice(_device);
        }
        catch
        {
        }
    }

    /// <inheritdoc/>
    public override int GetSerialNumber()
    {
        if (_device == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            var dd = new byte[18];
            var done = ControlTransferRaw(0x80, 0x06, 0x0100, 0x0000, dd, dd.Length);
            if (done < 18)
            {
                return -1;
            }

            Speed = UsbSpeedInference.FromBcdUsb((ushort)(dd[2] | (dd[3] << 8)));

            var serialIndex = dd[16];
            if (serialIndex == 0)
            {
                return -1;
            }

            var buf = new byte[256];
            done = ControlTransferRaw(0x80, 0x06, (ushort)((0x03 << 8) | serialIndex), 0x0409, buf, buf.Length);
            if (done <= 2)
            {
                return -1;
            }

            SerialNumber = UsbStringDescriptor.Decode(buf, done);
            return 0;
        }
        catch
        {
            return -1;
        }
    }

    private int ControlTransferRaw(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] buffer, int length)
    {
        if (_device == IntPtr.Zero)
        {
            return -1;
        }

        var request = new ObjcRuntime.UsbHostDeviceRequest(bmRequestType, bRequest, wValue, wIndex, (ushort)length);
        var kr = UsbHostAPI.SendDeviceRequest(_device, request, buffer, PlatformDefaultTimeoutMs, out var transferred);
        return kr == IOKitUsbAPI.Ok ? transferred : -1;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        Volatile.Write(ref _disposing, 1);
        UsbHostAPI.AbortPipe(_pipeIn);
        UsbHostAPI.AbortPipe(_pipeOut);
        WaitForInFlightTransfers();

        if (_pipeIn != IntPtr.Zero)
        {
            UsbHostAPI.ReleasePipe(_pipeIn);
            _pipeIn = IntPtr.Zero;
        }

        if (_pipeOut != IntPtr.Zero)
        {
            UsbHostAPI.ReleasePipe(_pipeOut);
            _pipeOut = IntPtr.Zero;
        }

        if (_interface != IntPtr.Zero)
        {
            UsbHostAPI.Destroy(_interface);
            _interface = IntPtr.Zero;
        }

        if (_device != IntPtr.Zero)
        {
            UsbHostAPI.Destroy(_device);
            _device = IntPtr.Zero;
        }
    }

    private int _inFlightTransfers;
    private int _disposing;

    private void WaitForInFlightTransfers()
    {
        var spins = 0;
        while (Interlocked.CompareExchange(ref _inFlightTransfers, 0, 0) != 0 && spins < 1000)
        {
            Thread.Sleep(1);
            spins++;
        }
    }
}