using System.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using UotanStudio.Comm.Transports.Usb.Backend.MacOS;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// <para>L4 总线拓扑面：向服务层暴露按平台选择的 USB 设备树读取，使宿主无需直接命名
/// <c>CommKit</c> 或任何 P/Invoke。树形状由共享的 <see cref="UsbTopologyTree"/> 统一，
/// 各后端只负责读取自己平台的扁平节点。</para>
/// L4 bus-topology surface: exposes platform-selected USB device-tree reads to the service layer so
/// hosts need not name <c>CommKit</c> or any P/Invoke. The tree shape is unified by the shared
/// <see cref="UsbTopologyTree"/>; each backend only reads the flat nodes its own platform offers.
/// </summary>
public sealed partial class DeviceController
{
    /// <summary>
    /// The topology backends that can currently deliver data on this machine.
    /// <para>当前机器上确实能给出数据的拓扑后端。</para>
    /// </summary>
    /// <value>
    /// Available backends in preference order; empty when no backend can run.
    /// <para>按优先级排列的可用后端；无后端可运行时为空。</para>
    /// </value>
    public static IReadOnlyList<UsbTopologyBackendKind> AvailableTopologyBackends { get; } =
        [
            .. Enum.GetValues<UsbTopologyBackendKind>()
                .Where(kind => ProviderFor(kind) is { IsAvailable: true })
        ];

    /// <summary>
    /// Reads the system USB bus topology.
    /// <para>读取系统 USB 总线拓扑。</para>
    /// </summary>
    /// <param name="backend">
    /// Explicit backend selection; <see langword="null"/> prefers the platform-native backend and falls
    /// back to libusb. <para>显式后端；为 <see langword="null"/> 时优先平台原生后端，并回退到 libusb。</para>
    /// </param>
    /// <returns>
    /// The topology result, or <see langword="null"/> when the requested or selected backend cannot run.
    /// <para>拓扑结果；请求或选中的后端无法运行时返回 <see langword="null"/>。</para>
    /// </returns>
    public UsbTopologyResult? ReadUsbTopology(UsbTopologyBackendKind? backend = null)
    {
        if (ResolveTopologyProvider(backend) is not { } provider)
        {
            return null;
        }

        var started = Stopwatch.GetTimestamp();
        var nodes = provider.ReadFlatNodes().Select(UsbTopologyNodes.Create).ToArray();
        var elapsed = Stopwatch.GetElapsedTime(started);
        return new UsbTopologyResult(provider.Capabilities, UsbTopologyTree.Build(nodes), nodes, elapsed);
    }

    /// <summary>
    /// Reports what a backend can fill, whether or not it can run here.
    /// <para>给出某后端可填充的范围，无论它能否在本机运行。</para>
    /// </summary>
    /// <param name="backend">
    /// The backend to describe. <para>要描述的后端。</para>
    /// </param>
    /// <returns>
    /// The capability set. <para>能力集合。</para>
    /// </returns>
    public static UsbTopologyCapabilities TopologyCapabilities(UsbTopologyBackendKind backend)
        => ProviderFor(backend)!.Capabilities;

    private static IUsbTopologyProvider? ResolveTopologyProvider(UsbTopologyBackendKind? backend)
    {
        if (backend is { } requested)
        {
            return ProviderFor(requested) is { IsAvailable: true } provider ? provider : null;
        }

        foreach (var candidate in PreferredTopologyBackends())
        {
            if (ProviderFor(candidate) is { IsAvailable: true } provider)
            {
                return provider;
            }
        }

        return null;
    }

    private static IEnumerable<UsbTopologyBackendKind> PreferredTopologyBackends()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return UsbTopologyBackendKind.WindowsPnp;
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return UsbTopologyBackendKind.LinuxSysfs;
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return UsbTopologyBackendKind.MacOSIoRegistry;
        }

        yield return UsbTopologyBackendKind.LibUsb;
        yield return UsbTopologyBackendKind.HarmonyOS;
    }

    private static IUsbTopologyProvider? ProviderFor(UsbTopologyBackendKind backend) => backend switch
    {
        UsbTopologyBackendKind.WindowsPnp => OperatingSystem.IsWindows() ? WindowsUsbTopologyProvider.Instance : null,
        UsbTopologyBackendKind.LinuxSysfs => OperatingSystem.IsLinux() ? LinuxSysfsTopologyProvider.Instance : null,
        UsbTopologyBackendKind.MacOSIoRegistry => OperatingSystem.IsMacOS() ? MacIoRegistryTopologyProvider.Instance : null,
        UsbTopologyBackendKind.LibUsb => LibUsbTopologyProvider.Instance,
        UsbTopologyBackendKind.HarmonyOS => HarmonyUsbTopologyProvider.Instance,
        _ => null
    };
}