using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Caches the first full USB enumeration and applies later hotplug events incrementally: only the device a
/// hint names is re-probed, every other device is served from the cache without backend I/O. A hint that
/// cannot name exactly one cached device falls back to a complete enumeration before any mutation is kept.
/// <para>缓存首次全量枚举，并以增量方式应用后续热插拔事件：只重新探测提示点名的设备，其余设备直接从
/// 缓存供给、不产生后端 I/O。若提示无法唯一指向一台已缓存设备，则先回退到完整枚举，再保留结果。</para>
/// </summary>
internal sealed class UsbEnumerationSnapshot
{
    private readonly Lock _gate = new();
    private Dictionary<string, UsbDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _slots = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HashSet<string>> _vidPidOwners = new(StringComparer.OrdinalIgnoreCase);
    private bool _seeded;

    /// <summary>
    /// Applies one event-driven pass. The first pass or a forced refresh enumerates fully; later passes
    /// with hints re-probe only the hinted devices. A pass without hints reuses the cached inventory.
    /// <para>应用一次事件驱动轮次。首轮或强制刷新执行全量枚举；后续带提示的轮次只重新探测被提示设备。
    /// 无提示轮次直接复用缓存清单。</para>
    /// </summary>
    /// <param name="hints">Devices the event source reported as arrived, changed or removed.
    /// <para>事件来源上报为到达、变化或移除的设备。</para></param>
    /// <param name="probeOne">Probes exactly one hint; <see cref="UsbDeviceProbeResult.Unhandled"/> forces a
    /// full pass and <see cref="UsbDeviceProbeResult.Absent"/> removes the hinted device.
    /// <para>只探测一个提示；<see cref="UsbDeviceProbeResult.Unhandled"/> 强制全量轮次，
    /// <see cref="UsbDeviceProbeResult.Absent"/> 移除被提示设备。</para></param>
    /// <param name="fullEnumerator">Fallback that performs a complete enumeration. <para>执行完整枚举的回退委托。</para></param>
    /// <param name="forceFull">Discards the cache and enumerates fully. <para>丢弃缓存并全量枚举。</para></param>
    /// <returns>The merged inventory for this pass. <para>本轮合并后的清单。</para></returns>
    internal IReadOnlyList<UsbDeviceInfo> Apply(
        IReadOnlyList<UsbDeviceProbeHint> hints,
        Func<UsbDeviceProbeHint, UsbDeviceProbeResult> probeOne,
        Func<IReadOnlyList<UsbDeviceInfo>> fullEnumerator,
        bool forceFull = false)
    {
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentNullException.ThrowIfNull(probeOne);
        var enumerator = fullEnumerator ?? throw new ArgumentNullException(nameof(fullEnumerator));

        lock (_gate)
        {
            if (!_seeded || forceFull)
            {
                return Seed(enumerator());
            }

            if (hints.Count == 0)
            {
                return SnapshotList();
            }

            if (hints.Any(hint => !hint.IsSpecific || !CanResolve(hint)))
            {
                return Seed(enumerator());
            }

            var needsFullPass = false;
            foreach (var hint in hints)
            {
                if (ApplyHint(hint, probeOne) == ProbeOutcome.Unhandled)
                {
                    needsFullPass = true;
                }
            }

            return needsFullPass ? Seed(enumerator()) : SnapshotList();
        }
    }

    /// <summary>Gets a cached inventory, or an empty list before the first pass.
    /// <para>获取缓存清单；首轮之前返回空列表。</para></summary>
    internal IReadOnlyList<UsbDeviceInfo> Current
    {
        get
        {
            lock (_gate)
            {
                return _seeded ? SnapshotList() : [];
            }
        }
    }

    /// <summary>Replaces the cache with a complete enumeration for an explicit refresh.
    /// <para>用完整枚举替换缓存，用于显式刷新。</para></summary>
    /// <param name="devices">The complete inventory. <para>完整清单。</para></param>
    /// <returns>The stored inventory. <para>存入的清单。</para></returns>
    internal IReadOnlyList<UsbDeviceInfo> Reset(IReadOnlyList<UsbDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        lock (_gate)
        {
            return Seed(devices);
        }
    }

    /// <summary>Gets whether a baseline pass has completed. <para>获取基线轮次是否已完成。</para></summary>
    internal bool HasBaseline
    {
        get
        {
            lock (_gate)
            {
                return _seeded;
            }
        }
    }

