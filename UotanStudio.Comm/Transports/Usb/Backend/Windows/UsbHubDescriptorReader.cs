using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// One populated port of one hub as the hub driver reported it: the negotiated speed, the descriptor's
/// VID/PID, and the port device's driver key name used to correlate the port with a PnP devnode. The driver
/// key is the authoritative correlation because a vendor controller stack — ASMedia, for example — reports
/// <c>DEVPKEY_Device_Address</c> values that do not line up with the hub's connection indexes.
/// <para>集线器驱动报告的单个在位端口：协商速度、描述符 VID/PID，以及用于把端口与 PnP 设备节点关联的
/// 驱动键名。驱动键是权威关联依据，因为厂商控制器栈（例如 ASMedia）报告的 <c>DEVPKEY_Device_Address</c>
/// 与集线器连接索引并不一致。</para>
/// </summary>
/// <param name="HubInstanceId">
/// The hub devnode's device instance id. <para>集线器设备节点的设备实例标识。</para>
/// </param>
/// <param name="Port">
/// The one-based port number. <para>一基端口号。</para>
/// </param>
/// <param name="VendorId">
/// The descriptor's vendor id. <para>描述符的厂商 ID。</para>
/// </param>
/// <param name="ProductId">
/// The descriptor's product id. <para>描述符的产品 ID。</para>
/// </param>
/// <param name="Speed">
/// The negotiated link speed of the port. <para>端口的协商链路速度。</para>
/// </param>
/// <param name="DriverKeyName">
/// The port device's driver key name, or <see langword="null"/> when the hub does not report one.
/// <para>端口设备的驱动键名；集线器未报告时为 <see langword="null"/>。</para>
/// </param>
internal sealed record HubPortSnapshot(
    string HubInstanceId,
    uint Port,
    ushort VendorId,
    ushort ProductId,
    UsbDeviceSpeed Speed,
    string? DriverKeyName);

/// <summary>
/// Reads a USB device's device descriptor and negotiated link speed from the hub it is plugged into, without
/// opening the device itself. A port that belongs to a vendor kernel driver — a Rockchip boot ROM port serviced
/// by <c>rockusb.sys</c>, for example — cannot be opened through WinUSB at all, yet the hub driver still reports
/// the descriptor it enumerated, including the <c>bcdUSB</c> value that tells the boot ROM's mask ROM stage from
/// its loader stage, and the speed byte that reports the negotiated link rate.
/// <para>从设备所插入的集线器读取 USB 设备的设备描述符与协商链路速度，而不打开设备本身。属于厂商内核驱动的
/// 端口——例如由 <c>rockusb.sys</c> 服务的瑞芯微 boot ROM 端口——根本无法经 WinUSB 打开，但集线器驱动仍会报告
/// 它枚举到的描述符，其中包含用于区分 boot ROM 的 mask ROM 阶段与 loader 阶段的 <c>bcdUSB</c> 取值，以及报告
/// 协商链路速率的速度字节。</para>
/// </summary>
internal static class UsbHubDescriptorReader
{
    /// <summary><c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION</c>, the request that reports one hub port's device.
    /// <para><c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION</c>，报告单个集线器端口设备的请求。</para>
    /// </summary>
    private const uint IoctlUsbGetNodeConnectionInformation = 0x22040C;

    /// <summary><c>IOCTL_USB_GET_NODE_INFORMATION</c>, the request that reports a hub's descriptor including its
    /// port count.
    /// <para><c>IOCTL_USB_GET_NODE_INFORMATION</c>，报告集线器描述符（含端口号）的请求。</para>
    /// </summary>
    private const uint IoctlUsbGetNodeInformation = 0x220408;

    /// <summary><c>IOCTL_USB_GET_NODE_CONNECTION_DRIVERKEY_NAME</c>, the request that reports the driver key name
    /// of one hub port's device.
    /// <para><c>IOCTL_USB_GET_NODE_CONNECTION_DRIVERKEY_NAME</c>，报告单个集线器端口设备驱动键名的请求。</para>
    /// </summary>
    private const uint IoctlUsbGetNodeConnectionDriverKeyName = 0x220420;

