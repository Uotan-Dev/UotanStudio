using System.Collections.Concurrent;
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
/// Missing fastboot serials are probed at most a bounded number of times per physical identity and
/// resolved serials are memoized under that identity, so interface renumbering of one device does
/// not re-claim it every round and a permanently silent device does not burn a claim per refresh.
/// <para>缺失的 fastboot 序列号按物理身份限次探测，成功读到的序列号按该身份记忆化，
/// 因此同一设备接口重编号不会每轮重新认领，长期无响应的设备也不会每次刷新都占用一次认领。</para>
/// </summary>
public sealed class UsbDiscoverySource : IDeviceDiscoverySource
{
    /// <summary>
    /// Upper bound on consecutive <c>getvar serialno</c> probe failures tolerated per physical
    /// identity before probing that identity stops until the next successful value replaces it.
    /// <para>每个物理身份可容忍的连续 <c>getvar serialno</c> 探测失败上限，达到上限后停止探测，
    /// 直到下一个成功值将其替换。</para>
    /// </summary>
    private const int MaxSerialProbeFailuresPerIdentity = 3;

    /// <summary>
    /// Upper bound on memoized fastboot-serial identities and failure counters; discovery stays
    /// best-effort beyond the bound instead of growing the dictionaries with recycled topology churn.
    /// <para>记忆化的 fastboot 序列号身份与失败计数器的上限；超出上限时发现保持尽力而为，
    /// 不让字典随拓扑回收噪声无界增长。</para>
    /// </summary>
    private const int MaxTrackedSerialIdentities = 128;

