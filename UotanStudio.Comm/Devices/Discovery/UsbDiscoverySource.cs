using System.Collections.Concurrent;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Projects exact CommKit USB keys without merging backends or serials; while a debug-bridge
/// server is listening, that protocol's direct-USB endpoints are suppressed entirely.
/// <para>按精确 CommKit USB 键投影端点，不合并后端或序列号；调试桥 server 处于监听状态时，
/// 该协议的 direct-usb 端点被完全抑制。</para>
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
    /// Enumerates backend USB devices and projects each into a <c>direct-usb</c> endpoint; while a
    /// debug-bridge server is listening, that protocol's endpoints are suppressed.
    /// <para>枚举后端的 USB 设备并把每个设备投影为 <c>direct-usb</c> 端点；调试桥 server 监听时
    /// 抑制该协议的端点。</para>
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
    /// Projects one device into its <c>direct-usb</c> endpoint; the probed serial slot is pre-filled
    /// by the concurrent probe pass so projection never claims an interface.
    /// <para>把单个设备投影为 <c>direct-usb</c> 端点；探测序列号槽位由并发探测阶段预先填充，
    /// 因此投影本身不认领接口。</para>
    /// </summary>
    /// <param name="device">
    /// The enumerated USB device to project. <para>待投影的已枚举 USB 设备。</para>
    /// </param>
    /// <param name="probedSerial">
    /// The serial from the probe pass, or <see langword="null"/> to keep the descriptor serial.
    /// <para>来自探测阶段的序列号；为 <see langword="null"/> 时沿用描述符序列号。</para>
    /// </param>
    /// <returns>The <c>direct-usb</c> endpoint for this device. <para>该设备的 <c>direct-usb</c> 端点。</para></returns>
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
    /// Probes serial-less fastboot devices concurrently, one task per distinct physical path, so a
    /// device is never claimed twice within one round.
    /// <para>对缺少序列号的 fastboot 设备并发探测，每个不同物理路径一个任务，同一设备在一轮内
    /// 不会被重复认领。</para>
    /// </summary>
    /// <param name="devices">
    /// The enumerated devices to filter for serial-less fastboot candidates.
    /// <para>待筛选出缺少序列号的 fastboot 候选设备的已枚举设备集合。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the per-path probe tasks. <para>取消各物理路径的探测任务。</para>
    /// </param>
    /// <returns>A map from device path to probed serial, with a <see langword="null"/> entry when
    /// that probe failed.
    /// <para>设备路径到探测序列号的映射；对应探测失败时该项为 <see langword="null"/>。</para></returns>
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
    /// Memoizes the fastboot <c>getvar serialno</c> probe by physical identity, caching only
    /// successful reads and stopping claims after a bounded number of failures.
    /// <para>按物理身份记忆化 fastboot <c>getvar serialno</c> 探测：只缓存成功读取的值，
    /// 失败次数到达上限后停止认领。</para>
    /// </summary>
    /// <param name="device">
    /// The fastboot-mode device missing a descriptor serial. <para>描述符缺少序列号的 fastboot 模式设备。</para>
    /// </param>
    /// <returns>The cached or probed serial, or <see langword="null"/> when the probe failed or
    /// its failure quota is exhausted.
    /// <para>缓存或新探测的序列号；探测失败或失败配额用尽时返回 <see langword="null"/>。</para></returns>
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