    /// <summary><c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX</c>, whose <c>Speed</c> byte reports the
    /// negotiated link rate of one hub port's device.
    /// <para><c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX</c>，其 <c>Speed</c> 字节报告单个集线器端口设备
    /// 的协商链路速率。</para>
    /// </summary>
    private const uint IoctlUsbGetNodeConnectionInformationEx = 0x220448;

    /// <summary><c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2</c>, whose flags separate a SuperSpeedPlus
    /// link from plain SuperSpeed.
    /// <para><c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2</c>，其标志位把 SuperSpeedPlus 链路与普通
    /// SuperSpeed 区分开。</para>
    /// </summary>
    private const uint IoctlUsbGetNodeConnectionInformationExV2 = 0x22045C;

    /// <summary>Bytes of the request buffer: the connection index plus the unused tail the driver tolerates.
    /// <para>请求缓冲区字节数：连接索引加上驱动可容忍的未使用尾部。</para>
    /// </summary>
    private const uint RequestBytes = 32;

    /// <summary>Bytes of the EX request buffer, exactly <c>sizeof(USB_NODE_CONNECTION_INFORMATION_EX)</c>:
    /// the connection index, the embedded descriptor and the fixed output header.
    /// <para>EX 请求缓冲区字节数，恰好是 <c>sizeof(USB_NODE_CONNECTION_INFORMATION_EX)</c>：连接索引、内嵌
    /// 描述符与固定输出头。</para>
    /// </summary>
    private const uint ExRequestBytes = 36;

    /// <summary>Bytes of the V2 request buffer, exactly <c>sizeof(USB_NODE_CONNECTION_INFORMATION_EX_V2)</c>.
    /// <para>V2 请求缓冲区字节数，恰好是 <c>sizeof(USB_NODE_CONNECTION_INFORMATION_EX_V2)</c>。</para>
    /// </summary>
    private const uint V2RequestBytes = 16;

    /// <summary>Bytes of the response buffer, above the largest connection-information response.
    /// <para>响应缓冲区字节数，大于最大的连接信息响应。</para>
    /// </summary>
    private const uint ResponseBytes = 512;

    /// <summary>Bytes of the V2 response buffer, above the 16-byte V2 record.
    /// <para>V2 响应缓冲区字节数，大于 16 字节的 V2 记录。</para>
    /// </summary>
    private const uint V2ResponseBytes = 32;

    /// <summary>Bytes of the driver-key-name response buffer: the 8-byte header plus a class driver key string.
    /// <para>驱动键名响应缓冲区字节数：8 字节头加一条类驱动键字符串。</para>
    /// </summary>
    private const uint DriverKeyNameResponseBytes = 256;

    /// <summary>Offset of the embedded <c>USB_DEVICE_DESCRIPTOR</c> behind the leading connection index.
    /// <para>内嵌 <c>USB_DEVICE_DESCRIPTOR</c> 相对开头连接索引的偏移。</para>
    /// </summary>
    private const int DescriptorOffset = 4;

    private const int BcdUsbOffset = DescriptorOffset + 2;
    private const int IdVendorOffset = DescriptorOffset + 8;
    private const int IdProductOffset = DescriptorOffset + 10;

    /// <summary>Offset of the EX response's <c>Speed</c> byte: connection index, descriptor and
    /// <c>CurrentConfigurationValue</c> precede it.
    /// <para>EX 响应中 <c>Speed</c> 字节的偏移：其前是连接索引、描述符与
    /// <c>CurrentConfigurationValue</c>。</para>
    /// </summary>
    private const int SpeedOffset = DescriptorOffset + 18 + 1;

    /// <summary>Offset of the node-information response's <c>bNbrPorts</c> byte: the node type and the hub
    /// descriptor's length and type bytes precede it.
    /// <para>节点信息响应中 <c>bNbrPorts</c> 字节的偏移：其前是节点类型与集线器描述符的长度、类型字节。</para>
    /// </summary>
    private const int PortCountOffset = 4 + 2;

