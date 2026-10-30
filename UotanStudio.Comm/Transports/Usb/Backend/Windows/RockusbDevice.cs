using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Drives a Rockchip boot ROM port whose interface is claimed by Rockchip's own <c>rockusb.sys</c> driver instead of
/// WinUSB. The driver is a bulk-only pass-through with three IOCTLs: an identity query that returns the device's
/// configuration descriptor, one vendor-request entry per loader request index (<c>0x0471</c> and <c>0x0472</c>), and a
/// pipe reset; bulk traffic goes through plain <see cref="ReadFile"/> and <see cref="WriteFile"/> because the driver,
/// not the caller, owns endpoint selection.
/// <para>驱动接口被瑞芯微自带 <c>rockusb.sys</c> 而非 WinUSB 占用的 boot ROM 端口。该驱动是仅批量传输的直通驱动，
/// 提供三个 IOCTL：返回设备配置描述符的身份查询、每个 loader 请求索引（<c>0x0471</c> 与 <c>0x0472</c>）各一个的厂商
/// 请求入口，以及一次管道复位；批量读写走普通的 <see cref="ReadFile"/> 与 <see cref="WriteFile"/>，因为端点选择
/// 由驱动而非调用方拥有。</para>
/// </summary>
internal sealed class RockusbDevice : UsbDevice
{
    private const int IoTimeoutMs = 30000;
    private const int ConfigurationDescriptorBufferBytes = 4096;
    private const uint IoGetConfigurationCode = 0x00220000;
    private const uint IoVendorRequest471Code = 0x8000A000;
    private const uint IoVendorRequest472Code = 0x8000A004;
    private const uint IoResetPipesCode = 0x8000A008;
    private const byte MassStorageInterfaceClass = 0x08;
    private const byte TransferTypeMask = 0x03;
    private const byte BulkTransferType = 0x02;
    private const byte VendorRequestType = 0x40;
    private const byte VendorRequestDownloadBoot = 0x0C;
    private const ushort WIndex471 = 0x0471;
    private const ushort WIndex472 = 0x0472;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    private const int ERROR_NO_SUCH_DEVICE = 433;
    private const int ERROR_DEVICE_REMOVED = 1617;

    private SafeFileHandle _fileHandle = new(new IntPtr(-1), ownsHandle: true);
    private bool _disposed;
    private byte _endpointIn;
    private byte _endpointOut;

    /// <summary>
    /// Gets the native handle the driver IOCTLs run on, or <see cref="INVALID_HANDLE_VALUE"/> when the device is closed.
    /// <para>获取驱动 IOCTL 使用的原生句柄；设备已关闭时为 <see cref="INVALID_HANDLE_VALUE"/>。</para>
    /// </summary>
    public IntPtr Handle => _fileHandle.IsInvalid ? INVALID_HANDLE_VALUE : _fileHandle.DangerousGetHandle();

    /// <inheritdoc/>
    public override byte EndpointIn => _endpointIn;

    /// <inheritdoc/>
    public override byte EndpointOut => _endpointOut;

    /// <summary>
    /// Gets or sets the session options the discovery path requested for this device.
    /// <para>获取或设置发现路径为该设备请求的会话选项。</para>
    /// </summary>
    internal UsbSessionOptions? SessionOptions { get; set; }

    /// <inheritdoc/>
    public override int DefaultTimeoutMs => SessionOptions?.DefaultTimeoutMs ?? IoTimeoutMs;

    /// <summary>
    /// Opens the interface and reads the configuration descriptor the driver caches, which is the only way a caller
    /// can learn the interface class and the bulk endpoint addresses while <c>rockusb.sys</c> owns the port.
    /// <para>打开接口并读取驱动缓存的配置描述符：在 <c>rockusb.sys</c> 占用端口时，这是调用方了解接口类与批量端点地址
    /// 的唯一途径。</para>
    /// </summary>
    /// <returns><c>0</c> on success, otherwise the <see cref="Marshal.GetLastWin32Error"/> value of the failing step.
    /// <para>成功时为 <c>0</c>，否则为失败步骤的 <see cref="Marshal.GetLastWin32Error"/> 取值。</para></returns>
    public override int CreateHandle()
    {
        _fileHandle = new SafeFileHandle(SimpleCreateHandle(DevicePath), ownsHandle: true);
        if (_fileHandle.IsInvalid)
        {
            return Marshal.GetLastWin32Error();
        }

        if (!ReadConfiguration())
        {
            var error = Marshal.GetLastWin32Error();
            _fileHandle.Dispose();
            return error == 0 ? -1 : error;
        }

        return 0;
    }