    /// <summary>Gets the number of cached devices, for diagnostics and tests.
    /// <para>获取缓存中的设备数量，供诊断与测试使用。</para></summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _devices.Count;
            }
        }
    }

    private bool CanResolve(UsbDeviceProbeHint hint)
    {
        var hasStrongIdentity = !string.IsNullOrWhiteSpace(hint.DevicePath) ||
            !string.IsNullOrWhiteSpace(hint.InstanceId) ||
            !string.IsNullOrWhiteSpace(hint.PhysicalPortPath);

        if (!hasStrongIdentity && hint.VendorId is not null && hint.ProductId is not null)
        {
            var key = VidPidKey(hint.VendorId.Value, hint.ProductId.Value);
            return _vidPidOwners.TryGetValue(key, out var owners) && owners.Count == 1;
        }

        if (!TryGetMatchedKeys(hint, out var keys))
        {
            return false;
        }

        return hint.IsRemoval || keys.Count > 0 || hasStrongIdentity;
    }

    private ProbeOutcome ApplyHint(UsbDeviceProbeHint hint, Func<UsbDeviceProbeHint, UsbDeviceProbeResult> probeOne)
    {
        if (!TryGetMatchedKeys(hint, out var matchedKeys))
        {
            return ProbeOutcome.Unhandled;
        }

        if (hint.IsRemoval)
        {
            RemoveKeys(matchedKeys);
            return ProbeOutcome.Applied;
        }

        UsbDeviceProbeResult result;
        try
        {
            result = probeOne(hint);
        }
        catch (Exception ex)
        {
            UsbTrace.Log($"Incremental USB probe failed for hint: {ex.GetType().Name}: {ex.Message}");
            result = UsbDeviceProbeResult.Absent;
        }

        if (!result.IsHandled)
        {
            return ProbeOutcome.Unhandled;
        }

        if (result.Device is null)
        {
            RemoveKeys(matchedKeys);
            return ProbeOutcome.Applied;
        }

        var probedKey = UsbDeviceIdentity.BuildPhysicalKey(result.Device);
        var staleKeys = matchedKeys.Where(key =>
            !string.Equals(key, probedKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        RemoveKeys(staleKeys);

        _devices[probedKey] = result.Device;
        RegisterSlots(probedKey, result.Device);
        return ProbeOutcome.Applied;
    }

    private bool TryGetMatchedKeys(UsbDeviceProbeHint hint, out IReadOnlyCollection<string> keys)
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflictingSlot = false;

        AddSlot(hint.DevicePath, matched, ref conflictingSlot);
        AddSlot(hint.InstanceId, matched, ref conflictingSlot);
        AddSlot(hint.PhysicalPortPath, matched, ref conflictingSlot);
        if (conflictingSlot)
        {
            keys = [];
            return false;
        }

        if (hint.VendorId is not null && hint.ProductId is not null)
        {
            var vidPid = VidPidKey(hint.VendorId.Value, hint.ProductId.Value);
            if (_vidPidOwners.TryGetValue(vidPid, out var owners))
            {
                if (owners.Count > 1 && hint.DevicePath is null && hint.InstanceId is null &&
                    hint.PhysicalPortPath is null)
                {
                    keys = [];
                    return false;
                }

                foreach (var owner in owners)
                {
                    matched.Add(owner);
                }
            }
        }

        var validated = matched.Where(key => _devices.TryGetValue(key, out var device) && hint.Matches(device))
            .ToArray();
        if (validated.Length != matched.Count)
        {
            keys = [];
            return false;
        }

        if (validated.Length == 0)
        {
            foreach (var pair in _devices)
            {
                if (hint.Matches(pair.Value))
                {
                    validated = [.. validated, pair.Key];
                }
            }
        }

        keys = validated;
        return true;
    }

    private void AddSlot(string? slot, HashSet<string> matched, ref bool conflictingSlot)
    {
        if (string.IsNullOrWhiteSpace(slot) || !_slots.TryGetValue(slot!, out var key))
        {
            return;
        }

        if (!_devices.ContainsKey(key))
        {
            conflictingSlot = true;
            return;
        }

        matched.Add(key);
    }

    private void RemoveKeys(IEnumerable<string> keys)
    {
        foreach (var key in keys.ToArray())
        {
            if (!_devices.Remove(key, out var device))
            {
                continue;
            }

            UnregisterSlots(key, device);
        }
    }

    private IReadOnlyList<UsbDeviceInfo> Seed(IReadOnlyList<UsbDeviceInfo> devices)
    {
        var map = new Dictionary<string, UsbDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            var key = UsbDeviceIdentity.BuildPhysicalKey(device);
            map[key] = device;
            foreach (var slot in SlotsOf(device))
            {
                slots[slot] = key;
            }

            TrackVidPid(owners, device, key, add: true);
        }

        _devices = map;
        _slots = slots;
        _vidPidOwners = owners;
        _seeded = true;
        return SnapshotList();
    }

    private void RegisterSlots(string key, UsbDeviceInfo device)
    {
        foreach (var slot in SlotsOf(device))
        {
            _slots[slot] = key;
        }

        TrackVidPid(_vidPidOwners, device, key, add: true);
    }

    private void UnregisterSlots(string key, UsbDeviceInfo device)
    {
        foreach (var slot in SlotsOf(device))
        {
            if (_slots.TryGetValue(slot, out var owner) &&
                string.Equals(owner, key, StringComparison.OrdinalIgnoreCase))
            {
                _slots.Remove(slot);
            }
        }

        TrackVidPid(_vidPidOwners, device, key, add: false);
    }

    private static void TrackVidPid(
        Dictionary<string, HashSet<string>> owners,
        UsbDeviceInfo device,
        string key,
        bool add)
    {
        var vidPid = VidPidKey(device.VendorId, device.ProductId);
        if (!owners.TryGetValue(vidPid, out var set))
        {
            if (!add)
            {
                return;
            }

            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            owners[vidPid] = set;
        }

        if (add)
        {
            set.Add(key);
        }
        else
        {
            set.Remove(key);
            if (set.Count == 0)
            {
                owners.Remove(vidPid);
            }
        }
    }

    private static IEnumerable<string> SlotsOf(UsbDeviceInfo device)
    {
        if (!string.IsNullOrWhiteSpace(device.DevicePath))
        {
            yield return device.DevicePath;
        }

        if (!string.IsNullOrWhiteSpace(device.DeviceInstanceId))
        {
            yield return device.DeviceInstanceId;
        }

        if (!string.IsNullOrWhiteSpace(device.PhysicalPortPath))
        {
            yield return device.PhysicalPortPath;
        }

        yield return VidPidKey(device.VendorId, device.ProductId);
    }

    private static string VidPidKey(ushort vendorId, ushort productId) =>
        $"{vendorId:X4}:{productId:X4}";

    private IReadOnlyList<UsbDeviceInfo> SnapshotList() => [.. _devices.Values];

    private enum ProbeOutcome
    {
        Applied,
        Unhandled
    }
}