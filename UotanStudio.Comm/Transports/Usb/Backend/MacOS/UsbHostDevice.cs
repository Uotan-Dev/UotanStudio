using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// macOS USB device backed by Apple's official IOUSBHost user-space framework,
/// driven from C# through the ObjC runtime (no native bridge library). Claims the target
/// interface through an <c>IOUSBHostInterface</c> user client and performs bulk I/O through
/// <c>IOUSBHostPipe.sendIORequestWithData:</c>; the optional device user client supplies
/// device-level control requests and reset.
/// <para>基于苹果官方 IOUSBHost 用户空间框架的 macOS USB 设备，经 ObjC 运行时从 C#
/// 直接驱动（无原生桥接库）。经 <c>IOUSBHostInterface</c> 用户客户端声明目标接口，
/// 并通过 <c>IOUSBHostPipe.sendIORequestWithData:</c> 执行批量 I/O；可选的设备级用户客户端
/// 提供设备级控制请求与复位。</para>
/// </summary>
/// <remarks>
/// Error codes are IOReturn values reported through NSError.code. Interface access does not require a device user
/// client, so a device whose interfaces are partly owned by system drivers still yields a working bulk session; only
/// device-level calls (serial string, configuration, reset) depend on the device client. The device-level
/// <c>resetWithError:</c> re-enumerates the device (the session must be re-opened afterwards) — this matches libusb's
/// darwin backend semantics.
/// <para>错误码为经 NSError.code 上报的 IOReturn 值。接口访问不要求设备级用户客户端，因此即使设备的某些接口被系统驱动
/// 占用，仍能得到可用的批量会话；只有设备级调用（序列号、配置、复位）依赖设备客户端。设备级
/// <c>resetWithError:</c> 会使设备重新枚举（之后必须重开会话）——与 libusb darwin 后端语义一致。</para>
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
    private string? _lastInterfaceFailure;

    private byte _bulkIn;
    private byte _bulkOut;
    private byte _interfaceNumber;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => _device != IntPtr.Zero || _interface != IntPtr.Zero;

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
            return ChunkResult(kr, transferred);
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
                throw IsDisconnection(kr)
                    ? new UsbDeviceDisconnectedException($"USB device disconnected during control transfer (error: 0x{kr:X}).", kr)
                    : new IOException($"USB control transfer failed with IOReturn 0x{kr:X}.");
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
    /// <remarks>
    /// The device user client is optional. macOS refuses <c>initWithIOService:</c> on the device whenever any system
    /// driver owns part of it (MTP, HID, network) or another tool holds the port, which is the state of every phone
    /// in its normal mode; the interface user client is still grantable and is all a bulk protocol needs. Opening an
    /// interface therefore does not depend on the device client, and device-level calls stay guarded by
    /// <see cref="_device"/>.
    /// <para>设备级 user client 是可选的。只要有系统驱动占用设备的任一部分（MTP、HID、网络）或有其他工具持有该端口，
    /// macOS 就会拒绝在设备上的 <c>initWithIOService:</c>，而这正是每台手机正常模式的状态；接口级 user client 仍可
    /// 获准，而批量协议只需要它。因此打开接口不再依赖设备客户端，设备级调用仍由 <see cref="_device"/> 防护。</para>
    /// </remarks>
    public override int CreateHandle()
    {
        if (_device != IntPtr.Zero || _interface != IntPtr.Zero)
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
            OpenFailureDetail = "IORegistryEntryFromPath found no node for the enumerated path";
            UsbTrace.LogError($"UsbHostDevice.CreateHandle: {OpenFailureDetail} for '{DevicePath}'.");
            return -1;
        }

        try
        {
            var device = UsbHostAPI.CreateObject(UsbHostAPI.GetDeviceClass(), (uint)service.ToInt32(), out var kr);
            UsbTrace.Log($"UsbHostDevice.CreateHandle: device user client kr=0x{kr:X} device={device}");
            if (device != IntPtr.Zero)
            {
                _device = device;
            }
            else
            {
                UsbTrace.Log(
                    $"UsbHostDevice.CreateHandle: device user client refused with {IOKitUsbAPI.DescribeIoReturn(kr)}; continuing with interface-only access.");
            }

            var physicalPortPath = MacUsbPortIdentity.Observe(service);
            if (!MacUsbPortIdentity.Matches(PhysicalPortPath, physicalPortPath))
            {
                OpenFailureDetail =
                    $"the device reports physical port '{physicalPortPath}' instead of '{PhysicalPortPath}'";
                UsbTrace.LogError($"UsbHostDevice.CreateHandle: {OpenFailureDetail}.");
                DestroyDeviceClient();
                return -1;
            }
            PhysicalPortPath = physicalPortPath;

            var children = UsbHostAPI.GetInterfaceServices((uint)service.ToInt32());
            UsbTrace.Log($"UsbHostDevice.CreateHandle: interface children={children.Count}");
            try
            {
                // A composite device exposes several interfaces; when discovery published a triple the session must
                // bind that one (the adb port's 0xFF interface and an MTP interface both carry bulk pipes).
                var claimed = InterfaceClass is not null
                    ? TryOpenFirstInterface(children, InterfaceClass, InterfaceSubClass, InterfaceProtocol)
                        || TryOpenFirstInterface(children, null, null, null)
                    : TryOpenFirstInterface(children, null, null, null);
                if (claimed)
                {
                    UsbTrace.Log($"UsbHostDevice.CreateHandle: interface claimed, bulkIn=0x{_bulkIn:X2} bulkOut=0x{_bulkOut:X2}");
                    return 0;
                }
            }
            finally
            {
                foreach (var child in children)
                {
                    IOKitUsbAPI.IOObjectRelease((IntPtr)child);
                }
            }

            OpenFailureDetail = _lastInterfaceFailure
                ?? "no IOUSBHostInterface child could be opened (the device user client is also unavailable)";
            UsbTrace.LogError($"UsbHostDevice.CreateHandle: {OpenFailureDetail}.");

            DestroyDeviceClient();
            return -1;
        }
        finally
        {
            IOKitUsbAPI.IOObjectRelease(service);
        }
    }

    /// <summary>
    /// Releases the device user client, if the kernel granted one.
    /// <para>释放内核授予的设备级 user client（若已获准）。</para>
    /// </summary>
    private void DestroyDeviceClient()
    {
        if (_device != IntPtr.Zero)
        {
            UsbHostAPI.Destroy(_device);
            _device = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Tries every interface child in order and keeps the first one that opens; when <paramref name="requiredClass"/> is
    /// given, only an interface whose descriptor matches the requested triple is kept.
    /// <para>按顺序尝试每个接口子项并保留第一个能打开的接口；给出 <paramref name="requiredClass"/> 时，只保留描述符
    /// 与请求三元组匹配的接口。</para>
    /// </summary>
    /// <param name="children">The device's interface service handles; the caller owns them.
    /// <para>设备的接口服务句柄；由调用方持有。</para></param>
    /// <param name="requiredClass">Required interface class, or <see langword="null"/> for any.
    /// <para>要求的接口类；<see langword="null"/> 表示不限。</para></param>
    /// <param name="requiredSubClass">Required interface subclass. <para>要求的接口子类。</para></param>
    /// <param name="requiredProtocol">Required interface protocol. <para>要求的接口协议。</para></param>
    /// <returns><see langword="true"/> when an interface was opened and kept.
    /// <para>已打开并保留一个接口时为 <see langword="true"/>。</para></returns>
    private bool TryOpenFirstInterface(List<uint> children, byte? requiredClass, byte? requiredSubClass, byte? requiredProtocol)
    {
        foreach (var child in children)
        {
            if (TryOpenInterface(child, requiredClass, requiredSubClass, requiredProtocol))
            {
                return true;
            }

            UsbTrace.Log($"UsbHostDevice: interface child {child:X} not claimable");
        }

        return false;
    }

    /// <remarks>
    /// Publish the claimed interface's own class triple and endpoints; the finder could not read them
    /// before the interface was opened, and post-open filter matching depends on them.
    /// <para>发布被占用接口自身的类三元组与端点；接口打开之前查找器读不到它们，而打开后的过滤器
    /// 匹配依赖这些值。</para>
    /// </remarks>
    private bool TryOpenInterface(uint ifcService, byte? requiredClass = null, byte? requiredSubClass = null,
        byte? requiredProtocol = null)
    {
        var usbInterface = UsbHostAPI.CreateObject(UsbHostAPI.GetInterfaceClass(), ifcService, out var interfaceError);
        if (usbInterface == IntPtr.Zero)
        {
            _lastInterfaceFailure = $"IOUSBHostInterface open refused with {IOKitUsbAPI.DescribeIoReturn(interfaceError)}";
            return false;
        }

        try
        {
            var descriptor = UsbHostAPI.GetInterfaceDescriptor(usbInterface);
            byte interfaceNumber = 0;
            byte numEndpoints = 0;
            byte interfaceClass = 0;
            byte interfaceSubClass = 0;
            byte interfaceProtocol = 0;
            if (descriptor != IntPtr.Zero)
            {
                interfaceNumber = Marshal.ReadByte(descriptor, 2);
                numEndpoints = Marshal.ReadByte(descriptor, 4);
                interfaceClass = Marshal.ReadByte(descriptor, 5);
                interfaceSubClass = Marshal.ReadByte(descriptor, 6);
                interfaceProtocol = Marshal.ReadByte(descriptor, 7);
            }
            UsbTrace.Log($"TryOpenInterface: ifc#{interfaceNumber} endpoints={numEndpoints} desc={descriptor}");

            if (requiredClass is byte wantedClass
                && (interfaceClass != wantedClass
                    || (requiredSubClass is byte wantedSubClass && interfaceSubClass != wantedSubClass)
                    || (requiredProtocol is byte wantedProtocol && interfaceProtocol != wantedProtocol)))
            {
                UsbTrace.Log($"TryOpenInterface: ifc#{interfaceNumber} class {interfaceClass:X2}/{interfaceSubClass:X2}/{interfaceProtocol:X2} does not match the required {wantedClass:X2}.");
                return false;
            }

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

            var outPipe = IntPtr.Zero;
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

            _interfaceNumber = interfaceNumber;
            InterfaceClass = interfaceClass;
            InterfaceSubClass = interfaceSubClass;
            InterfaceProtocol = interfaceProtocol;
            InterfaceMetadataObserved = true;
            Interfaces =
            [
                new UsbInterfaceInfo
                {
                    InterfaceNumber = interfaceNumber,
                    Class = interfaceClass,
                    SubClass = interfaceSubClass,
                    Protocol = interfaceProtocol,
                    Endpoints = [.. endpoints.Select(endpoint => new UsbEndpointInfo
                    {
                        EndpointAddress = endpoint.Address,
                        Attributes = endpoint.Type,
                        MaxPacketSize = 0
                    })]
                }
            ];
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

    /// <summary>
    /// A device reset tears down every pipe of the IOUSBHost objects, so the session cannot be reused and
    /// the caller must reopen the device.
    /// <para>设备复位会拆除 IOUSBHost 对象的所有管道，因此会话不能复用，调用方必须重新打开设备。</para>
    /// </summary>
    public override bool ResetReenumeratesDevice => true;

    /// <inheritdoc/>
    public override void Reset()
    {
        if (_device == IntPtr.Zero)
        {
            UsbTrace.Log("UsbHostDevice.Reset skipped: no device user client (interface-only session).");
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

    /// <summary>
    /// Switches the claimed IOUSBHostInterface to another alternate setting through
    /// <c>selectAlternateSetting:error:</c> and re-opens its pipes, because the previous pipe objects belong to
    /// the old setting. A setting that no longer exposes the bound endpoint addresses makes the switch fail,
    /// and the caller must reopen the device.
    /// <para>经 <c>selectAlternateSetting:error:</c> 把已申请的 IOUSBHostInterface 切到另一个备用设置并重新打开
    /// 管道，因为旧管道对象属于旧设置。新设置不再提供已绑定的端点地址时切换会失败，调用方必须重新打开设备。</para>
    /// </summary>
    /// <param name="interfaceNumber">The interface number; must be the one this session claimed.
    /// <para>接口号；必须是本会话已申请的那个。</para></param>
    /// <param name="altSetting">The alternate setting to activate. <para>要激活的备用设置。</para></param>
    /// <exception cref="UsbDeviceHandleClosedException">The handle is not open. <para>句柄未打开。</para></exception>
    /// <exception cref="NotSupportedException">The interface number is not the one this session holds.
    /// <para>接口号不是本会话持有的那个。</para></exception>
    /// <exception cref="UsbDeviceDisconnectedException">The device disappeared. <para>设备已消失。</para></exception>
    /// <exception cref="IOException">The setting was rejected or its pipes cannot be bound.
    /// <para>设置被拒绝，或其管道无法绑定。</para></exception>
    public override void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting)
    {
        if (_interface == IntPtr.Zero)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        if (interfaceNumber != _interfaceNumber)
        {
            throw new NotSupportedException($"This session holds interface {_interfaceNumber}; interface {interfaceNumber} is not open.");
        }

        var kr = UsbHostAPI.SelectAlternateSetting(_interface, altSetting);
        if (kr != IOKitUsbAPI.Ok)
        {
            throw IsDisconnection(kr)
                ? new UsbDeviceDisconnectedException($"USB device disconnected while selecting alternate setting {altSetting} (error: 0x{kr:X}).", kr)
                : new IOException($"selectAlternateSetting({altSetting}) failed with IOReturn 0x{kr:X}.");
        }

        RebindPipes();
    }

    private void RebindPipes()
    {
        var inPipe = UsbHostAPI.CopyPipe(_interface, _bulkIn);
        if (inPipe == IntPtr.Zero)
        {
            throw new IOException($"USB endpoint 0x{_bulkIn:X2} is not available in the selected alternate setting; reopen the device.");
        }

        var outPipe = _bulkOut == 0 ? IntPtr.Zero : UsbHostAPI.CopyPipe(_interface, _bulkOut);
        if (_bulkOut != 0 && outPipe == IntPtr.Zero)
        {
            UsbHostAPI.ReleasePipe(inPipe);
            throw new IOException($"USB endpoint 0x{_bulkOut:X2} is not available in the selected alternate setting; reopen the device.");
        }

        UsbHostAPI.ReleasePipe(_pipeIn);
        UsbHostAPI.ReleasePipe(_pipeOut);
        _pipeIn = inPipe;
        _pipeOut = outPipe;
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

            BcdUsb = (ushort)(dd[2] | (dd[3] << 8));
            Speed = UsbSpeedInference.FromBcdUsb(BcdUsb.Value);

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