    /// <summary>
    /// Queries the driver for the device's configuration descriptor and records the interface it exposes.
    /// <para>向驱动查询设备的配置描述符，并记录其暴露的接口。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the descriptor was read; <see langword="false"/> otherwise.
    /// <para>读到描述符时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    private bool ReadConfiguration()
    {
        var buffer = new byte[ConfigurationDescriptorBufferBytes];
        if (!DeviceIoControl(_fileHandle.DangerousGetHandle(), IoGetConfigurationCode, null, 0, buffer,
                (uint)buffer.Length, out var returned, IntPtr.Zero))
        {
            return false;
        }

        var length = (int)Math.Min(returned, (uint)buffer.Length);
        var interfaces = UsbConfigurationDescriptorParser.Parse(buffer.AsSpan(0, length));
        ApplyConfiguration(interfaces);
        if (CommTrace.IsEnabled(CommLogLevel.Debug))
        {
            CommTrace.Debug("usb",
                $"opened rockusb {DevicePath}: interface {InterfaceClass:X2}/{InterfaceSubClass:X2}/{InterfaceProtocol:X2}, " +
                $"bulk in 0x{_endpointIn:X2}/out 0x{_endpointOut:X2}; {interfaces.Count} interface(s) reported");
        }

        return true;
    }

    /// <summary>
    /// Selects the interface whose bulk pair the driver bound and publishes its endpoints and class.
    /// <para>选出驱动绑定的、带成对批量端点的接口，并公布其端点与接口类。</para>
    /// </summary>
    /// <param name="interfaces">Interfaces parsed from the configuration descriptor.
    /// <para>从配置描述符解析出的接口。</para></param>
    private void ApplyConfiguration(List<UsbInterfaceInfo> interfaces)
    {
        EnumeratedInterfaces = interfaces;
        if (interfaces.Count == 0)
        {
            return;
        }

        UsbInterfaceInfo? bound = null;
        UsbEndpointInfo? bulkIn = null;
        UsbEndpointInfo? bulkOut = null;
        foreach (var candidate in interfaces)
        {
            var candidateIn = FindBulkEndpoint(candidate, isIn: true);
            var candidateOut = FindBulkEndpoint(candidate, isIn: false);
            if (candidateIn is null || candidateOut is null)
            {
                continue;
            }

            bound = candidate;
            bulkIn = candidateIn;
            bulkOut = candidateOut;
            break;
        }

        if (bound is null)
        {
            foreach (var candidate in interfaces)
            {
                if (candidate.Class != MassStorageInterfaceClass)
                {
                    continue;
                }

                bound = candidate;
                break;
            }

            bound ??= interfaces[0];
            bulkIn = FindBulkEndpoint(bound, isIn: true);
            bulkOut = FindBulkEndpoint(bound, isIn: false);
        }

        Interfaces =
        [
            new UsbInterfaceInfo
            {
                InterfaceNumber = bound.InterfaceNumber,
                Class = bound.Class,
                SubClass = bound.SubClass,
                Protocol = bound.Protocol,
                Endpoints = bound.Endpoints
            }
        ];
        InterfaceClass = bound.Class;
        InterfaceSubClass = bound.SubClass;
        InterfaceProtocol = bound.Protocol;
        InterfaceMetadataObserved = true;
        _endpointIn = bulkIn?.EndpointAddress ?? 0;
        _endpointOut = bulkOut?.EndpointAddress ?? 0;
    }

