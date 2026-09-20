#if ANDROID
using System.Runtime.InteropServices;
using Android.Hardware.Usb;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.Android;

/// <summary>
/// Android USB Host backend device backed by android.hardware.usb.UsbDeviceConnection.
/// <para>基于 android.hardware.usb.UsbDeviceConnection 的 Android USB Host 后端设备。</para>
/// The host device (the phone running this app) talks to a peer gadget over an OTG cable
/// without root: UsbManager enumerates, the user grants permission per device, and
/// bulk/control transfers run through the framework API.
/// <para>主机设备（运行本应用的手机）通过 OTG 线缆以免 root 方式与对端 gadget 通信：
/// UsbManager 负责枚举，用户按设备授权，批量/控制传输走框架 API。</para>
/// </summary>
internal sealed class AndroidUsbDevice : UsbDevice
{
    // Android's UsbDeviceConnection.bulkTransfer is unreliable beyond 16 KiB per call
    // on many framework versions; chunk at a conservative size.
    // <para>多数框架版本上 bulkTransfer 单次超过 16 KiB 不可靠；按保守值分块。</para>
    private const int AndroidMaxChunkSize = 16384;

    private readonly UsbManager _manager;
    private readonly UsbDevice _device;
    private UsbDeviceConnection? _connection;
    private readonly List<UsbInterface> _claimedInterfaces = [];
    private UsbEndpoint? _epIn;
    private UsbEndpoint? _epOut;

    public override byte EndpointIn => _epIn?.EndpointAddress ?? 0;
    public override byte EndpointOut => _epOut?.EndpointAddress ?? 0;

    protected override string BackendName => "AndroidUsb";

    protected override int MaxChunkSize => AndroidMaxChunkSize;

    protected override bool IsOpen => _connection is not null;

    public AndroidUsbDevice(UsbManager manager, UsbDevice device, byte? interfaceNumber)
    {
        _manager = manager;
        _device = device;

        VendorId = (ushort)device.VendorId;
        ProductId = (ushort)device.ProductId;
        SerialNumber = device.SerialNumber;
        // Android does not expose a sysfs path; the device id is the stable identity.
        // <para>Android 不暴露 sysfs 路径；设备 id 即稳定身份。</para>
        DevicePath = $"android://usb/{device.DeviceId}";
        UsbDeviceType = UsbDeviceType.Android;

        var interfaces = new List<UsbInterfaceInfo>(device.InterfaceCount);
        for (var i = 0; i < device.InterfaceCount; i++)
        {
            var iface = device.GetInterface(i);
            var endpoints = new List<UsbEndpointInfo>(iface.EndpointCount);
            for (var e = 0; e < iface.EndpointCount; e++)
            {
                var ep = iface.GetEndpoint(e);
                endpoints.Add(new UsbEndpointInfo
                {
                    EndpointAddress = (byte)ep.EndpointAddress,
                    Attributes = (byte)ep.Attributes,
                    MaxPacketSize = (ushort)ep.MaxPacketSize,
                    Interval = (byte)ep.Interval
                });
            }

            interfaces.Add(new UsbInterfaceInfo
            {
                InterfaceNumber = (byte)iface.Id,
                Class = (byte)iface.InterfaceClass,
                SubClass = (byte)iface.InterfaceSubclass,
                Protocol = (byte)iface.InterfaceProtocol,
                Endpoints = endpoints
            });

            // Bind the requested interface, or the first interface exposing a bulk pair.
            // <para>绑定请求的接口，或第一个暴露批量端点对的接口。</para>
            if (InterfaceMetadataObserved)
            {
                continue;
            }

            var selected = interfaceNumber is null
                ? endpoints.Any(ep => ep.Attributes == 2 && ep.IsIn) && endpoints.Any(ep => ep.Attributes == 2 && !ep.IsIn)
                : iface.Id == interfaceNumber.Value;
            if (!selected)
            {
                continue;
            }

            InterfaceMetadataObserved = true;
            InterfaceClass = (byte)iface.InterfaceClass;
            InterfaceSubClass = (byte)iface.InterfaceSubclass;
            InterfaceProtocol = (byte)iface.InterfaceProtocol;
            _epIn = endpoints.FirstOrDefault(ep => ep.Attributes == 2 && ep.IsIn) is { } inInfo
                ? iface.GetEndpoint(endpoints.IndexOf(inInfo))
                : null;
            _epOut = endpoints.FirstOrDefault(ep => ep.Attributes == 2 && !ep.IsIn) is { } outInfo
                ? iface.GetEndpoint(endpoints.IndexOf(outInfo))
                : null;
        }

        Interfaces = interfaces;
    }

