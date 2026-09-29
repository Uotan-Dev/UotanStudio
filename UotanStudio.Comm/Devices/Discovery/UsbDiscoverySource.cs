using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Projects exact CommKit USB keys without merging backends or serials. While a debug-bridge
/// server is listening, every direct-USB endpoint of that protocol is suppressed: the server's
/// target list is the only inventory for that protocol (full server takeover).
/// <para>按精确 CommKit USB 键投影端点，不合并后端或序列号。调试桥 server 处于监听状态时，该协议的
/// direct-usb 端点被完全抑制：该协议的设备清单只来自 server 目标列表（server 完全让位）。</para>
/// </summary>
public sealed class UsbDiscoverySource : IDeviceDiscoverySource
{
    private readonly ICommKit _kit;
    private readonly UsbApiKind _backend;
    private readonly Func<string, bool>? _serverProbeOverride;

    internal UsbDiscoverySource(ICommKit kit, UsbApiKind backend, Func<string, bool>? serverProbeOverride = null)
    {
        _kit = kit;
        _backend = backend;
        _serverProbeOverride = serverProbeOverride;
    }

    /// <summary>
    /// Canonical <c>Source</c> value stamped on every direct-USB endpoint.
    /// <para>盖在每个 direct-usb 端点上的规范 <c>Source</c> 值。</para>
    /// </summary>
    public const string SourceName = "usb";

    /// <summary>The source label <c>usb</c>. <para>源标签 <c>usb</c>。</para></summary>
    /// <value>The stable name of this discovery source. <para>该发现源的稳定名称。</para></value>
    public string Name => SourceName;

    /// <summary>
    /// Enumerates the backend's USB devices and projects each into a <c>direct-usb</c> endpoint,
    /// classifying its protocol and completing missing fastboot serials via <c>getvar serialno</c>.
    /// <para>枚举后端的 USB 设备并把每个设备投影为 <c>direct-usb</c> 端点，分类其协议，并经
    /// <c>getvar serialno</c> 补全缺失的 fastboot 序列号。</para>
    /// While a debug-bridge server is listening, every adb or hdc endpoint of that protocol is
    /// suppressed without consulting the server's target list; other protocols are never filtered
    /// by server state.
    /// <para>调试桥 server 处于监听状态时，该协议的全部 adb 或 hdc 端点被抑制，且不再查询 server 的
    /// 目标列表；其他协议从不受 server 状态影响。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels USB enumeration; it is re-checked once enumeration completes.
    /// <para>取消 USB 枚举；枚举完成后会再次检查取消。</para>
    /// </param>
    /// <returns>
    /// The surviving direct-USB endpoints in enumeration order.
    /// <para>按枚举顺序保留下来的 direct-usb 端点。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// A device was enumerated without an exact device key or device path.
    /// <para>某设备枚举结果缺少精确的设备键或设备路径。</para>
    /// </exception>
    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var probeScope = DebugBridgeRoute.BeginProbeScope();
        var adbServerLive = ProbeQuietly(() => _serverProbeOverride?.Invoke("adb") ?? DebugBridgeRoute.UseAdbServer());
        var hdcServerLive = ProbeQuietly(() => _serverProbeOverride?.Invoke("hdc") ?? DebugBridgeRoute.UseHdcServer());

        var devices = await _kit.EnumerateUsbDevicesAsync(_backend, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return devices.Select(device =>
        {
            if (string.IsNullOrWhiteSpace(device.DeviceKey) || string.IsNullOrWhiteSpace(device.DevicePath))
            {
                throw new InvalidDataException("USB endpoint has no exact device key or path.");
            }

            var serial = device.SerialNumber;
            var protocol = UsbProtocolClassifier.Classify(device);
            if (protocol == "fastboot" && (string.IsNullOrWhiteSpace(serial) || serial == "UNKNOWN"))
            {
                serial = FastbootSerialReader.Read(_kit, device) ?? serial;
            }

            return new DeviceEndpoint
            {
                Source = Name,
                Transport = device.SourceApiKind.ToString(),
                Address = device.DeviceKey,
                DisplayName = serial,
                Serial = serial,
                Route = "direct-usb",
                Protocol = protocol,
                UsbInfo = device,
                Usb = new(device.SourceApiKind, device.DevicePath, device.VendorId, device.ProductId,
                    device.InterfaceClass, device.InterfaceSubClass, device.InterfaceProtocol)
            };
        })
        .Where(endpoint => !IsServerOwned(endpoint.Protocol, adbServerLive, hdcServerLive))
        .ToArray();
    }

    private static bool IsServerOwned(string? protocol, bool adbLive, bool hdcLive) => protocol switch
    {
        "adb" => adbLive,
        "hdc" => hdcLive,
        _ => false
    };

    private static bool ProbeQuietly(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}