    /// <summary>
    /// Returns the first bulk endpoint of one interface in the requested direction.
    /// <para>返回某接口在指定方向上的第一个批量端点。</para>
    /// </summary>
    /// <param name="interfaceInfo">Interface to search. <para>要搜索的接口。</para></param>
    /// <param name="isIn">Whether a device-to-host endpoint is wanted.
    /// <para>是否要设备到主机的端点。</para></param>
    /// <returns>The matching endpoint, or <see langword="null"/> when the interface has none.
    /// <para>匹配的端点；该接口没有时为 <see langword="null"/>。</para></returns>
    private static UsbEndpointInfo? FindBulkEndpoint(UsbInterfaceInfo interfaceInfo, bool isIn)
    {
        foreach (var endpoint in interfaceInfo.Endpoints)
        {
            if ((endpoint.Attributes & TransferTypeMask) != BulkTransferType || endpoint.IsIn != isIn)
            {
                continue;
            }

            return endpoint;
        }

        return null;
    }

    /// <summary>
    /// Reports no serial number: <c>rockusb.sys</c> exposes no serial IOCTL, so the caller must take the serial from
    /// Windows' device instance id instead of guessing one here.
    /// <para>不报告序列号：<c>rockusb.sys</c> 没有序列号 IOCTL，调用方应从 Windows 的设备实例 ID 取值，而不是在此猜测。</para>
    /// </summary>
    /// <returns>Always <c>0</c>. <para>恒为 <c>0</c>。</para></returns>
    public override int GetSerialNumber() => 0;

    /// <summary>
    /// Sends one loader vendor request through the driver's per-index IOCTL. Only the boot download request the RKDL
    /// loader handshake uses is supported: the driver exposes <c>0x0471</c> and <c>0x0472</c> as host-to-device
    /// requests with the payload as the IOCTL input buffer, and offers no vendor IN transfer at all.
    /// <para>通过驱动的按索引 IOCTL 发送一个 loader 厂商请求。仅支持 RKDL loader 握手使用的下载请求：驱动把
    /// <c>0x0471</c> 与 <c>0x0472</c> 暴露为主机到设备的请求，负载作为 IOCTL 输入缓冲区，且完全不提供厂商 IN 传输。</para>
    /// </summary>
    /// <param name="setupPacket">The request to send. <para>要发送的请求。</para></param>
    /// <param name="buffer">Payload bytes, or <see langword="null"/> for a zero-length request.
    /// <para>负载字节；零长请求时为 <see langword="null"/>。</para></param>
    /// <param name="offset">Offset into <paramref name="buffer"/>. <para><paramref name="buffer"/> 内的偏移。</para></param>
    /// <param name="length">Payload byte count. <para>负载字节数。</para></param>
    /// <param name="timeoutMs">Ignored: the driver enforces its own five-second control timeout.
    /// <para>忽略：驱动自行执行五秒的控制传输超时。</para></param>
    /// <returns>The number of payload bytes the driver reports as transferred.
    /// <para>驱动报告的已传输负载字节数。</para></returns>
    /// <exception cref="UsbDeviceHandleClosedException">The device is closed.
    /// <para>设备已关闭。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is non-zero while <paramref name="buffer"/>
    /// is <see langword="null"/>, or the range exceeds <paramref name="buffer"/>.
    /// <para><paramref name="buffer"/> 为 <see langword="null"/> 而 <paramref name="length"/> 非零，或范围超出
    /// <paramref name="buffer"/>。</para></exception>
    /// <exception cref="NotSupportedException">The request is not one the driver exposes.
    /// <para>该请求不在驱动提供的范围内。</para></exception>
    /// <exception cref="Win32Exception">The driver rejected the request.
    /// <para>驱动拒绝了该请求。</para></exception>
    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_disposed || _fileHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        if (buffer == null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(length, 0);
        }
        else
        {
            ValidateBufferRange(buffer, offset, length);
        }

