using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Projects exact CommKit USB keys without merging backends or serials. When a debug-bridge
/// server owns its protocol, the corresponding direct-USB endpoints are suppressed entirely:
/// the server's target list is the only inventory for that protocol (full server takeover).
/// <para>按精确 CommKit USB 键投影端点，不合并后端或序列号。当调试桥 server 接管其协议时，
/// 对应协议的 direct-usb 端点被完全抑制：该协议的设备清单只来自 server 目标列表
/// （server 完全让位）。</para>
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
        // Test seam: unit tests inject a constant probe verdict so takeover suppression does
        // not depend on whether a real hdc/adb server happens to run on the host.
        // 单测注入固定探测结论，使接管抑制不依赖宿主是否恰好运行真实 server。
        _serverProbeOverride = serverProbeOverride;
    }

    public const string SourceName = "usb";
    public string Name => SourceName;

    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        // One probe scope per scan pass merges the adb/hdc endpoint probes; only a refused
        // connection means "no server", an inconclusive probe keeps USB enumeration (the
        // open-time ownership guard stays fail-closed).
        // 每个扫描 pass 一个探测范围以合并 adb/hdc 端点探测；仅"连接被拒"视为无 server，
        // 探测不确定时保留 USB 枚举（打开时的归属守卫保持 fail-closed）。
        using var probeScope = DebugBridgeRoute.BeginProbeScope();
        var adbServerLive = ProbeQuietly(() => _serverProbeOverride?.Invoke("adb") ?? DebugBridgeRoute.UseAdbServer());
        var hdcServerLive = ProbeQuietly(() => _serverProbeOverride?.Invoke("hdc") ?? DebugBridgeRoute.UseHdcServer());
        // Per-device takeover: a live server suppresses only the USB endpoints whose serial it
        // actually lists; a device the server does not know about (e.g. its daemon has not
        // re-registered after a server restart) falls back to direct USB. When the list cannot
        // be fetched, suppression stays conservative (whole protocol hidden).
        // 按设备接管：活着的 server 只抑制其列表中实际存在的 serial 对应的 USB 端点；server
        // 不认识的设备（如 server 重启后 daemon 尚未重新注册）回退直连 USB。列表获取失败时
        // 保持保守（整个协议隐藏）。
        var adbTargetSerials = adbServerLive ? TryFetchSerials(() =>
            new UotanStudio.Comm.Protocols.Adb.AdbServerServices(string.Empty,
                UotanStudio.Comm.Routing.DebugBridgeEndpoints.ServerHost,
                UotanStudio.Comm.Routing.DebugBridgeEndpoints.AdbServerPort).ListTargets()
                .Select(target => target.Serial).ToList()) : null;
        var hdcTargetSerials = hdcServerLive ? TryFetchSerials(() =>
            new UotanStudio.Comm.Protocols.Hdc.HdcServerClient().ListTargets()) : null;

        var devices = await _kit.EnumerateUsbDevicesAsync(_backend, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return devices.Select(device =>
        {
            if (string.IsNullOrWhiteSpace(device.DeviceKey) || string.IsNullOrWhiteSpace(device.DevicePath))
            {
                throw new InvalidDataException("USB endpoint has no exact device key or path.");
            }

            // Serial consistency invariant: when the descriptor serial is absent, the value read
            // back over the protocol (fastboot getvar serialno) becomes authoritative so the same
            // physical device keeps one identity across USB and server paths.
            // serial 一致性不变量：描述符缺序列号时，以协议读回值（fastboot getvar serialno）
            // 为权威，使同一物理设备在 USB 与 server 两条路径上身份一致。
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
        .Where(endpoint => !IsServerOwned(endpoint.Protocol, endpoint.Serial, adbServerLive, adbTargetSerials, hdcServerLive, hdcTargetSerials))
        .ToArray();
    }

    private static HashSet<string>? TryFetchSerials(Func<IReadOnlyList<string>> fetch)
    {
        try
        {
            return [.. fetch()];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsServerOwned(string? protocol, string? serial, bool adbLive, HashSet<string>? adbSerials,
        bool hdcLive, HashSet<string>? hdcSerials) => protocol switch
    {
        "adb" when adbLive => adbSerials is not { } list || serial is null || list.Contains(serial),
        "hdc" when hdcLive => hdcSerials is not { } list || serial is null || list.Contains(serial),
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