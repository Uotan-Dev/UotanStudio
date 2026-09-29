using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

/// <summary>
/// <para>总线拓扑面：宿主只转发 Comm 的 <see cref="DeviceController"/> 拓扑门面，供 USB 树窗口填充后端
/// 选择器、读取设备树并描述后端能力。</para>
/// Bus-topology surface: the host only forwards Comm's <see cref="DeviceController"/> topology façade so
/// the USB tree window can fill its backend selector, read the device tree and describe backend
/// capabilities.
/// </summary>
public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// <para>当前平台上是否至少存在一个可运行的 USB 拓扑后端。</para>
    /// Whether at least one USB topology backend can run on the current platform.
    /// </summary>
    /// <value>
    /// <para>存在可用后端时为 <see langword="true"/>。</para>
    /// <see langword="true"/> when a backend is available.
    /// </value>
    public static bool IsUsbTopologySupported => DeviceController.AvailableTopologyBackends.Count > 0;

    /// <summary>
    /// <para>按优先级排列的可用拓扑后端；没有可用后端时为空，界面必须据此显示不支持而非空树。</para>
    /// The available topology backends in preference order; empty when none can run, in which case the UI
    /// must report the platform as unsupported instead of showing an empty tree.
    /// </summary>
    /// <value>
    /// <para>可用后端列表。</para>
    /// The list of available backends.
    /// </value>
    public static IReadOnlyList<UsbTopologyBackendKind> TopologyBackends
        => DeviceController.AvailableTopologyBackends;

    /// <summary>
    /// <para>读取系统 USB 总线拓扑。宿主不解析 sysfs、注册表或 I/O Registry，只做门面转发。</para>
    /// Reads the system USB bus topology. The host parses no sysfs, registry or I/O Registry; it only
    /// forwards the façade call.
    /// </summary>
    /// <param name="backend">
    /// <para>显式后端；为 <see langword="null"/> 时由 Comm 选择平台原生后端并回退。</para>
    /// The explicit backend; <see langword="null"/> lets Comm pick the platform-native backend with fallback.
    /// </param>
    /// <returns>
    /// <para>拓扑结果；请求或选中的后端无法运行时返回 <see langword="null"/>。</para>
    /// The topology result, or <see langword="null"/> when the requested or selected backend cannot run.
    /// </returns>
    public UsbTopologyResult? ReadUsbTopology(UsbTopologyBackendKind? backend = null)
        => _controller.ReadUsbTopology(backend);

    /// <summary>
    /// <para>描述某后端可填充的范围，用于在后端选择器上给出诚实的能力说明。</para>
    /// Describes what a backend can fill, so the backend selector can state its capabilities honestly.
    /// </summary>
    /// <param name="backend">
    /// <para>要描述的后端。</para>
    /// The backend to describe.
    /// </param>
    /// <returns>
    /// <para>该后端的能力集合。</para>
    /// The capability set of that backend.
    /// </returns>
    public static UsbTopologyCapabilities TopologyCapabilities(UsbTopologyBackendKind backend)
        => DeviceController.TopologyCapabilities(backend);
}