    public override int CreateHandle()
    {
        if (_connection is not null)
        {
            return 0;
        }

        var connection = _manager.OpenDevice(_device);
        if (connection is null)
        {
            // Missing permission or the device vanished between enumeration and open.
            // <para>缺少权限，或设备在枚举与打开之间消失。</para>
            return -1;
        }

        var primary = SelectPrimaryInterface();
        if (primary is not null)
        {
            if (!connection.ClaimInterface(primary, force: true))
            {
                connection.Close();
                return -1;
            }

            _claimedInterfaces.Add(primary);
        }

        _connection = connection;
        return 0;
    }

    private UsbInterface? SelectPrimaryInterface()
    {
        for (var i = 0; i < _device.InterfaceCount; i++)
        {
            var iface = _device.GetInterface(i);
            if (_epIn is null)
            {
                continue;
            }

            for (var e = 0; e < iface.EndpointCount; e++)
            {
                if (iface.GetEndpoint(e).EndpointAddress == _epIn.EndpointAddress)
                {
                    return iface;
                }
            }
        }

        return _device.InterfaceCount > 0 ? _device.GetInterface(0) : null;
    }

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (_connection is null || _epIn is null)
        {
            return UsbChunkResult.Fatal(-1);
        }

        // bulkTransfer cannot report errno: negative means timeout OR error, and the
        // framework does not distinguish them. Map to Timeout (retryable short-read
        // semantics) so upper-layer read loops behave like the desktop backends.
        // <para>bulkTransfer 无法报告 errno：负值兼指超时与错误且框架不做区分。
        // 映射为 Timeout（可重试的短读语义），使上层读循环与桌面后端行为一致。</para>
        var managed = new byte[length];
        var transferred = _connection.BulkTransfer(_epIn, managed, length, UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs));
        if (transferred < 0)
        {
            return UsbChunkResult.Timeout(-1);
        }

        if (transferred > 0)
        {
            Marshal.Copy(managed, 0, buffer, transferred);
        }

        return UsbChunkResult.Success(transferred);
    }

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (_connection is null || _epOut is null)
        {
            return UsbChunkResult.Fatal(-1);
        }

        var managed = new byte[length];
        Marshal.Copy(buffer, managed, 0, length);
        var transferred = _connection.BulkTransfer(_epOut, managed, length, UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs));
        if (transferred < 0)
        {
            return UsbChunkResult.Timeout(-1);
        }

        return UsbChunkResult.Success(transferred);
    }

    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_connection is null)
        {
            return -1;
        }

        UsbDevice.ValidateBufferRange(buffer!, offset, length);

        // Android's controlTransfer indexes into the buffer itself, so a non-zero
        // caller offset needs a copy into a fresh array.
        // <para>Android 的 controlTransfer 以缓冲区自身为基准索引，调用方偏移非零时需复制。</para>
        var payload = buffer is null ? [] : buffer[offset..(offset + length)];
        var transferred = _connection.ControlTransfer(
            setupPacket.RequestType,
            setupPacket.Request,
            setupPacket.Value,
            setupPacket.Index,
            payload,
            0,
            length,
            UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs));
        if (transferred > 0 && buffer is not null)
        {
            Buffer.BlockCopy(payload, 0, buffer, offset, transferred);
        }

        return transferred;
    }

    public override int GetSerialNumber() => 0;

    public override void Reset()
    {
        // UsbDeviceConnection exposes no port reset; a no-op keeps the session usable.
        // <para>UsbDeviceConnection 不提供端口复位；保持无操作以维持会话可用。</para>
    }

    public override void Dispose()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
        {
            return;
        }

        foreach (var iface in _claimedInterfaces)
        {
            try
            {
                connection.ReleaseInterface(iface);
            }
            catch
            {
                // Release failures during teardown must not mask the original outcome.
                // <para>释放阶段的失败不应掩盖原始结果。</para>
            }
        }

        _claimedInterfaces.Clear();
        connection.Close();
    }
}
#endif