    /// <summary>Offset of the driver-key-name response's string behind the connection index and actual length.
    /// <para>驱动键名响应中字符串的偏移，其前是连接索引与实际长度。</para>
    /// </summary>
    private const int DriverKeyNameOffset = 8;

    /// <summary>Offset of the V2 response's <c>Flags</c> bitmask behind connection index, length and protocols.
    /// <para>V2 响应中 <c>Flags</c> 位掩码的偏移，其前是连接索引、长度与协议掩码。</para>
    /// </summary>
    private const int V2FlagsOffset = 12;

    /// <summary>Node-type value of a plain hub in the node-information response; multi-interface parents carry
    /// no hub descriptor.
    /// <para>节点信息响应中普通集线器的节点类型值；多重接口父节点不带集线器描述符。</para>
    /// </summary>
    private const uint NodeTypeHub = 0;

    /// <summary>Upper bound of ports a single hub may report; the descriptor's byte is a uchar so this is only
    /// a defensive clamp.
    /// <para>单个集线器可报告的端口数上限；描述符中该字段是 uchar，此处只是防御性钳制。</para>
    /// </summary>
    private const uint MaxHubPorts = 255;

    /// <summary>V2 flag bit 2: the device is operating at SuperSpeedPlus or higher.
    /// <para>V2 标志第 2 位：设备正运行在 SuperSpeedPlus 或更高。</para>
    /// </summary>
    private const uint V2FlagOperatingAtSuperSpeedPlusOrHigher = 0x4;

    /// <summary>V2 input protocol bitmask the caller understands: USB 1.1, 2.0 and 3.x.
    /// <para>V2 输入的调用方可理解协议掩码：USB 1.1、2.0 与 3.x。</para>
    /// </summary>
    private const uint V2SupportedProtocols = 0x7;

    /// <summary>The USB hub device-interface class, published by every hub devnode on this machine.
    /// <para>USB 集线器设备接口类，本机每个集线器设备节点都会发布。</para>
    /// </summary>
    private static readonly Guid UsbHubInterfaceClass = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    /// <summary>
    /// Reads the <c>bcdUSB</c> value of a connected device through its parent hub, verifying that the hub reports the
    /// same vendor and product id the enumeration already saw.
    /// <para>经父集线器读取在位设备的 <c>bcdUSB</c> 取值，并核对集线器报告的厂商与产品 ID 与枚举结果一致。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id from enumeration. <para>枚举得到的设备实例标识。</para></param>
    /// <param name="expectedVendorId">The vendor id enumeration reported for this device.
    /// <para>枚举为该设备报告的厂商 ID。</para></param>
    /// <param name="expectedProductId">The product id enumeration reported for this device.
    /// <para>枚举为该设备报告的产品 ID。</para></param>
    /// <param name="bcdUsb">The descriptor's <c>bcdUSB</c> value when this returns <see langword="true"/>.
    /// <para>返回 <see langword="true"/> 时描述符的 <c>bcdUSB</c> 取值。</para></param>
    /// <returns><see langword="true"/> when the hub answered for this port and its identity matched; every failure is
    /// reported as <see langword="false"/> rather than as a guess.
    /// <para>集线器为该端口作答且身份匹配时为 <see langword="true"/>；任何失败都返回 <see langword="false"/>，不做猜测。</para></returns>
    internal static bool TryReadBcdUsb(string? instanceId, ushort expectedVendorId, ushort expectedProductId,
        out ushort bcdUsb)
    {
        bcdUsb = 0;
        if (!TryOpenDeviceHub(instanceId, out var hubHandle, out var hubInstanceId, out var port))
        {
            return false;
        }

        try
        {
            var request = new byte[RequestBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(request, port);
            var response = new byte[ResponseBytes];
            if (!Win32API.DeviceIoControl(hubHandle, IoctlUsbGetNodeConnectionInformation, request,
                (uint)request.Length, response, (uint)response.Length, out var returned, IntPtr.Zero) ||
                returned < IdProductOffset + sizeof(ushort))
            {
                return false;
            }

            if (!MatchesDevice(response, expectedVendorId, expectedProductId))
            {
                UsbTrace.Log($"UsbHubDescriptorReader: port {port} of '{hubInstanceId}' reports " +
                    $"{ReadVendorId(response):X4}:{ReadProductId(response):X4} instead of " +
                    $"{expectedVendorId:X4}:{expectedProductId:X4}.");
                return false;
            }

            bcdUsb = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(BcdUsbOffset));
            return true;
        }
        finally
        {
            Win32API.CloseHandle(hubHandle);
        }
    }

