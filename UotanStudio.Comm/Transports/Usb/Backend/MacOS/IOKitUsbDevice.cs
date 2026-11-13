using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// macOS USB device backed by the IOKit classic API. Opens the device via
/// <c>IOCreatePlugInInterfaceForService</c> + <c>IOUSBDeviceInterface</c>
/// COM-vtable, claims the target interface through
/// <c>IOUSBInterfaceInterface</c>, and performs bulk I/O through
/// <c>ReadPipeTO</c>/<c>WritePipeTO</c>.
/// <para>基于 IOKit 经典 API 的 macOS USB 设备。通过
/// <c>IOCreatePlugInInterfaceForService</c> + <c>IOUSBDeviceInterface</c>
/// COM-vtable 打开设备，经 <c>IOUSBInterfaceInterface</c> 声明目标接口，并通过
/// <c>ReadPipeTO</c>/<c>WritePipeTO</c> 执行批量 I/O。</para>
/// </summary>
/// <remarks>
/// The calling shape follows Google adb / fastboot usb_osx.cc: the device-level
/// <c>USBDeviceOpen</c> is deliberately NOT used — on current macOS it fails with
/// <c>kIOReturnNoSpace</c> when the system has claimed the device. Only the
/// interface-level <c>USBInterfaceOpen</c> is required for pipe I/O. All method
/// pointers are read via <c>IOKitUsbAPI.GetDelegate</c> (double indirection,
/// verified layout). See README "Platform Notes (macOS IOKit backend)".
/// <para>调用形遵循谷歌 adb / fastboot usb_osx.cc：刻意不使用设备级
/// <c>USBDeviceOpen</c>——当前 macOS 上系统已声明设备时它会以
/// <c>kIOReturnNoSpace</c> 失败。管道 I/O 仅需接口级 <c>USBInterfaceOpen</c>。
/// 所有方法指针经 <c>IOKitUsbAPI.GetDelegate</c> 读取（双重解引用，已验证布局）。
/// 详见 README「平台注意事项（macOS IOKit 后端）」。</para>
/// </remarks>
internal class IOKitUsbDevice : UsbDevice
{
    /// <summary>
    /// The stable IORegistry entry ID, used to reopen the device by identity.
    /// <para>稳定的 IORegistry 项 ID，用于按标识重开设备。</para>
    /// </summary>
    public ulong RegistryEntryId { get; set; }

    private const int PlatformDefaultTimeoutMs = UsbTransferPolicies.DefaultTimeoutMs;

    /// <summary>Bounded retries for a device whose previous user client is still being torn down.
    /// <para>设备上一个用户客户端仍在拆除时的有界重试次数。</para></summary>
    private const int ExclusiveAccessRetryAttempts = 40;

    /// <summary>Delay between exclusive-access retries, in milliseconds.
    /// <para>独占访问重试之间的延迟（毫秒）。</para></summary>
    private const int ExclusiveAccessRetryDelayMs = 500;

    private IntPtr _pluginInterface = IntPtr.Zero;
    private IntPtr _deviceInterface = IntPtr.Zero;
    private bool _deviceOpened;
    private IntPtr _interfaceInterface = IntPtr.Zero;

    private byte _bulkIn;
    private byte _bulkOut;
    private string? _lastInterfaceFailure;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => _deviceInterface != IntPtr.Zero;

    /// <inheritdoc/>
    protected override string BackendName => "macos-iokit";

    /// <inheritdoc/>
    protected override bool IsOpen => _interfaceInterface != IntPtr.Zero;

    /// <inheritdoc/>
    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (_interfaceInterface == IntPtr.Zero || _bulkIn == 0)
        {
            return UsbChunkResult.Fatal(IOKitUsbAPI.KIOReturnNoDevice);
        }

