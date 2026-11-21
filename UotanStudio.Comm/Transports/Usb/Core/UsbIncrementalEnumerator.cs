using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Turns event hints into a change list while keeping one cached inventory for a backend/filter pair.
/// <para>把事件提示转换为变化列表，并为后端/过滤器组合保存一份缓存清单。</para>
/// </summary>
internal sealed class UsbIncrementalEnumerator
{
    private readonly IReadOnlyList<IUsbApiProvider> _providers;
    private readonly UsbDeviceFilter? _filter;
    private readonly UsbEnumerationSnapshot _snapshot = new();

    public UsbIncrementalEnumerator(IReadOnlyList<IUsbApiProvider> providers, UsbDeviceFilter? filter)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _filter = filter;
    }

    /// <summary>
    /// Gets the cached inventory, performing one full enumeration on first use.
    /// <para>获取缓存清单；首次使用时执行一次全量枚举。</para>
    /// </summary>
    /// <param name="forceFull">Forces a complete re-enumeration. <para>强制完整重枚举。</para></param>
    /// <returns>The inventory. <para>设备清单。</para></returns>
    internal IReadOnlyList<UsbDeviceInfo> Enumerate(bool forceFull = false)
    {
        if (forceFull || !_snapshot.HasBaseline)
        {
            return _snapshot.Reset(FullEnumerate());
        }

        return _snapshot.Current;
    }

    /// <summary>
    /// Applies one event pass and returns only the devices whose metadata changed.
    /// <para>应用一次事件轮次，只返回元数据发生变化的设备。</para>
    /// </summary>
    /// <param name="hints">Hotplug hints gathered by the event source. <para>事件来源收集的热插拔提示。</para></param>
    /// <returns>The change list; empty when nothing changed. <para>变化列表；无变化时为空。</para></returns>
    internal IReadOnlyList<UsbDeviceChange> Apply(IReadOnlyList<UsbDeviceProbeHint> hints)
    {
        ArgumentNullException.ThrowIfNull(hints);
        var before = UsbDeviceMonitor.BuildMap(_snapshot.Current);
        var devices = _snapshot.Apply(hints, ProbeOne, FullEnumerate);
        var after = UsbDeviceMonitor.BuildMap(devices);
        return UsbDeviceMonitor.ComputeChanges(after, before) ?? [];
    }

    private IReadOnlyList<UsbDeviceInfo> FullEnumerate()
    {
        var devices = new List<UsbDeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            if (!provider.IsSupportedOnCurrentPlatform)
            {
                continue;
            }

            foreach (var info in provider.EnumerateDeviceInfos(_filter))
            {
                if (seen.Add(UsbDeviceIdentity.BuildPhysicalKey(info)))
                {
                    devices.Add(info);
                }
            }
        }

        return devices;
    }

    private UsbDeviceProbeResult ProbeOne(UsbDeviceProbeHint hint)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsSupportedOnCurrentPlatform)
            {
                continue;
            }

            var result = provider.ProbeDeviceInfo(hint, _filter);
            if (result.IsHandled)
            {
                if (result.Device is not null)
                {
                    UsbTrace.Log($"Incremental USB probe hit {provider.ApiName}: {result.Device.DevicePath}");
                }

                return result;
            }
        }

        return UsbDeviceProbeResult.Unhandled;
    }
}