    /// <summary>
    /// Reads every populated port of one hub: per port the negotiated speed, the descriptor's VID/PID and the
    /// port device's driver key name. Ports the hub driver cannot fully describe — empty ports, unstarted hubs,
    /// vendor stacks that reject the standard hub requests — are skipped rather than guessed, so an absent
    /// snapshot always means unknown and never a wrong speed.
    /// <para>读取单个集线器所有在位端口：每端口的协商速度、描述符 VID/PID 与端口设备驱动键名。集线器驱动
    /// 无法完整描述的端口（空端口、未启动的集线器、拒绝标准集线器请求的厂商栈）直接跳过而非猜测，因此缺失
    /// 快照只意味着未知，绝不会给出错误速度。</para>
    /// </summary>
    /// <param name="hubInstanceId">The hub devnode's device instance id. <para>集线器设备节点的设备实例标识。</para></param>
    /// <returns>The populated ports' snapshots; empty when the hub is unavailable or rejects the queries.
    /// <para>在位端口的快照；集线器不可用或拒绝查询时为空。</para></returns>
    internal static IReadOnlyList<HubPortSnapshot> ReadHubPorts(string hubInstanceId)
    {
        var snapshots = new List<HubPortSnapshot>();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(hubInstanceId) ||
            UsbPnpNative.FindDeviceInterfacePath(UsbHubInterfaceClass, hubInstanceId) is not { } hubPath)
        {
            return snapshots;
        }

        var handle = Win32API.CreateFileW(hubPath, Win32API.GENERIC_READ | Win32API.GENERIC_WRITE,
            Win32API.FILE_SHARE_READ | Win32API.FILE_SHARE_WRITE, IntPtr.Zero, Win32API.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == Win32API.INVALID_HANDLE_VALUE)
        {
            UsbTrace.Log($"UsbHubDescriptorReader: hub '{hubInstanceId}' could not be opened.");
            return snapshots;
        }

        try
        {
            if (!TryReadPortCount(handle, out var portCount))
            {
                UsbTrace.Log($"UsbHubDescriptorReader: hub '{hubInstanceId}' did not answer the node information query.");
                return snapshots;
            }

            for (var port = 1u; port <= Math.Min(portCount, MaxHubPorts); port++)
            {
                if (TryReadPort(handle, hubInstanceId, port) is { } snapshot)
                {
                    snapshots.Add(snapshot);
                }
            }
        }
        finally
        {
            Win32API.CloseHandle(handle);
        }