        var readPipe = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReadPipeTODelegate>(_interfaceInterface, IOKitUsbAPI.Offset_ReadPipeTO);
        var size = (uint)length;
        var kr = readPipe(_interfaceInterface, _bulkIn, buffer, ref size, (uint)timeoutMs, (uint)timeoutMs);
        return kr != IOKitUsbAPI.KIOReturnSuccess
            ? kr is IOKitUsbAPI.KIOReturnNoDevice or IOKitUsbAPI.KIOReturnNotResponding or IOKitUsbAPI.KIOReturnAborted
                ? UsbChunkResult.Fatal(kr)
                : kr == IOKitUsbAPI.KIOReturnTimeout ? UsbChunkResult.Timeout(kr) : UsbChunkResult.Error(kr)
            : UsbChunkResult.Success((int)size);
    }

    /// <inheritdoc/>
    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (_interfaceInterface == IntPtr.Zero || _bulkOut == 0)
        {
            return UsbChunkResult.Fatal(IOKitUsbAPI.KIOReturnNoDevice);
        }

        var writePipe = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.WritePipeTODelegate>(_interfaceInterface, IOKitUsbAPI.Offset_WritePipeTO);
        var kr = writePipe(_interfaceInterface, _bulkOut, buffer, (uint)length, (uint)timeoutMs, (uint)timeoutMs);
        return kr != IOKitUsbAPI.KIOReturnSuccess
            ? kr is IOKitUsbAPI.KIOReturnNoDevice or IOKitUsbAPI.KIOReturnNotResponding or IOKitUsbAPI.KIOReturnAborted
                ? UsbChunkResult.Fatal(kr)
                : kr == IOKitUsbAPI.KIOReturnTimeout ? UsbChunkResult.Timeout(kr) : UsbChunkResult.Error(kr)
            : UsbChunkResult.Success(length);
    }

    /// <inheritdoc/>
    protected override bool IsDisconnectionError(int nativeError)
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
        if (_interfaceInterface == IntPtr.Zero || _bulkIn == 0)
        {
            return 0;
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var readPipe = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReadPipeTODelegate>(_interfaceInterface, IOKitUsbAPI.Offset_ReadPipeTO);

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
            var size = (uint)length;
            var kr = readPipe(_interfaceInterface, _bulkIn, ptr, ref size, (uint)effectiveTimeoutMs, (uint)effectiveTimeoutMs);
            return kr != IOKitUsbAPI.KIOReturnSuccess
                ? kr is IOKitUsbAPI.KIOReturnNoDevice or IOKitUsbAPI.KIOReturnNotResponding or IOKitUsbAPI.KIOReturnAborted
                    ? throw new UsbDeviceDisconnectedException($"USB device disconnected during read (error: 0x{kr:X}).", kr)
                    : kr == IOKitUsbAPI.KIOReturnTimeout ? 0 : throw new IOException($"USB read failed with error: 0x{kr:X}")
                : (int)size;
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
        if (_interfaceInterface == IntPtr.Zero || _bulkOut == 0)
        {
            return -1;
        }

        if (length == 0)
        {
            WriteZlp(timeoutMs);
            return 0;
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var writePipe = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.WritePipeTODelegate>(_interfaceInterface, IOKitUsbAPI.Offset_WritePipeTO);

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var lenRemaining = length;
            var count = 0;
            const int maxLenToSend = 1048576;
            while (lenRemaining > 0)
            {
                var lenToSend = Math.Min(lenRemaining, maxLenToSend);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + count);
                var kr = writePipe(_interfaceInterface, _bulkOut, ptr, (uint)lenToSend, (uint)effectiveTimeoutMs, (uint)effectiveTimeoutMs);
                if (kr == IOKitUsbAPI.KIOUSBPipeStalled)
                {
                    // The device consumed the payload and answered the handshake with a stall — exactly
                    // what a Qualcomm programmer does after each Firehose command. The bytes are on the
                    // wire, so clear the condition and report the chunk as written instead of failing.
                    var clear = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ClearPipeStallBothEndsDelegate>(
                        _interfaceInterface, IOKitUsbAPI.Offset_ClearPipeStallBothEnds);
                    var cleared = clear(_interfaceInterface, _bulkOut);
                    UsbTrace.Log($"IOKitUsbDevice.Write: pipe stall after {lenToSend} bytes (expected for a Firehose command); clear=0x{cleared:X}");
                }
                else if (kr != IOKitUsbAPI.KIOReturnSuccess)
                {
                    if (kr is IOKitUsbAPI.KIOReturnNoDevice or IOKitUsbAPI.KIOReturnNotResponding or IOKitUsbAPI.KIOReturnAborted)
                    {
                        throw new UsbDeviceDisconnectedException($"USB device disconnected during write (error: 0x{kr:X}).", kr);
                    }
                    throw new IOException($"USB write failed with error: 0x{kr:X}");
                }
                lenRemaining -= lenToSend;
                count += lenToSend;
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
        if (_interfaceInterface == IntPtr.Zero || _bulkOut == 0)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var writePipe = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.WritePipeTODelegate>(_interfaceInterface, IOKitUsbAPI.Offset_WritePipeTO);
        var kr = writePipe(_interfaceInterface, _bulkOut, IntPtr.Zero, 0, (uint)effectiveTimeoutMs, (uint)effectiveTimeoutMs);
        if (kr != IOKitUsbAPI.KIOReturnSuccess)
        {
            if (kr is IOKitUsbAPI.KIOReturnNoDevice or IOKitUsbAPI.KIOReturnNotResponding or IOKitUsbAPI.KIOReturnAborted)
            {
                throw new UsbDeviceDisconnectedException($"USB device disconnected during zero-length write (error: 0x{kr:X}).", kr);
            }
            throw new IOException($"USB zero-length write failed with error: 0x{kr:X}");
        }
    }

    /// <inheritdoc/>
    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_deviceInterface == IntPtr.Zero)
        {
            return 0;
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var devRequest = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.DeviceRequestDelegate>(_deviceInterface, IOKitUsbAPI.Offset_DeviceRequest);

        GCHandle? handle = null;
        try
        {
            var dataPtr = IntPtr.Zero;
            if (buffer is { Length: > 0 })
            {
                handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                dataPtr = new IntPtr(handle.Value.AddrOfPinnedObject().ToInt64() + offset);
            }

            var request = new IOKitUsbAPI.IOUSBDeviceRequest
            {
                BmRequestType = setupPacket.RequestType,
                BRequest = setupPacket.Request,
                WValue = setupPacket.Value,
                WIndex = setupPacket.Index,
                WLength = (ushort)length,
                PData = dataPtr,
                WLenDone = 0,
            };

            var kr = devRequest(_deviceInterface, ref request);
            if (kr != IOKitUsbAPI.KIOReturnSuccess)
            {
                throw kr is IOKitUsbAPI.KIOReturnNoDevice or IOKitUsbAPI.KIOReturnNotResponding or IOKitUsbAPI.KIOReturnAborted
                    ? new UsbDeviceDisconnectedException($"USB device disconnected during control transfer (error: 0x{kr:X}).", kr)
                    : new IOException($"USB control transfer failed with IOReturn 0x{kr:X}.");
            }

            return (int)request.WLenDone;
        }
        finally
        {
            handle?.Free();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Device-level user-client calls (GetConfiguration/SetConfiguration, USBDeviceReset) need an
    /// open client. Open it best-effort: the system driver usually owns the device on modern macOS,
    /// and the interface path still works when this open is refused.
    /// <para>设备级 user-client 调用（GetConfiguration/SetConfiguration、USBDeviceReset）需要一个
    /// 已打开的 client。此处尽力打开：现代 macOS 上系统驱动通常已持有该设备，打开被拒绝时接口路径
    /// 仍然可用。</para>
    /// </remarks>
    public override int CreateHandle()
    {
        if (_deviceInterface != IntPtr.Zero)
        {
            return 0;
        }

        if (string.IsNullOrEmpty(DevicePath))
        {
            return -1;
        }

        UsbTrace.Log($"IOKitUsbDevice.CreateHandle: DevicePath='{DevicePath}'");
        var service = IOKitUsbAPI.IORegistryEntryFromPath(IntPtr.Zero, DevicePath);
        UsbTrace.Log($"IOKitUsbDevice.CreateHandle: IORegistryEntryFromPath -> {service}");
        if (service == IntPtr.Zero)
        {
            OpenFailureDetail = "IORegistryEntryFromPath found no node for the enumerated path";
            UsbTrace.LogError($"IOKitUsbDevice.CreateHandle: {OpenFailureDetail}: '{DevicePath}'.");
            return -1;
        }

        try
        {
            var deviceClientType = IOKitUsbAPI.CreateCoreFoundationUuid(IOKitUsbAPI.KIOUSBDeviceUserClientTypeID);
            var pluginInterfaceType = IOKitUsbAPI.CreateCoreFoundationUuid(IOKitUsbAPI.KIOCFPlugInInterfaceID);
            int kr;
            IntPtr plugin;
            try
            {
                // A device whose previous owner is still tearing down answers kIOReturnExclusiveAccess
                // for a short window; the bootrom clears it once the old user client is gone, so a
                // bounded retry is what makes consecutive EDL sessions work without a physical replug.
                kr = IOKitUsbAPI.IOCreatePlugInInterfaceForServiceWithUuid(
                    service, deviceClientType, pluginInterfaceType, out plugin, out _);
                for (var attempt = 1;
                     attempt < ExclusiveAccessRetryAttempts
                     && kr is IOKitUsbAPI.KIOReturnExclusiveAccess
                         or IOKitUsbAPI.KIOReturnUnsupported
                         or IOKitUsbAPI.KIOReturnNotPermitted
                         or IOKitUsbAPI.KIOReturnNotPrivileged
                         or IOKitUsbAPI.KIOReturnBusy;
                     attempt++)
                {
                    Thread.Sleep(ExclusiveAccessRetryDelayMs);
                    kr = IOKitUsbAPI.IOCreatePlugInInterfaceForServiceWithUuid(
                        service, deviceClientType, pluginInterfaceType, out plugin, out _);
                    UsbTrace.Log($"IOKitUsbDevice.CreateHandle: exclusive-access retry {attempt} kr=0x{kr:X}");
                }
            }
            finally
            {
                if (deviceClientType != IntPtr.Zero)
                {
                    IOKitUsbAPI.CFRelease(deviceClientType);
                }

                if (pluginInterfaceType != IntPtr.Zero)
                {
                    IOKitUsbAPI.CFRelease(pluginInterfaceType);
                }
            }

            UsbTrace.Log($"IOKitUsbDevice.CreateHandle: IOCreatePlugInInterfaceForService kr={kr} plugin={plugin}");
            if (kr != IOKitUsbAPI.Ok || plugin == IntPtr.Zero)
            {
                OpenFailureDetail = $"the device user client could not be created: {IOKitUsbAPI.DescribeIoReturn(kr)}";
                UsbTrace.LogError($"IOKitUsbDevice.CreateHandle: {OpenFailureDetail}.");
                return -1;
            }

            try
            {
                var queryInterface = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.QueryInterfaceDelegate>(plugin, IOKitUsbAPI.Offset_Plugin_QueryInterface);
                var deviceInterfaceId = new IOKitUsbAPI.CfUuidBytes(IOKitUsbAPI.KIOUSBDeviceInterfaceID);
                var queryResult = queryInterface(plugin, deviceInterfaceId, out _deviceInterface);
                if (queryResult != IOKitUsbAPI.Ok || _deviceInterface == IntPtr.Zero)
                {
                    OpenFailureDetail = "the plugin interface does not expose KIOUSBDeviceInterfaceID";
                    UsbTrace.LogError($"IOKitUsbDevice.CreateHandle: {OpenFailureDetail}.");
                    return -1;
                }
                UsbTrace.Log($"IOKitUsbDevice.CreateHandle: QueryInterface -> deviceInterface={_deviceInterface}");

                _pluginInterface = plugin;

                try
                {
                    var openDevice = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBDeviceOpenDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBDeviceOpen);
                    _deviceOpened = openDevice(_deviceInterface) == IOKitUsbAPI.Ok;
                }
                catch (Exception ex)
                {
                    UsbTrace.Log($"IOKitUsbDevice.CreateHandle: USBDeviceOpen ignored: {ex.GetType().Name}: {ex.Message}");
                }

                var physicalPortPath = MacUsbPortIdentity.Observe(service);
                if (!MacUsbPortIdentity.Matches(PhysicalPortPath, physicalPortPath))
                {
                    return -1;
                }
                PhysicalPortPath = physicalPortPath;

                try
                {
                    var getConf = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBGetConfigurationDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBGetConfiguration);
                    var setConf = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBSetConfigurationDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBSetConfiguration);
                    var getResult = getConf(_deviceInterface, out var currentConf);
                    if (getResult != IOKitUsbAPI.Ok)
                    {
                        UsbTrace.Log($"IOKitUsbDevice.CreateHandle: GetConfiguration failed with IOReturn 0x{getResult:X} (device open: {_deviceOpened}); the current configuration is left unchanged.");
                    }
                    else if (currentConf != 1)
                    {
                        var setResult = setConf(_deviceInterface, 1);
                        if (setResult != IOKitUsbAPI.Ok)
                        {
                            UsbTrace.Log($"IOKitUsbDevice.CreateHandle: SetConfiguration(1) failed with IOReturn 0x{setResult:X}.");
                        }
                    }
                }
                catch
                {
                }

                var findRequest = new IOKitUsbAPI.IOUSBFindInterfaceRequest
                {
                    BInterfaceClass = InterfaceClass ?? IOKitUsbAPI.KIOUSBFindInterfaceDontCare,
                    BInterfaceSubClass = InterfaceSubClass ?? IOKitUsbAPI.KIOUSBFindInterfaceDontCare,
                    BInterfaceProtocol = InterfaceProtocol ?? IOKitUsbAPI.KIOUSBFindInterfaceDontCare,
                    BAlternateSetting = IOKitUsbAPI.KIOUSBFindInterfaceDontCare,
                };

                var createIter = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBDeviceCreateInterfaceIteratorDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBDeviceCreateInterfaceIterator);
                var iteratorResult = createIter(_deviceInterface, ref findRequest, out var iter);
                if (iteratorResult != IOKitUsbAPI.Ok || iter == IntPtr.Zero)
                {
                    OpenFailureDetail =
                        $"the interface iterator could not be created (device open: {_deviceOpened}): " +
                        IOKitUsbAPI.DescribeIoReturn(iteratorResult);
                    UsbTrace.LogError($"IOKitUsbDevice.CreateHandle: {OpenFailureDetail}.");
                    return -1;
                }

                try
                {
                    IntPtr ifc;
                    while ((ifc = new IntPtr(IOKitUsbAPI.IOIteratorNext(iter))) != IntPtr.Zero)
                    {
                        try
                        {
                            if (TryOpenInterface(ifc))
                            {
                                return 0;
                            }
                        }
                        finally
                        {
                            IOKitUsbAPI.IOObjectRelease(ifc);
                        }
                    }
                }
                finally
                {
                    IOKitUsbAPI.IOObjectRelease(iter);
                }

                OpenFailureDetail = _lastInterfaceFailure ?? "no interface exposed a bulk or interrupt pipe";
                UsbTrace.LogError($"IOKitUsbDevice.CreateHandle: {OpenFailureDetail}.");
            }
            finally
            {
                if (_interfaceInterface == IntPtr.Zero && _deviceInterface != IntPtr.Zero)
                {
                    var devClose = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBDeviceCloseDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBDeviceClose);
                    _ = devClose(_deviceInterface);
                    var devRelease = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReleaseDelegate>(_deviceInterface, IOKitUsbAPI.Offset_IUnknown_Release);
                    _ = devRelease(_deviceInterface);
                    _deviceInterface = IntPtr.Zero;

                    if (_pluginInterface != IntPtr.Zero)
                    {
                        var pluginRelease = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReleaseDelegate>(_pluginInterface, IOKitUsbAPI.Offset_Plugin_Release);
                        _ = pluginRelease(_pluginInterface);
                        _pluginInterface = IntPtr.Zero;
                    }
                }
            }

            return -1;
        }
        finally
        {
            IOKitUsbAPI.IOObjectRelease(service);
        }
    }

    private bool TryOpenInterface(IntPtr ifcService)
    {
        var interfaceClientType = IOKitUsbAPI.CreateCoreFoundationUuid(IOKitUsbAPI.KIOUSBInterfaceUserClientTypeID);
        var interfacePlugInType = IOKitUsbAPI.CreateCoreFoundationUuid(IOKitUsbAPI.KIOCFPlugInInterfaceID);
        int kr;
        IntPtr ifcPlugin;
        try
        {
            kr = IOKitUsbAPI.IOCreatePlugInInterfaceForServiceWithUuid(
                ifcService, interfaceClientType, interfacePlugInType, out ifcPlugin, out _);
        }
        finally
        {
            if (interfaceClientType != IntPtr.Zero)
            {
                IOKitUsbAPI.CFRelease(interfaceClientType);
            }

            if (interfacePlugInType != IntPtr.Zero)
            {
                IOKitUsbAPI.CFRelease(interfacePlugInType);
            }
        }
        if (kr != IOKitUsbAPI.Ok || ifcPlugin == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var ifcQuery = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.QueryInterfaceDelegate>(ifcPlugin, IOKitUsbAPI.Offset_Plugin_QueryInterface);
            var ifcId = new IOKitUsbAPI.CfUuidBytes(IOKitUsbAPI.KIOUSBInterfaceInterfaceID190);
            var ifcQueryResult = ifcQuery(ifcPlugin, ifcId, out var ifcIntf);
            if (ifcQueryResult != IOKitUsbAPI.Ok || ifcIntf == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var ifcOpen = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBInterfaceOpenDelegate>(ifcIntf, IOKitUsbAPI.Offset_USBInterfaceOpen);
                var openResult = ifcOpen(ifcIntf);
                if (openResult != IOKitUsbAPI.Ok)
                {
                    _lastInterfaceFailure = $"USBInterfaceOpen refused with {IOKitUsbAPI.DescribeIoReturn(openResult)}";
                    return false;
                }

                var getNumEpts = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.GetNumEndpointsDelegate>(ifcIntf, IOKitUsbAPI.Offset_GetNumEndpoints);
                var getPipeProps = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.GetPipePropertiesDelegate>(ifcIntf, IOKitUsbAPI.Offset_GetPipeProperties);

                if (getNumEpts(ifcIntf, out var numEpts) != IOKitUsbAPI.Ok)
                {
                    return false;
                }

                byte bulkIn = 0;
                byte bulkOut = 0;
                byte interruptIn = 0;
                byte interruptOut = 0;
                for (byte i = 1; i <= numEpts; i++)
                {
                    if (getPipeProps(ifcIntf, i, out var direction, out _, out var transferType, out _, out _) == IOKitUsbAPI.Ok)
                    {
                        if (direction == 1)
                        {
                            if (transferType == 0x02 && bulkIn == 0)
                            {
                                bulkIn = i;
                            }
                            else if (transferType == 0x03 && interruptIn == 0)
                            {
                                interruptIn = i;
                            }
                        }
                        else if (direction == 0)
                        {
                            if (transferType == 0x02 && bulkOut == 0)
                            {
                                bulkOut = i;
                            }
                            else if (transferType == 0x03 && interruptOut == 0)
                            {
                                interruptOut = i;
                            }
                        }
                    }
                }

                var pipeIn = bulkIn != 0 ? bulkIn : interruptIn;
                var pipeOut = bulkOut != 0 ? bulkOut : interruptOut;
                if (pipeIn == 0)
                {
                    return false;
                }

                var clearStall = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ClearPipeStallBothEndsDelegate>(ifcIntf, IOKitUsbAPI.Offset_ClearPipeStallBothEnds);
                _ = clearStall(ifcIntf, pipeIn);
                if (pipeOut != 0)
                {
                    _ = clearStall(ifcIntf, pipeOut);
                }

                _bulkIn = pipeIn;
                _bulkOut = pipeOut;
                _interfaceInterface = ifcIntf;
                return true;
            }
            finally
            {
                if (_interfaceInterface == IntPtr.Zero)
                {
                    var ifcRelease = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReleaseDelegate>(ifcIntf, IOKitUsbAPI.Offset_IUnknown_Release);
                    _ = ifcRelease(ifcIntf);
                }
            }
        }
        finally
        {
            var pluginRelease = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReleaseDelegate>(ifcPlugin, IOKitUsbAPI.Offset_Plugin_Release);
            _ = pluginRelease(ifcPlugin);
        }
    }

    /// <summary>
    /// <c>USBDeviceReset</c> is a device-level reset that re-enumerates the device, so the caller must
    /// reopen the session instead of retrying on the same interface.
    /// <para><c>USBDeviceReset</c> 是会使设备重新枚举的设备级复位，因此调用方必须重新打开会话，而不能在同一
    /// 接口上重试。</para>
    /// </summary>
    public override bool ResetReenumeratesDevice => true;

    /// <inheritdoc/>
    public override void Reset()
    {
        if (_deviceInterface == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var reset = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBDeviceResetDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBDeviceReset);
            _ = reset(_deviceInterface);
        }
        catch
        {
        }
    }

    /// <inheritdoc/>
    public override int GetSerialNumber()
    {
        if (_deviceInterface == IntPtr.Zero)
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
        if (_deviceInterface == IntPtr.Zero)
        {
            return -1;
        }

        var devRequest = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.DeviceRequestDelegate>(_deviceInterface, IOKitUsbAPI.Offset_DeviceRequest);

        GCHandle? handle = null;
        try
        {
            var dataPtr = IntPtr.Zero;
            if (buffer.Length > 0)
            {
                handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                dataPtr = handle.Value.AddrOfPinnedObject();
            }

            var request = new IOKitUsbAPI.IOUSBDeviceRequest
            {
                BmRequestType = bmRequestType,
                BRequest = bRequest,
                WValue = wValue,
                WIndex = wIndex,
                WLength = (ushort)length,
                PData = dataPtr,
                WLenDone = 0,
            };

            var kr = devRequest(_deviceInterface, ref request);
            return kr == IOKitUsbAPI.KIOReturnSuccess ? (int)request.WLenDone : -1;
        }
        finally
        {
            handle?.Free();
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_interfaceInterface != IntPtr.Zero)
        {
            try
            {
                var ifcClose = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBInterfaceCloseDelegate>(_interfaceInterface, IOKitUsbAPI.Offset_USBInterfaceClose);
                _ = ifcClose(_interfaceInterface);
                var ifcRelease = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReleaseDelegate>(_interfaceInterface, IOKitUsbAPI.Offset_IUnknown_Release);
                _ = ifcRelease(_interfaceInterface);
            }
            catch
            {
            }
            _interfaceInterface = IntPtr.Zero;
        }

        if (_deviceInterface != IntPtr.Zero)
        {
            try
            {
                var devClose = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBDeviceCloseDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBDeviceClose);
                if (_deviceOpened)
                {
                    _ = devClose(_deviceInterface);
                    _deviceOpened = false;
                }

                var devRelease = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReleaseDelegate>(_deviceInterface, IOKitUsbAPI.Offset_IUnknown_Release);
                _ = devRelease(_deviceInterface);
            }
            catch
            {
            }
            _deviceInterface = IntPtr.Zero;
        }

        if (_pluginInterface != IntPtr.Zero)
        {
            try
            {
                var pluginRelease = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReleaseDelegate>(_pluginInterface, IOKitUsbAPI.Offset_Plugin_Release);
                _ = pluginRelease(_pluginInterface);
            }
            catch
            {
            }
            _pluginInterface = IntPtr.Zero;
        }
    }
}