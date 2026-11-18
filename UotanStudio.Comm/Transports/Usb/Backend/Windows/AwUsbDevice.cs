using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Bulk session over Allwinner's legacy <c>USBDrv</c> (<c>usbUDisc</c>) function driver, the AWUSB driver
/// PhoenixSuit and LiveSuit install for FEL/FES devices. The driver exposes one device interface per FEL
/// device and runs bulk transfers through device-control codes instead of read/write file I/O:
/// <c>0x22201E</c> (bulk OUT, <c>METHOD_OUT_DIRECT</c> — the payload buffer is the output buffer),
/// <c>0x222021</c> (bulk IN, <c>METHOD_IN_DIRECT</c> — the receive buffer is the input buffer and the
/// transfer length comes from the output-buffer size) and <c>0x222025</c> (endpoint queries). Control
/// transfers are not implemented; the Sunxi eFEX protocol never issues them.
/// <para>全志旧式 <c>USBDrv</c>（<c>usbUDisc</c>）功能驱动之上的批量会话，即 PhoenixSuit 与 LiveSuit 为
/// FEL/FES 设备安装的 AWUSB 驱动。该驱动为每台 FEL 设备公布一个设备接口，并以设备控制码而非读写文件
/// I/O 执行批量传输：<c>0x22201E</c>（批量 OUT，<c>METHOD_OUT_DIRECT</c>——负载缓冲作为输出缓冲）、
/// <c>0x222021</c>（批量 IN，<c>METHOD_IN_DIRECT</c>——接收缓冲作为输入缓冲，传输长度取自输出缓冲
/// 大小）与 <c>0x222025</c>（端点查询）。控制传输未实现；Sunxi eFEX 协议从不发出控制传输。</para>
/// </summary>
/// <remarks>
/// The legacy driver has no IRP cancel routine, so a bulk read on a silent device stays pending until the
/// device answers, the handle closes or the device is replugged, and cancellation cannot retire it. A
/// timed-out read therefore reports a fatal error instead of a retryable timeout, so upper layers fail the
/// operation and close the lease rather than retrying into a wedged pipe.
/// <para>旧驱动没有 IRP 取消例程，静默设备上的批量读取会保持挂起，直到设备应答、句柄关闭或设备重新插拔，
/// 取消也无法让它结束。因此读超时按致命错误而非可重试超时上报，使上层直接失败并关闭租约，而不是对着
/// 已堵死的管道重试。</para>
/// </remarks>
internal sealed class AwUsbDevice : UsbDevice
{
    /// <summary>Device-interface class GUID the AWUSB driver publishes for every FEL/FES device.
    /// <para>AWUSB 驱动为每台 FEL/FES 设备公布的设备接口类 GUID。</para></summary>
    public static readonly Guid InterfaceGuid = new("{0E57C50F-0CCC-4AD2-A895-93C5ED119860}");

    /// <summary>Bulk OUT control code; the payload travels as the output buffer's direct MDL.
    /// <para>批量 OUT 控制码；负载经输出缓冲的直接 MDL 传输。</para></summary>
    private const uint BulkOutControlCode = 0x22201E;

    /// <summary>Bulk IN control code; the receive buffer travels as the input buffer's direct MDL.
    /// <para>批量 IN 控制码；接收缓冲经输入缓冲的直接 MDL 传输。</para></summary>
    private const uint BulkInControlCode = 0x222021;

    /// <summary>Endpoint query control code; sub-command 2 copies one endpoint record into the input
    /// buffer's kernel mapping. <para>端点查询控制码；子命令 2 把一条端点记录复制进输入缓冲的内核映射。</para></summary>
    private const uint QueryControlCode = 0x222025;

    /// <summary><c>USBD_PIPE_TYPE</c> value of a bulk pipe. <para>批量管道的 <c>USBD_PIPE_TYPE</c> 取值。</para></summary>
    private const uint BulkPipeType = 2;