        return snapshots;
    }

    /// <summary>
    /// Reads one port's speed, descriptor identity and driver key name, returning <see langword="null"/> when
    /// the hub does not describe the port fully enough to correlate it.
    /// <para>读取单个端口的速度、描述符身份与驱动键名；集线器对该端口的描述不足以完成关联时返回
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <param name="hubHandle">The opened hub handle. <para>已打开的集线器句柄。</para></param>
    /// <param name="hubInstanceId">The hub devnode's instance id. <para>集线器设备节点的实例标识。</para></param>
    /// <param name="port">The one-based port number. <para>一基端口号。</para></param>
    /// <returns>The port snapshot, or <see langword="null"/> for an empty or undescribable port.
    /// <para>端口快照；空端口或无法描述的端口为 <see langword="null"/>。</para></returns>
    private static HubPortSnapshot? TryReadPort(IntPtr hubHandle, string hubInstanceId, uint port)
    {
        var request = new byte[ExRequestBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(request, port);
        var response = new byte[ResponseBytes];
        if (!Win32API.DeviceIoControl(hubHandle, IoctlUsbGetNodeConnectionInformationEx, request,
            (uint)request.Length, response, (uint)response.Length, out var returned, IntPtr.Zero) ||
            returned < SpeedOffset + sizeof(byte))
        {
            return null;
        }

        var speed = UsbSpeedInference.FromPortSpeedByte(response[SpeedOffset]);
        var vendorId = ReadVendorId(response);
        var productId = ReadProductId(response);
        if (speed == UsbDeviceSpeed.Super && TryReadSuperSpeedPlus(hubHandle, port))
        {
            speed = UsbDeviceSpeed.SuperPlus;
        }

        var driverKeyName = TryReadPortDriverKeyName(hubHandle, port);
        if ((vendorId == 0 && productId == 0) && string.IsNullOrEmpty(driverKeyName))
        {
            return null;
        }

        return new HubPortSnapshot(hubInstanceId, port, vendorId, productId, speed, driverKeyName);
    }

    /// <summary>
    /// Reads a hub's port count from its node information; multi-interface parents and unstartable hubs report
    /// no count.
    /// <para>从集线器的节点信息读取端口号；多重接口父节点与未启动的集线器没有端口号。</para>
    /// </summary>
    /// <param name="hubHandle">The opened hub handle. <para>已打开的集线器句柄。</para></param>
    /// <param name="portCount">The reported port count when this returns <see langword="true"/>.
    /// <para>返回 <see langword="true"/> 时报告的端口数。</para></param>
    /// <returns><see langword="true"/> when the hub answered as a plain hub with at least one port.
    /// <para>集线器以普通集线器身份应答且至少有一个端口时为 <see langword="true"/>。</para></returns>
    private static bool TryReadPortCount(IntPtr hubHandle, out uint portCount)
    {
        portCount = 0;
        var response = new byte[ResponseBytes];
        if (!Win32API.DeviceIoControl(hubHandle, IoctlUsbGetNodeInformation, null, 0u,
            response, (uint)response.Length, out var returned, IntPtr.Zero) ||
            returned < PortCountOffset + sizeof(byte) ||
            BinaryPrimitives.ReadUInt32LittleEndian(response) != NodeTypeHub)
        {
            return false;
        }

        portCount = response[PortCountOffset];
        return portCount > 0;
    }

    /// <summary>
    /// Reads one port device's driver key name; the string is size-bounded to the response buffer and treated
    /// as absent when the hub reports no name.
    /// <para>读取单个端口设备的驱动键名；字符串以响应缓冲区为界，集线器未报告时按缺失处理。</para>
    /// </summary>
    /// <param name="hubHandle">The opened hub handle. <para>已打开的集线器句柄。</para></param>
    /// <param name="port">The one-based port number. <para>一基端口号。</para></param>
    /// <returns>The driver key name, or <see langword="null"/> when the hub reports none.
    /// <para>驱动键名；集线器未报告时为 <see langword="null"/>。</para></returns>
    private static string? TryReadPortDriverKeyName(IntPtr hubHandle, uint port)
    {
        var request = new byte[RequestBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(request, port);
        var response = new byte[DriverKeyNameResponseBytes];
        if (!Win32API.DeviceIoControl(hubHandle, IoctlUsbGetNodeConnectionDriverKeyName, request,
            (uint)request.Length, response, (uint)response.Length, out var returned, IntPtr.Zero) ||
            returned < DriverKeyNameOffset)
        {
            return null;
        }

        var actualLength = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4));
        var usableBytes = (int)Math.Min(returned, actualLength) - DriverKeyNameOffset;
        if (usableBytes <= 0)
        {
            return null;
        }

        var name = Encoding.Unicode.GetString(response, DriverKeyNameOffset, usableBytes).TrimEnd('\0');
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// Queries the V2 connection information of one port and reports whether the device on it is operating at
    /// SuperSpeedPlus or higher.
    /// <para>查询单个端口的 V2 连接信息并报告其设备是否运行在 SuperSpeedPlus 或更高。</para>
    /// </summary>
    /// <param name="hubHandle">The opened parent hub handle. <para>已打开的父集线器句柄。</para></param>
    /// <param name="port">The one-based port number. <para>一基端口号。</para></param>
    /// <returns><see langword="true"/> when the V2 flags mark a SuperSpeedPlus link; <see langword="false"/> when
    /// the query fails or the flags do not mark one, in which case the port stays plain SuperSpeed.
    /// <para>V2 标志标记 SuperSpeedPlus 链路时为 <see langword="true"/>；查询失败或标志未标记时为
    /// <see langword="false"/>，此时端口按普通 SuperSpeed 报告。</para></returns>
    private static bool TryReadSuperSpeedPlus(IntPtr hubHandle, uint port)
    {
        var request = new byte[V2RequestBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(request, port);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), V2RequestBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), V2SupportedProtocols);
        var response = new byte[V2ResponseBytes];
        if (!Win32API.DeviceIoControl(hubHandle, IoctlUsbGetNodeConnectionInformationExV2, request,
            (uint)request.Length, response, (uint)response.Length, out var returned, IntPtr.Zero) ||
            returned < V2FlagsOffset + sizeof(uint))
        {
            UsbTrace.Log($"UsbHubDescriptorReader: port {port} did not answer the V2 speed flags query.");
            return false;
        }

        return (BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(V2FlagsOffset)) &
            V2FlagOperatingAtSuperSpeedPlusOrHigher) != 0;
    }

    /// <summary>
    /// Locates the parent hub of one device, reads the port the device sits on and opens the hub's device interface.
    /// <para>定位某个设备的父集线器，读取设备所在端口并打开集线器的设备接口。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id from enumeration. <para>枚举得到的设备实例标识。</para></param>
    /// <param name="hubHandle">The opened hub handle when this returns <see langword="true"/>; the caller owns and
    /// must close it. <para>返回 <see langword="true"/> 时已打开的集线器句柄；由调用方负责关闭。</para></param>
    /// <param name="hubInstanceId">The parent hub's device instance id. <para>父集线器的设备实例标识。</para></param>
    /// <param name="port">The one-based port the device is connected to. <para>设备所连的一基端口号。</para></param>
    /// <returns><see langword="true"/> when the hub interface was located and opened.
    /// <para>找到并打开集线器接口时为 <see langword="true"/>。</para></returns>
    private static bool TryOpenDeviceHub(string? instanceId, out IntPtr hubHandle, out string hubInstanceId,
        out uint port)
    {
        hubHandle = IntPtr.Zero;
        hubInstanceId = string.Empty;
        port = 0;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId) ||
            !UsbPnpNative.TryLocateDevNode(instanceId, out var devInst) ||
            !UsbPnpNative.TryReadAddressPort(devInst, out port) ||
            !UsbPnpNative.TryGetParent(devInst, out var hubDevInst) ||
            UsbPnpNative.GetDeviceId(hubDevInst) is not { Length: > 0 } hubId ||
            UsbPnpNative.FindDeviceInterfacePath(UsbHubInterfaceClass, hubId) is not { } hubPath)
        {
            return false;
        }

        var handle = Win32API.CreateFileW(hubPath, Win32API.GENERIC_READ | Win32API.GENERIC_WRITE,
            Win32API.FILE_SHARE_READ | Win32API.FILE_SHARE_WRITE, IntPtr.Zero, Win32API.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == Win32API.INVALID_HANDLE_VALUE)
        {
            UsbTrace.Log($"UsbHubDescriptorReader: hub '{hubId}' could not be opened for {instanceId}.");
            return false;
        }

        hubHandle = handle;
        hubInstanceId = hubId;
        return true;
    }

    private static bool MatchesDevice(ReadOnlySpan<byte> response, ushort expectedVendorId, ushort expectedProductId)
        => ReadVendorId(response) == expectedVendorId && ReadProductId(response) == expectedProductId;

    private static ushort ReadVendorId(ReadOnlySpan<byte> response)
        => BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(IdVendorOffset, sizeof(ushort)));

    private static ushort ReadProductId(ReadOnlySpan<byte> response)
        => BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(IdProductOffset, sizeof(ushort)));
}