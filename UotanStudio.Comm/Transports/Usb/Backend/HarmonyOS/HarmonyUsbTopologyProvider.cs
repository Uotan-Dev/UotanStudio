using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Providers;
using static UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS.HarmonyOSUsbDDK;

namespace UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;

/// <summary>
/// The HarmonyOS topology backend: yields device and interface nodes from the USB DDK device list. That
/// DDK exposes no parentage, driver binding or problem codes, so only a flat device/interface list is
/// possible here and the shared tree shows every node as a root. This class is the only native layer and
/// mirrors the discovery of <c>HarmonyOSUsbFinder</c>: it
/// probes device ids through <c>OH_Usb_GetDeviceDescriptor</c> and <c>OH_Usb_GetConfigDescriptor</c>
/// only, never claims an interface and never starts a transfer, leaving all shaping to the pure
/// <see cref="HarmonyTopologyGraph"/>. It honours the same environment gate as
/// <see cref="HarmonyOSUsbApiProvider"/> (<c>FIRMWAREKIT_USB_ENABLE_HARMONY</c>): until that gate is
/// satisfied <see cref="IsAvailable"/> reports <see langword="false"/> and nothing attempts to load
/// <c>libusb_ndk.z.so</c>.
/// <para>HarmonyOS 拓扑后端：由 USB DDK 的设备清单给出设备与接口节点。该 DDK 不暴露父子链、
/// 驱动绑定或故障码，因此这里只能提供扁平的设备/接口列表，共享树会把它们全部显示为根。</para>
/// <para>本类是唯一的原生层，镜像 <c>HarmonyOSUsbFinder</c> 的发现方式：只按设备 ID 探测
/// <c>OH_Usb_GetDeviceDescriptor</c> 与 <c>OH_Usb_GetConfigDescriptor</c>，不声明接口、不发起传输，
/// 塑形完全交给纯的 <see cref="HarmonyTopologyGraph"/>。与 <see cref="HarmonyOSUsbApiProvider"/> 使用同一
/// 环境变量门（<c>FIRMWAREKIT_USB_ENABLE_HARMONY</c>）：门未满足时 <see cref="IsAvailable"/> 为
/// <see langword="false"/>，默认不会尝试加载 <c>libusb_ndk.z.so</c>。</para>
/// </summary>
internal sealed class HarmonyUsbTopologyProvider : IUsbTopologyProvider
{
    /// <summary>The shared instance. <para>共享实例。</para></summary>
    /// <value>The singleton provider. <para>单例提供程序。</para></value>
    public static HarmonyUsbTopologyProvider Instance { get; } = new();

    /// <summary>
    /// Caches one opt-in gated DDK probe, mirroring the availability rule of
    /// <see cref="HarmonyOSUsbApiProvider"/> so both surfaces agree on when the DDK may be touched.
    /// <para>缓存一次带门控的 DDK 探测，与 <see cref="HarmonyOSUsbApiProvider"/> 的可用性规则一致，
    /// 使两个界面在同一时机才允许接触 DDK。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only when the environment gate is satisfied, the host looks like HarmonyOS
    /// and <c>OH_Usb_Init</c> succeeds.
    /// <para>仅当环境变量门满足、主机形似 HarmonyOS 且 <c>OH_Usb_Init</c> 成功时为 <see langword="true"/>。</para>
    /// </value>
    private static readonly Lazy<bool> DdkAvailable = new(() =>
    {
        if (!IsExplicitlyEnabled() || !IsHarmonyOSPlatform())
        {
            return false;
        }

        try
        {
            var ret = OH_Usb_Init();
            if (ret == USB_DDK_NO_ERROR)
            {
                OH_Usb_Release();
                return true;
            }

            return false;
        }
        catch (Exception error)
        {
            UsbTrace.Log($"HarmonyUsbTopologyProvider: DDK probe failed: {error.GetType().Name}.");
            return false;
        }
    }, isThreadSafe: true);

    /// <inheritdoc />
    public UsbTopologyCapabilities Capabilities { get; } = new()
    {
        Backend = UsbTopologyBackendKind.HarmonyOS,
        SupportsDriverInfo = false,
        SupportsInterfaceNodes = true,
        SupportsHostControllerNodes = false,
        SupportsDeviceInterfaceGuids = false,
        SupportsProblemCodes = false
    };

    /// <inheritdoc />
    public bool IsAvailable => DdkAvailable.Value;

    /// <inheritdoc />
    public IReadOnlyList<UsbTopologyNode> ReadFlatNodes()
    {
        if (!IsAvailable)
        {
            return [];
        }

        var descriptors = new List<HarmonyTopologyDescriptor>();
        var ret = OH_Usb_Init();
        if (ret != USB_DDK_NO_ERROR)
        {
            UsbTrace.Log($"HarmonyUsbTopologyProvider: OH_Usb_Init failed: {GetErrorMessage(ret)}.");
            return [];
        }

        try
        {
            var maxProbe = (ulong)Math.Max(0, HarmonyOSUsbFinder.MaxDeviceId);
            for (var deviceId = 1UL; deviceId <= maxProbe; deviceId++)
            {
                var descriptor = ProbeDevice(deviceId);
                if (descriptor is not null)
                {
                    descriptors.Add(descriptor);
                }
            }
        }
        finally
        {
            OH_Usb_Release();
        }

        return HarmonyTopologyGraph.Shape(descriptors);
    }

