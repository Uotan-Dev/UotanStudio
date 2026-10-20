using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>
/// The libusb topology backend: rebuilds a bus → port → device hierarchy from bus numbers and port
/// chains, and takes interfaces from the active configuration descriptor. libusb exposes no driver
/// binding, device-interface class GUIDs or problem codes, so cross-platform consistency wins over
/// diagnostic detail here. This class is the only native layer: it reuses the runtime probe of
/// <see cref="LibUsbFinder"/> and the <c>UsbContext.List()</c> device list and reads only the device and
/// configuration descriptors the list already caches; it never opens, claims, resets or configures a
/// device, so string descriptors (product name, serial) stay <see langword="null"/>, and the topology
/// shaping itself lives entirely in the pure <see cref="LibUsbTopologyGraph"/>.
/// <para>libusb 拓扑后端：由总线号与端口链重建 总线 → 端口 → 设备 层级，并从活动配置描述符取接口。
/// libusb 不提供驱动绑定、设备接口类 GUID 或故障码，跨平台一致性优先于诊断细节。本类是唯一的原生层：
/// 复用 <see cref="LibUsbFinder"/> 的运行时探测与 <c>UsbContext.List()</c> 设备清单，只读取列表已缓存的
/// 设备/配置描述符；不打开、不声明接口、不复位、不切换配置，因此字符串描述符（产品名、序列号）保持
/// <see langword="null"/>，拓扑形状本身完全交给纯的 <see cref="LibUsbTopologyGraph"/>。</para>
/// </summary>
internal sealed class LibUsbTopologyProvider : IUsbTopologyProvider
{
    /// <summary>The shared instance. <para>共享实例。</para></summary>
    /// <value>The singleton provider. <para>单例提供程序。</para></value>
    public static LibUsbTopologyProvider Instance { get; } = new();

    /// <inheritdoc />
    public UsbTopologyCapabilities Capabilities { get; } = new()
    {
        Backend = UsbTopologyBackendKind.LibUsb,
        SupportsDriverInfo = false,
        SupportsInterfaceNodes = true,
        SupportsHostControllerNodes = false,
        SupportsDeviceInterfaceGuids = false,
        SupportsProblemCodes = false
    };

    /// <inheritdoc />
    /// <remarks>
    /// Availability is the LibUsbDotNet runtime probe of <see cref="LibUsbFinder.IsRuntimeAvailable"/>:
    /// the native library must be next to the application or registered explicitly, and
    /// <c>libusb_init</c> must succeed.
    /// <para>可用性即 <see cref="LibUsbFinder.IsRuntimeAvailable"/> 的 LibUsbDotNet 运行时探测：原生库必须
    /// 位于程序目录或已显式注册，且 <c>libusb_init</c> 必须成功。</para>
    /// </remarks>
    public bool IsAvailable => LibUsbFinder.IsRuntimeAvailable(out _);

    /// <inheritdoc />
    public IReadOnlyList<UsbTopologyNode> ReadFlatNodes()
    {
        if (!IsAvailable)
        {
            return [];
        }

        var devices = new List<LibUsbTopologyDevice>();
        try
        {
            using var context = new UsbContext();
            using var deviceList = context.List();
            foreach (var device in deviceList)
            {
                if (device is not LibUsbDotNet.LibUsb.UsbDevice libUsbDevice)
                {
                    continue;
                }

                var mapped = MapDevice(libUsbDevice);
                if (mapped is not null)
                {
                    devices.Add(mapped);
                }
            }
        }
        catch (Exception error)
        {
            UsbTrace.Log($"LibUsbTopologyProvider: device list unavailable: {error.GetType().Name}.");
            return [];
        }

        return LibUsbTopologyGraph.Build(devices);
    }