        if (buffer == null || setupPacket.RequestType != VendorRequestType ||
            setupPacket.Request != VendorRequestDownloadBoot || setupPacket.Value != 0)
        {
            throw new NotSupportedException(
                $"rockusb.sys exposes only host-to-device boot download requests (0x40/0x{VendorRequestDownloadBoot:X2}, wValue 0); " +
                $"request 0x{setupPacket.RequestType:X2}/0x{setupPacket.Request:X2} with wValue 0x{setupPacket.Value:X4} is unavailable.");
        }

        var ioControlCode = setupPacket.Index switch
        {
            WIndex471 => IoVendorRequest471Code,
            WIndex472 => IoVendorRequest472Code,
            _ => 0u
        };
        if (ioControlCode == 0)
        {
            throw new NotSupportedException(
                $"rockusb.sys forwards vendor requests for wIndex 0x{WIndex471:X4} and 0x{WIndex472:X4} only; " +
                $"wIndex 0x{setupPacket.Index:X4} is unavailable.");
        }

        var payload = length > 0 ? Marshal.AllocHGlobal(length) : IntPtr.Zero;
        try
        {
            if (length > 0)
            {
                Marshal.Copy(buffer, offset, payload, length);
            }

            if (!DeviceIoControl(_fileHandle.DangerousGetHandle(), ioControlCode, payload, (uint)length, null, 0,
                    out var returned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return (int)Math.Min(returned, (uint)length);
        }
        finally
        {
            if (payload != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(payload);
            }
        }
    }

    /// <inheritdoc/>
    public override byte[] Read(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var buffer = new byte[length];
        var count = ReadInto(buffer, 0, length, IoTimeoutMs);
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

    /// <inheritdoc/>
    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, IoTimeoutMs);

    /// <summary>
    /// Resets the driver's pipes so a stalled bulk transfer does not poison the next one.
    /// <para>复位驱动的管道，避免一次停住的批量传输影响下一次传输。</para>
    /// </summary>
    public override void Reset()
    {
        if (_disposed || _fileHandle.IsInvalid)
        {
            return;
        }

        if (!DeviceIoControl(_fileHandle.DangerousGetHandle(), IoResetPipesCode, null, 0, null, 0, out _,
                IntPtr.Zero))
        {
            UsbTrace.LogFormatted($"Rockusb: pipe reset on {DevicePath} failed err={Marshal.GetLastWin32Error()}.");
        }
    }

    /// <inheritdoc/>
    protected override string BackendName => "rockusb";

    /// <inheritdoc/>
    protected override bool IsOpen => !_disposed && !_fileHandle.IsInvalid;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => !_disposed && !_fileHandle.IsInvalid;

    /// <inheritdoc/>
    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        return !ReadFile(_fileHandle.DangerousGetHandle(), buffer, (uint)length, out var bytesRead, IntPtr.Zero)
            ? UsbChunkResult.Fatal(Marshal.GetLastWin32Error())
            : UsbChunkResult.Success((int)bytesRead);
    }

    /// <inheritdoc/>
    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        return !WriteFile(_fileHandle.DangerousGetHandle(), buffer, (uint)length, out var written, IntPtr.Zero)
            ? UsbChunkResult.Fatal(Marshal.GetLastWin32Error())
            : UsbChunkResult.Success((int)written);
    }

    /// <inheritdoc/>
    protected override bool IsDisconnectionError(int nativeError)
        => nativeError is ERROR_DEVICE_NOT_CONNECTED or ERROR_NO_SUCH_DEVICE or ERROR_DEVICE_REMOVED;

    /// <inheritdoc/>
    protected override Exception CreateReadFatalException(int nativeError)
        => IsDisconnectionError(nativeError) ? base.CreateReadFatalException(nativeError) : new Win32Exception(nativeError);

    /// <inheritdoc/>
    protected override Exception CreateWriteFatalException(int nativeError)
        => IsDisconnectionError(nativeError) ? base.CreateWriteFatalException(nativeError) : new Win32Exception(nativeError);

    /// <inheritdoc/>
    public override long Write(byte[] data, int length) => Write(data, length, IoTimeoutMs);

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_fileHandle.IsInvalid)
        {
            _fileHandle.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