    private readonly ICommKit _kit;
    private readonly UsbApiKind _backend;
    private readonly Func<string, bool>? _serverProbeOverride;
    private readonly ConcurrentDictionary<string, string> _serialByPhysicalIdentity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _serialProbeFailuresByPhysicalIdentity = new(StringComparer.Ordinal);

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
    /// classifying its protocol and completing missing fastboot serials via a memoized, failure-bounded
    /// <c>getvar serialno</c> probe keyed by physical identity.
    /// <para>枚举后端的 USB 设备并把每个设备投影为 <c>direct-usb</c> 端点，分类其协议，并按物理身份
    /// 键控、记忆化且限次失败的 <c>getvar serialno</c> 探测补全缺失的 fastboot 序列号。</para>
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
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceKey) || string.IsNullOrWhiteSpace(device.DevicePath))
            {
                throw new InvalidDataException("USB endpoint has no exact device key or path.");
            }
        }

        var probedSerials = await ProbeMissingFastbootSerialsAsync(devices, cancellationToken).ConfigureAwait(false);
        return [.. devices.Select(device => Project(device, probedSerials.GetValueOrDefault(device.DevicePath))).Where(endpoint => !IsServerOwned(endpoint.Protocol, adbServerLive, hdcServerLive))];
    }

    /// <summary>
    /// Projects one device into its <c>direct-usb</c> endpoint; the probed serial slot is pre-filled by
    /// the concurrent probe pass so projection itself never claims an interface and enumeration order
    /// is preserved.
    /// <para>把单个设备投影为 <c>direct-usb</c> 端点；探测序列号槽位由并发探测阶段预先填充，因此投影
    /// 本身不再认领接口，枚举顺序得以保留。</para>
    /// </summary>
    private DeviceEndpoint Project(UsbDeviceInfo device, string? probedSerial)
    {
        var serial = device.SerialNumber;
        var protocol = UsbProtocolClassifier.Classify(device);
        if (probedSerial is not null)
        {
            serial = probedSerial;
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
    }

    /// <summary>
    /// Probes serial-less fastboot devices concurrently, one task per distinct physical path. Concurrent
    /// probes claim different USB handles, and path grouping guarantees a device is never claimed twice
    /// within one round; the memoized, failure-bounded per-identity rules are unchanged.
    /// <para>对缺少序列号的 fastboot 设备并发探测，每个不同物理路径一个任务。并发探测认领的是不同
    /// USB 句柄，且按路径分组保证同一设备在一轮内不会被重复认领；按身份记忆化与限次的规则保持不变。</para>
    /// </summary>
    private async Task<Dictionary<string, string?>> ProbeMissingFastbootSerialsAsync(
        IReadOnlyList<UsbDeviceInfo> devices, CancellationToken cancellationToken)
    {
        var targets = devices
            .Where(device => UsbProtocolClassifier.Classify(device) == "fastboot" &&
                (string.IsNullOrWhiteSpace(device.SerialNumber) || device.SerialNumber == "UNKNOWN"))
            .GroupBy(device => device.DevicePath, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (targets.Length == 0)
        {
            return [];
        }

        var serials = new string?[targets.Length];
        await Task.WhenAll(targets.Select((device, index) => Task.Run(() =>
        {
            serials[index] = ProbeFastbootSerial(device);
        }, cancellationToken))).ConfigureAwait(false);

        var probed = new Dictionary<string, string?>(targets.Length, StringComparer.Ordinal);
        for (var i = 0; i < targets.Length; i++)
        {
            probed[targets[i].DevicePath] = serials[i];
        }

        return probed;
    }

    private static bool IsServerOwned(string? protocol, bool adbLive, bool hdcLive) => protocol switch
    {
        "adb" => adbLive,
        "hdc" => hdcLive,
        _ => false
    };

    /// <summary>
    /// <para>按物理身份记忆化 fastboot <c>getvar serialno</c> 探测：键为
    /// <see cref="PhysicalDeviceIdentity.From(UsbDeviceInfo)"/> 的身份键，
    /// 身份不可推导时才退回精确设备键（稳定认领键，绝非会被回收的总线地址或设备路径）；
    /// 只记忆化读取成功的值，失败按同一身份计数并在有界次数后停止认领，读到新值后计数清零。</para>
    /// Memoizes the fastboot <c>getvar serialno</c> probe by physical identity: the key is the
    /// <see cref="PhysicalDeviceIdentity"/> derived from the device, falling back to the exact stable
    /// device key only when no identity can be derived (never a recycled bus address or device path);
    /// only successful reads are cached, failures count per the same identity and stop claiming after
    /// a bounded number of rounds, and a later success resets the counter.
    /// </summary>
    /// <param name="device">
    /// The fastboot-mode device missing a descriptor serial. <para>描述符缺少序列号的 fastboot 模式设备。</para>
    /// </param>
    /// <returns>The cached or freshly probed serial, or <see langword="null"/> when the probe failed
    /// or this identity already exhausted its bounded failure rounds.
    /// <para>缓存或新探测到的序列号；探测失败或该身份已用尽有限次失败配额时返回 <see langword="null"/>。</para></returns>
    private string? ProbeFastbootSerial(UsbDeviceInfo device)
    {
        var identityKey = PhysicalDeviceIdentity.From(device)?.Key ?? device.DeviceKey;
        if (_serialByPhysicalIdentity.TryGetValue(identityKey, out var cached))
        {
            return cached;
        }

        if (_serialProbeFailuresByPhysicalIdentity.TryGetValue(identityKey, out var failures) &&
            failures >= MaxSerialProbeFailuresPerIdentity)
        {
            return null;
        }

        var serial = FastbootSerialReader.Read(_kit, device);
        if (serial is { } resolved)
        {
            if (_serialByPhysicalIdentity.Count < MaxTrackedSerialIdentities)
            {
                _serialByPhysicalIdentity[identityKey] = resolved;
            }

            _serialProbeFailuresByPhysicalIdentity.TryRemove(identityKey, out _);
            return resolved;
        }

        if (_serialProbeFailuresByPhysicalIdentity.Count < MaxTrackedSerialIdentities)
        {
            _serialProbeFailuresByPhysicalIdentity.AddOrUpdate(
                identityKey,
                static _ => 1,
                static (_, count) => count >= MaxSerialProbeFailuresPerIdentity ? count : count + 1);
        }

        return null;
    }

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