    private const int QueryBufferSize = 0x60;
    private const int QueryIndexOffset = 0x38;
    private const int QueryEndpointAddressOffset = 2;
    private const int QueryPipeTypeOffset = 4;
    private const int OverlappedSize = 64;
    private const int OverlappedEventOffset = 24;
    private const int ErrorGenFailure = 31;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorNoSuchDevice = 1168;
    private const int ErrorDeviceRemoved = 1617;
    private const uint WaitObjectZero = 0;

    private readonly SafeFileHandle _handle;
    private readonly byte _endpointIn;
    private readonly byte _endpointOut;

    private AwUsbDevice(SafeFileHandle handle, UsbEndpointDetails usb, string interfacePath, string deviceId)
    {
        _handle = handle;
        // The identity fields mirror what discovery recorded, anchored on the discovery device path, so the
        // wrapper's built device key matches the discovered endpoint and the opener's opened-key check passes.
        // 身份字段镜像发现阶段的记录，并锚定发现的设备路径，使包装层构建的设备键与发现的端点匹配，
        // 开启器的已打开键校验因此通过。
        DevicePath = usb.Path;
        SerialNumber = ExtractSerial(usb.Path);
        VendorId = usb.VendorId;
        ProductId = usb.ProductId;
        InterfaceClass = usb.InterfaceClass;
        InterfaceSubClass = usb.InterfaceSubClass;
        InterfaceProtocol = usb.InterfaceProtocol;
        DeviceInstanceId = deviceId;
        InterfacePath = interfacePath;
        (_endpointOut, _endpointIn) = QueryEndpoints();
    }

    /// <summary>Gets the vendor interface path this session opened, distinct from the discovery device path.
    /// <para>本会话打开的厂商接口路径；与发现的设备路径不同。</para></summary>
    public string InterfacePath { get; }

    /// <summary>
    /// Opens the AWUSB device interface a legacy Allwinner driver binding publishes for this endpoint, or
    /// returns <see langword="null"/> when the device carries no such interface (a WinUSB or libusb binding
    /// must serve it instead).
    /// <para>打开旧式全志驱动绑定为该端点公布的 AWUSB 设备接口；设备没有该接口（由 WinUSB 或 libusb 绑定
    /// 提供服务）时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="usb">The discovered USB details, whose device path anchors the instance id and whose
    /// identity fields feed the session. <para>发现阶段的 USB 详细信息；其设备路径锚定实例标识，身份字段
    /// 填入会话。</para></param>
    /// <returns>The opened session, or <see langword="null"/> when no AWUSB interface exists or the handle
    /// cannot be created. <para>已打开的会话；不存在 AWUSB 接口或无法创建句柄时为 <see langword="null"/>。</para></returns>
    public static AwUsbDevice? TryOpen(UsbEndpointDetails usb)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var deviceId = TryDeriveDeviceInstanceId(usb.Path);
        if (deviceId is null)
        {
            return null;
        }

        var interfacePath = UsbPnpNative.FindDeviceInterfacePath(InterfaceGuid, deviceId);
        if (interfacePath is null)
        {
            return null;
        }

        var nativeHandle = Win32API.SimpleCreateHandle(interfacePath);
        if (nativeHandle == Win32API.INVALID_HANDLE_VALUE || nativeHandle == IntPtr.Zero)
        {
            UsbTrace.LogFormatted(
                $"AWUSB: open of [{interfacePath}] failed with native error {Marshal.GetLastWin32Error()}.");
            return null;
        }