    /// <summary>
    /// Probes one device id read-only: the device descriptor first, then the configuration descriptor of
    /// index zero for the interface triples. A device whose configuration descriptor is unavailable still
    /// yields a descriptor record with an empty interface list.
    /// <para>只读探测单个设备 ID：先取设备描述符，再取索引 0 的配置描述符以获得接口三元组。配置描述符不可用的
    /// 设备仍产出接口列表为空的记录。</para>
    /// </summary>
    /// <param name="deviceId">
    /// The DDK device id under probe. <para>正在探测的 DDK 设备 ID。</para>
    /// </param>
    /// <returns>
    /// The descriptor record, or <see langword="null"/> when the device id holds no readable device.
    /// <para>描述符记录；该设备 ID 没有可读设备时为 <see langword="null"/>。</para>
    /// </returns>
    private static HarmonyTopologyDescriptor? ProbeDevice(ulong deviceId)
    {
        try
        {
            var deviceDescriptor = new UsbDeviceDescriptor();
            if (OH_Usb_GetDeviceDescriptor(deviceId, ref deviceDescriptor) != USB_DDK_NO_ERROR)
            {
                return null;
            }

            IReadOnlyList<HarmonyTopologyInterface> interfaces = [];
            var configPtr = IntPtr.Zero;
            if (OH_Usb_GetConfigDescriptor(deviceId, 0, ref configPtr) == USB_DDK_NO_ERROR &&
                configPtr != IntPtr.Zero)
            {
                try
                {
                    interfaces = ReadInterfaces(configPtr);
                }
                finally
                {
                    OH_Usb_FreeConfigDescriptor(configPtr);
                }
            }

            return new HarmonyTopologyDescriptor
            {
                DeviceId = deviceId,
                VendorId = deviceDescriptor.IdVendor,
                ProductId = deviceDescriptor.IdProduct,
                BcdDevice = deviceDescriptor.BcdDevice,
                BcdUsb = deviceDescriptor.BcdUSB,
                DeviceClass = deviceDescriptor.BDeviceClass,
                Serial = null,
                Interfaces = interfaces
            };
        }
        catch (Exception error)
        {
            UsbTrace.Log($"HarmonyUsbTopologyProvider: probe failed for device {deviceId}: " +
                $"{error.GetType().Name}.");
            return null;
        }
    }

    /// <summary>
    /// Reads the interface triples of the first alternate setting of every interface, mirroring
    /// <c>HarmonyOSUsbFinder</c> but skipping endpoint enumeration, which the topology contract does not
    /// carry.
    /// <para>读取每个接口首个备用设置的接口三元组，与 <c>HarmonyOSUsbFinder</c> 一致，但跳过端点枚举——拓扑契约
    /// 不承载端点。</para>
    /// </summary>
    private static IReadOnlyList<HarmonyTopologyInterface> ReadInterfaces(IntPtr configPtr)
    {
        var config = Marshal.PtrToStructure<UsbDdkConfigDescriptor>(configPtr);
        var interfaces = new List<HarmonyTopologyInterface>();
        if (config.Iface == IntPtr.Zero)
        {
            return interfaces;
        }

        var interfaceSize = Marshal.SizeOf<UsbDdkInterface>();
        for (var index = 0; index < config.NumIface; index++)
        {
            var interfacePtr = new IntPtr(config.Iface.ToInt64() + ((long)index * interfaceSize));
            var iface = Marshal.PtrToStructure<UsbDdkInterface>(interfacePtr);
            if (iface.Altsetting == IntPtr.Zero)
            {
                continue;
            }

            var setting = Marshal.PtrToStructure<UsbDdkInterfaceDescriptor>(iface.Altsetting);
            var descriptor = setting.InterfaceDescriptor;
            interfaces.Add(new HarmonyTopologyInterface
            {
                Number = descriptor.BInterfaceNumber,
                Class = descriptor.BInterfaceClass,
                SubClass = descriptor.BInterfaceSubClass,
                Protocol = descriptor.BInterfaceProtocol
            });
        }

        return interfaces;
    }

    /// <summary>
    /// Reports whether the same opt-in environment gate as <see cref="HarmonyOSUsbApiProvider"/> is
    /// satisfied for this process.
    /// <para>报告本进程是否满足与 <see cref="HarmonyOSUsbApiProvider"/> 相同的选择加入环境变量门。</para>
    /// </summary>
    private static bool IsExplicitlyEnabled()
    {
        var value = Environment.GetEnvironmentVariable(
            HarmonyOSUsbApiProvider.EnableEnvironmentVariable);
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}