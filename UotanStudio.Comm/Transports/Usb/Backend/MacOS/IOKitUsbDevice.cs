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

    // COM pointers: plug-in factory, IOUSBDeviceInterface, IOUSBInterfaceInterface.
    // <para>COM 指针：插件工厂、IOUSBDeviceInterface、IOUSBInterfaceInterface。</para>
    private IntPtr _pluginInterface = IntPtr.Zero;
    private IntPtr _deviceInterface = IntPtr.Zero;
    private IntPtr _interfaceInterface = IntPtr.Zero;

    // Bulk pipe references discovered via GetPipeProperties on the claimed interface.
    // <para>在声明的接口上经 GetPipeProperties 发现的批量管道引用。</para>
    private byte _bulkIn;
    private byte _bulkOut;

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
                if (kr != IOKitUsbAPI.KIOReturnSuccess)
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
            return kr != IOKitUsbAPI.KIOReturnSuccess
                ? kr is IOKitUsbAPI.KIOReturnNoDevice or IOKitUsbAPI.KIOReturnNotResponding or IOKitUsbAPI.KIOReturnAborted
                    ? throw new UsbDeviceDisconnectedException($"USB device disconnected during control transfer (error: 0x{kr:X}).", kr)
                    : 0
                : (int)request.WLenDone;
        }
        finally
        {
            handle?.Free();
        }
    }

    /// <inheritdoc/>
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

        // Re-open the device service from its IOService-plane path (the finder
        // stored the path returned by IORegistryEntryGetPath).
        // <para>从 IOService 平面路径重开设备服务（finder 存储了
        // IORegistryEntryGetPath 返回的路径）。</para>
        UsbTrace.Log($"IOKitUsbDevice.CreateHandle: DevicePath='{DevicePath}'");
        var service = IOKitUsbAPI.IORegistryEntryFromPath(IntPtr.Zero, DevicePath);
        UsbTrace.Log($"IOKitUsbDevice.CreateHandle: IORegistryEntryFromPath -> {service}");
        if (service == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            var kr = IOKitUsbAPI.IOCreatePlugInInterfaceForService(
                service,
                IOKitUsbAPI.KIOUSBDeviceUserClientTypeID,
                IOKitUsbAPI.KIOCFPlugInInterfaceID,
                out var plugin,
                out _);
            UsbTrace.Log($"IOKitUsbDevice.CreateHandle: IOCreatePlugInInterfaceForService kr={kr} plugin={plugin}");
            if (kr != IOKitUsbAPI.Ok || plugin == IntPtr.Zero)
            {
                return -1;
            }

            try
            {
                var queryInterface = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.QueryInterfaceDelegate>(plugin, IOKitUsbAPI.Offset_Plugin_QueryInterface);
                if (queryInterface(plugin, IOKitUsbAPI.KIOUSBDeviceInterfaceID, out _deviceInterface) != IOKitUsbAPI.Ok || _deviceInterface == IntPtr.Zero)
                {
                    UsbTrace.Log("IOKitUsbDevice.CreateHandle: QueryInterface(KIOUSBDeviceInterfaceID) FAILED");
                    return -1;
                }
                UsbTrace.Log($"IOKitUsbDevice.CreateHandle: QueryInterface -> deviceInterface={_deviceInterface}");

                // Keep the plugin alive for the lifetime of the device (the
                // interface returned by QueryInterface borrows its vtable).
                // <para>保持插件存活于设备生命周期（QueryInterface 返回的接口借用其
                // vtable）。</para>
                _pluginInterface = plugin;

                // NOTE: no device-level USBDeviceOpen — it fails with
                // kIOReturnNoSpace when the system claims the device, and adb /
                // fastboot never open the device; CreateInterfaceIterator works on
                // a closed device interface. Pipe I/O only needs USBInterfaceOpen
                // in TryOpenInterface. See README "Platform Notes (macOS IOKit)".
                // <para>注意：不做设备级 USBDeviceOpen——系统已声明设备时它以
                // kIOReturnNoSpace 失败，且 adb/fastboot 从不打开设备；
                // CreateInterfaceIterator 在未打开的设备接口上即可工作。管道 I/O
                // 仅需 TryOpenInterface 中的 USBInterfaceOpen。
                // 详见 README「平台注意事项（macOS IOKit 后端）」。</para>

                // Align with adb: ensure Configuration 1 is selected before creating
                // the interface iterator. Best-effort; a device may legitimately have
                // a single, differently numbered configuration.
                // <para>与 adb 对齐：在创建接口迭代器前确保选择配置 1。尽力而为；设备
                // 可能仅有一个编号不同的配置。</para>
                try
                {
                    var getConf = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBGetConfigurationDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBGetConfiguration);
                    var setConf = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBSetConfigurationDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBSetConfiguration);
                    if (getConf(_deviceInterface, out var currentConf) == IOKitUsbAPI.Ok && currentConf != 1)
                    {
                        _ = setConf(_deviceInterface, 1);
                    }
                }
                catch
                {
                    // Configuration probing/selection is best-effort.
                }

                // Match interfaces by the device's known class/subclass/protocol
                // (adb CheckInterface semantics: avoid claiming mass-storage
                // endpoints). Falls back to KIOUSBFindInterfaceDontCare when the
                // finder did not surface interface codes.
                // <para>按设备已知的类/子类/协议匹配接口（adb CheckInterface 语义：
                // 避免声明大容量存储端点）。finder 未暴露接口码时回退到
                // KIOUSBFindInterfaceDontCare。</para>
                var findRequest = new IOKitUsbAPI.IOUSBFindInterfaceRequest
                {
                    BInterfaceClass = InterfaceClass ?? IOKitUsbAPI.KIOUSBFindInterfaceDontCare,
                    BInterfaceSubClass = InterfaceSubClass ?? IOKitUsbAPI.KIOUSBFindInterfaceDontCare,
                    BInterfaceProtocol = InterfaceProtocol ?? IOKitUsbAPI.KIOUSBFindInterfaceDontCare,
                    BAlternateSetting = IOKitUsbAPI.KIOUSBFindInterfaceDontCare,
                };

                var createIter = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBDeviceCreateInterfaceIteratorDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBDeviceCreateInterfaceIterator);
                if (createIter(_deviceInterface, ref findRequest, out var iter) != IOKitUsbAPI.Ok || iter == IntPtr.Zero)
                {
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
            }
            finally
            {
                if (_interfaceInterface == IntPtr.Zero && _deviceInterface != IntPtr.Zero)
                {
                    // Interface claim failed or no matching interface: release what we
                    // opened so a later retry can start clean.
                    // <para>接口声明失败或无匹配接口：释放已打开者，使后续重试可干净开始。</para>
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

    // Opens one IOUSBInterface service: creates its plug-in, queries the
    // IOUSBInterfaceInterface COM-vtable, opens the interface, discovers pipe
    // references via GetPipeProperties, and clears stall on the chosen pipes.
    // Bulk IN/OUT is preferred; when the interface exposes no bulk pair (e.g.
    // HID keyboards/digitizers), interrupt IN/OUT is used as a fallback, exactly
    // like the libusb backend's bulk-then-interrupt selection. Returns true when
    // a usable interface was claimed.
    // <para>打开一个 IOUSBInterface 服务：创建其插件、查询 IOUSBInterfaceInterface
    // COM-vtable、打开接口、经 GetPipeProperties 发现管道引用，并在选定管道上清除
    // stall。优先使用批量 IN/OUT；当接口无批量对（例如 HID 键盘/数字化仪）时回退到
    // 中断 IN/OUT，与 libusb 后端的 bulk→interrupt 选择一致。声明到可用接口时
    // 返回 true。</para>
    private bool TryOpenInterface(IntPtr ifcService)
    {
        var kr = IOKitUsbAPI.IOCreatePlugInInterfaceForService(
            ifcService,
            IOKitUsbAPI.KIOUSBInterfaceUserClientTypeID,
            IOKitUsbAPI.KIOCFPlugInInterfaceID,
            out var ifcPlugin,
            out _);
        if (kr != IOKitUsbAPI.Ok || ifcPlugin == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var ifcQuery = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.QueryInterfaceDelegate>(ifcPlugin, IOKitUsbAPI.Offset_Plugin_QueryInterface);
            if (ifcQuery(ifcPlugin, IOKitUsbAPI.KIOUSBInterfaceInterfaceID190, out var ifcIntf) != IOKitUsbAPI.Ok || ifcIntf == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var ifcOpen = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBInterfaceOpenDelegate>(ifcIntf, IOKitUsbAPI.Offset_USBInterfaceOpen);
                if (ifcOpen(ifcIntf) != IOKitUsbAPI.Ok)
                {
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
                        if (direction == 1) // kUSBIn
                        {
                            if (transferType == 0x02 && bulkIn == 0)
                            {
                                bulkIn = i;          // Bulk
                            }
                            else if (transferType == 0x03 && interruptIn == 0)
                            {
                                interruptIn = i; // Interrupt
                            }
                        }
                        else if (direction == 0) // kUSBOut
                        {
                            if (transferType == 0x02 && bulkOut == 0)
                            {
                                bulkOut = i;          // Bulk
                            }
                            else if (transferType == 0x03 && interruptOut == 0)
                            {
                                interruptOut = i; // Interrupt
                            }
                        }
                    }
                }

                // Prefer the bulk pair; fall back to interrupt pipes for HID-Style
                // interfaces that have no bulk endpoints (libusb parity).
                // <para>优先使用批量对；无批量端点时回退到中断管道（HID 类接口，
                // 与 libusb 对齐）。</para>
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
                    // Interface not claimed: release the interface COM object.
                    // <para>接口未声明：释放接口 COM 对象。</para>
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

    /// <inheritdoc/>
    public override void Reset()
    {
        if (_deviceInterface == IntPtr.Zero)
        {
            return;
        }

        // USBDeviceReset restarts the device; best-effort (mirrors SharpFastboot).
        // <para>USBDeviceReset 重启设备；尽力而为（与 SharpFastboot 对齐）。</para>
        try
        {
            var reset = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBDeviceResetDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBDeviceReset);
            _ = reset(_deviceInterface);
        }
        catch
        {
            // Best-effort reset.
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
            // Read the device descriptor via a control request; ISerialNumber lives
            // at offset 16, BcdUSB at offset 2 (used to infer the USB speed).
            // <para>经控制请求读取设备描述符；ISerialNumber 在偏移 16，BcdUSB 在偏移 2
            // （用于推断 USB 速度）。</para>
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

            // GET_DESCRIPTOR(STRING), language 0x0409 (en-US).
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
                // Best-effort cleanup.
            }
            _interfaceInterface = IntPtr.Zero;
        }

        if (_deviceInterface != IntPtr.Zero)
        {
            try
            {
                var devClose = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.USBDeviceCloseDelegate>(_deviceInterface, IOKitUsbAPI.Offset_USBDeviceClose);
                _ = devClose(_deviceInterface);
                var devRelease = IOKitUsbAPI.GetDelegate<IOKitUsbAPI.ReleaseDelegate>(_deviceInterface, IOKitUsbAPI.Offset_IUnknown_Release);
                _ = devRelease(_deviceInterface);
            }
            catch
            {
                // Best-effort cleanup.
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
                // Best-effort cleanup.
            }
            _pluginInterface = IntPtr.Zero;
        }
    }
}