    /// <summary>
    /// Maps one closed LibUsbDotNet device into the pure graph record, degrading every descriptor field
    /// that cannot be read without opening the device to <see langword="null"/> or an empty list.
    /// <para>把一个未打开的 LibUsbDotNet 设备映射为纯图记录；所有不打开设备就读不到的描述符字段都降级为
    /// <see langword="null"/> 或空列表。</para>
    /// </summary>
    /// <param name="device">
    /// The listed device. <para>清单中的设备。</para>
    /// </param>
    /// <returns>
    /// The record, or <see langword="null"/> when even the fixed descriptor cannot be read.
    /// <para>记录；连固定描述符都读不到时为 <see langword="null"/>。</para>
    /// </returns>
    private static LibUsbTopologyDevice? MapDevice(LibUsbDotNet.LibUsb.UsbDevice device)
    {
        try
        {
            return new LibUsbTopologyDevice
            {
                BusNumber = device.BusNumber,
                PortChain = ObservePortChain(device),
                VendorId = device.VendorId,
                ProductId = device.ProductId,
                BcdDevice = ObserveBcdDevice(device),
                Serial = null,
                Speed = MapSpeed(device.Speed),
                Interfaces = ObserveInterfaces(device)
            };
        }
        catch (Exception error)
        {
            UsbTrace.Log($"LibUsbTopologyProvider: skipped a device ({device.VendorId:X4}:" +
                $"{device.ProductId:X4}): {error.GetType().Name}.");
            return null;
        }
    }

    /// <summary>
    /// Reads the root-to-device port chain, degrading to an empty chain when the runtime cannot report
    /// port numbers; the graph builder then falls back to a bus-root-parented identity.
    /// <para>读取从根到设备的端口链；运行时无法报告端口号时降级为空链，由图构建器回退为挂在总线根下的标识。</para>
    /// </summary>
    private static IReadOnlyList<byte> ObservePortChain(LibUsbDotNet.LibUsb.UsbDevice device)
    {
        try
        {
            return [.. device.PortNumbers];
        }
        catch (Exception error)
        {
            UsbTrace.Log($"LibUsbTopologyProvider: port chain unavailable: {error.GetType().Name}.");
            return [];
        }
    }

    /// <summary>
    /// Reads bcdDevice from the cached device descriptor information without opening the device.
    /// <para>从不打开设备的缓存设备描述符信息中读取 bcdDevice。</para>
    /// </summary>
    private static ushort? ObserveBcdDevice(LibUsbDotNet.LibUsb.UsbDevice device)
    {
        try
        {
            return device.Descriptor?.Device;
        }
        catch (Exception error)
        {
            UsbTrace.Log($"LibUsbTopologyProvider: device descriptor unavailable: {error.GetType().Name}.");
            return null;
        }
    }

    /// <summary>
    /// Reads the interface triple of the active configuration from the descriptors the library already
    /// cached; a device whose active configuration cannot be inspected simply yields no function nodes.
    /// <para>从库已缓存的描述符中读取活动配置的接口三元组；无法查看活动配置的设备只是不产出功能节点。</para>
    /// </summary>
    private static IReadOnlyList<LibUsbTopologyInterface> ObserveInterfaces(
        LibUsbDotNet.LibUsb.UsbDevice device)
    {
        try
        {
            var configuration = device.ActiveConfigDescriptor;
            if (configuration is null)
            {
                return [];
            }

            var interfaces = new List<LibUsbTopologyInterface>();
            foreach (var iface in configuration.Interfaces)
            {
                interfaces.Add(new LibUsbTopologyInterface
                {
                    Number = (byte)iface.Number,
                    Class = (byte)iface.Class,
                    SubClass = iface.SubClass,
                    Protocol = iface.Protocol
                });
            }

            return interfaces;
        }
        catch (Exception error)
        {
            UsbTrace.Log($"LibUsbTopologyProvider: active configuration unavailable: " +
                $"{error.GetType().Name}.");
            return [];
        }
    }

    /// <summary>
    /// Maps LibUsbDotNet's <see cref="Speed"/> onto <see cref="UsbDeviceSpeed"/>. LibUsbDotNet does not
    /// distinguish Super from SuperPlus, so both report as Super, matching the mapping used by
    /// <c>LibUsbFinder</c>.
    /// <para>将 LibUsbDotNet 的 <see cref="Speed"/> 映射到 <see cref="UsbDeviceSpeed"/>。LibUsbDotNet 不区分
    /// Super 与 SuperPlus，两者都报告为 Super，与 <c>LibUsbFinder</c> 的映射保持一致。</para>
    /// </summary>
    private static UsbDeviceSpeed MapSpeed(Speed speed) => speed switch
    {
        Speed.Low => UsbDeviceSpeed.Low,
        Speed.Full => UsbDeviceSpeed.Full,
        Speed.High => UsbDeviceSpeed.High,
        Speed.Super => UsbDeviceSpeed.Super,
        _ => UsbDeviceSpeed.Unknown
    };
}