        return new AwUsbDevice(new SafeFileHandle(nativeHandle, ownsHandle: true), usb, interfacePath, deviceId);
    }

    private static string? TryDeriveDeviceInstanceId(string devicePath)
    {
        // The discovery path names the USB-device interface of the same devnode:
        // \\?\usb#vid_1f3a&pid_efe8#serial#{device-interface-guid} -> USB\VID_1F3A&PID_EFE8\serial.
        // 发现路径指向同一设备节点的 USB 设备接口：
        // \\?\usb#vid_1f3a&pid_efe8#serial#{device-interface-guid} -> USB\VID_1F3A&PID_EFE8\serial。
        if (devicePath.Length < 4 || !devicePath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return null;
        }

        var body = devicePath[4..];
        var brace = body.IndexOf('{');
        if (brace >= 0)
        {
            body = body[..brace];
        }

        if (!body.EndsWith('#'))
        {
            return null;
        }

        var converted = body[..^1].Replace('#', '\\');
        return converted.Length > 0 ? converted.ToUpperInvariant() : null;
    }

    private static string? ExtractSerial(string devicePath)
    {
        var body = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var brace = body.IndexOf('{');
        if (brace >= 0)
        {
            body = body[..brace];
        }

        var segments = body.Split('#');
        return segments.Length >= 3 ? segments[2] : null;
    }

    /// <summary>
    /// Queries the bulk pipe addresses the driver bound: sub-command 2 copies one endpoint record into the
    /// input buffer's kernel mapping, and the record's endpoint address byte separates IN from OUT. The first
    /// bulk pair wins; a failed query leaves the addresses unset, which the FEL protocol tolerates.
    /// <para>查询驱动绑定的批量管道地址：子命令 2 把一条端点记录复制进输入缓冲的内核映射，记录中的端点地址
    /// 字节区分 IN 与 OUT。取第一对批量端点；查询失败时保留未设置，FEL 协议对此不敏感。</para>
    /// </summary>
    private (byte Out, byte In) QueryEndpoints()
    {
        byte output = 0;
        byte input = 0;
        for (uint index = 0; index < 8; index++)
        {
            var request = new byte[QueryBufferSize];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request, 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(QueryIndexOffset), index);
            var mapped = Marshal.AllocHGlobal(QueryBufferSize);
            try
            {
                Marshal.Copy(request, 0, mapped, QueryBufferSize);
                if (!Win32API.DeviceIoControl(_handle.DangerousGetHandle(), QueryControlCode, mapped,
                    QueryBufferSize, mapped, QueryBufferSize, out _, IntPtr.Zero))
                {
                    break;
                }

                var record = new byte[QueryBufferSize];
                Marshal.Copy(mapped, record, 0, QueryBufferSize);
                var address = record[QueryEndpointAddressOffset];
                var pipeType = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    record.AsSpan(QueryPipeTypeOffset));
                if (pipeType != BulkPipeType)
                {
                    continue;
                }

                if ((address & 0x80) != 0 && input == 0)
                {
                    input = address;
                }
                else if ((address & 0x80) == 0 && output == 0)
                {
                    output = address;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(mapped);
            }
        }

        return (output, input);
    }

    /// <summary>Builds one cleared overlapped request with a signaled event at <c>OVERLAPPED.HEvent</c>.
    /// <para>构造一条已清零的重叠请求，在 <c>OVERLAPPED.HEvent</c> 处放入已创建事件。</para></summary>
    private static IntPtr BeginOverlapped(out IntPtr completionEvent)
    {
        var overlapped = Marshal.AllocHGlobal(OverlappedSize);
        for (var offset = 0; offset < OverlappedSize; offset += sizeof(long))
        {
            Marshal.WriteIntPtr(overlapped, offset, IntPtr.Zero);
        }

        completionEvent = Win32API.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        Marshal.WriteIntPtr(overlapped, OverlappedEventOffset, completionEvent);
        return overlapped;
    }

    /// <summary>Waits for one overlapped request and classifies the outcome; a timed-out request is cancelled
    /// and reported as fatal because the legacy driver cannot retire abandoned IRPs.
    /// <para>等待一条重叠请求并分类结果；超时请求会被取消并按致命上报，因为旧驱动无法回收被放弃的 IRP。</para></summary>
    private static (bool Completed, uint Transferred, int NativeError) AwaitOverlapped(SafeFileHandle handle,
        IntPtr overlapped, IntPtr completionEvent, int timeoutMs)
    {
        if (Win32API.WaitForSingleObject(completionEvent, (uint)timeoutMs) != WaitObjectZero)
        {
            Win32API.CancelIoEx(handle.DangerousGetHandle(), overlapped);
            UsbTrace.LogFormatted($"AWUSB: a bulk transfer timed out; the legacy driver keeps the IRP pending until the device answers or the handle closes.");
            return (false, 0, ErrorGenFailure);
        }

        var overlappedValue = new Win32API.OVERLAPPED
        {
            Internal = Marshal.ReadIntPtr(overlapped),
            InternalHigh = Marshal.ReadIntPtr(overlapped, 8),
            OffsetLow = (uint)Marshal.ReadInt32(overlapped, 16),
            OffsetHigh = (uint)Marshal.ReadInt32(overlapped, 20),
            HEvent = Marshal.ReadIntPtr(overlapped, OverlappedEventOffset),
        };
        if (!Win32API.GetOverlappedResult(handle.DangerousGetHandle(), ref overlappedValue,
            out var transferred, false))
        {
            return (false, 0, Marshal.GetLastWin32Error());
        }

        return (true, transferred, 0);
    }

    public override byte[] Read(int length)
    {
        if (length <= 0)
        {
            return [];
        }

        var data = new byte[length];
        var read = ReadPacket(data, 0, length, DefaultTimeoutMs).Count;
        return read == length ? data : data[..read];
    }

    public override long Write(byte[] data, int length)
    {
        ValidateWriteData(data, length);
        return Write(data, 0, length, DefaultTimeoutMs);
    }

    protected override string BackendName => "awusb";

    protected override bool IsOpen => !_handle.IsInvalid && !_handle.IsClosed;

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return UsbChunkResult.Success(0);
        }

        var overlapped = BeginOverlapped(out var completionEvent);
        try
        {
            Win32API.DeviceIoControl(_handle.DangerousGetHandle(), BulkInControlCode, buffer, (uint)length,
                buffer, (uint)length, out _, overlapped);
            var error = Marshal.GetLastWin32Error();
            if (error != 0 && error != Win32API.ERROR_IO_PENDING)
            {
                return UsbChunkResult.Fatal(error);
            }

            var (completed, transferred, nativeError) = AwaitOverlapped(_handle, overlapped, completionEvent,
                timeoutMs);
            return completed
                ? UsbChunkResult.Success((int)transferred)
                : UsbChunkResult.Fatal(nativeError);
        }
        finally
        {
            Win32API.CloseHandle(completionEvent);
            Marshal.FreeHGlobal(overlapped);
        }
    }

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return UsbChunkResult.Success(0);
        }

        var overlapped = BeginOverlapped(out var completionEvent);
        try
        {
            Win32API.DeviceIoControl(_handle.DangerousGetHandle(), BulkOutControlCode, IntPtr.Zero, 0,
                buffer, (uint)length, out _, overlapped);
            var error = Marshal.GetLastWin32Error();
            if (error != 0 && error != Win32API.ERROR_IO_PENDING)
            {
                return UsbChunkResult.Fatal(error);
            }

            var (completed, transferred, nativeError) = AwaitOverlapped(_handle, overlapped, completionEvent,
                timeoutMs);
            return completed
                ? UsbChunkResult.Success((int)transferred)
                : UsbChunkResult.Fatal(nativeError);
        }
        finally
        {
            Win32API.CloseHandle(completionEvent);
            Marshal.FreeHGlobal(overlapped);
        }
    }

    protected override bool IsDisconnectionError(int nativeError)
        => nativeError is ErrorDeviceNotConnected or ErrorNoSuchDevice or ErrorDeviceRemoved;

    public override int GetSerialNumber() => 0;

    public override int CreateHandle() => 0;

    public override void Reset()
    {
    }

    public override void Dispose() => _handle.Dispose();
}