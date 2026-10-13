using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Partial type carrying the USB bus-topology surface: which backends can run, the device tree and
/// backend capabilities.
/// <para>承载 USB 总线拓扑面：可运行的后端、设备树与后端能力。</para>
/// </summary>
public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Whether at least one USB topology backend can run on the current platform.
    /// <para>当前平台上是否至少存在一个可运行的 USB 拓扑后端。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when a backend is available.
    /// <para>存在可用后端时为 <see langword="true"/>。</para>
    /// </value>
    public static bool IsUsbTopologySupported => DeviceController.AvailableTopologyBackends.Count > 0;

    /// <summary>
    /// The available topology backends in preference order; empty when no backend can run on the current
    /// platform.
    /// <para>按优先级排列的可用拓扑后端；当前平台没有可运行的后端时为空。</para>
    /// </summary>
    /// <value>
    /// The list of available backends.
    /// <para>可用后端列表。</para>
    /// </value>
    public static IReadOnlyList<UsbTopologyBackendKind> TopologyBackends
        => DeviceController.AvailableTopologyBackends;

    /// <summary>
    /// Reads the system USB bus topology through the topology façade of the device controller.
    /// <para>经设备控制器的拓扑门面读取系统 USB 总线拓扑。</para>
    /// </summary>
    /// <param name="backend">
    /// The explicit backend; <see langword="null"/> selects the platform-native backend with fallback.
    /// <para>显式后端；为 <see langword="null"/> 时选择平台原生后端并回退。</para>
    /// </param>
    /// <returns>
    /// The topology result, or <see langword="null"/> when the requested or selected backend cannot run.
    /// <para>拓扑结果；请求或选中的后端无法运行时返回 <see langword="null"/>。</para>
    /// </returns>
    public UsbTopologyResult? ReadUsbTopology(UsbTopologyBackendKind? backend = null)
        => _controller.ReadUsbTopology(backend);

    /// <summary>
    /// Describes the information a given topology backend can fill.
    /// <para>描述给定拓扑后端能够填充的信息范围。</para>
    /// </summary>
    /// <param name="backend">
    /// The backend to describe.
    /// <para>要描述的后端。</para>
    /// </param>
    /// <returns>
    /// The capability set of that backend.
    /// <para>该后端的能力集合。</para>
    /// </returns>
    public static UsbTopologyCapabilities TopologyCapabilities(UsbTopologyBackendKind backend)
        => DeviceController.TopologyCapabilities(backend);
}