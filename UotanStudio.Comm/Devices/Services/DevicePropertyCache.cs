using System.Collections.Concurrent;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Cache for per-device parameters that do not change while a physical device exists
/// (model, brand, kernel version, protocol-reported hardware identity). Entries are keyed by
/// the normalized device identity and are invalidated when the device is removed or the user
/// explicitly refreshes; a mode switch (adb → fastboot) keeps the entry because the physical
/// device identity persists.
/// <para>缓存设备存在期间不会变动的参数（机型、品牌、内核版本、协议报告的硬件身份）。
/// 条目按规范化设备身份键控，仅在设备移除或用户显式刷新时失效；模式切换（adb → fastboot）
/// 保留条目，因为物理设备身份未变。</para>
/// </summary>
public sealed class DevicePropertyCache
{
    private readonly ConcurrentDictionary<string, ImmutableEntry> _entries = new(StringComparer.Ordinal);

    private sealed record ImmutableEntry(IReadOnlyDictionary<string, string> Properties);

    /// <summary>Gets the cached properties for a device identity, or null when absent. 读取缓存属性，缺失时返回 null。</summary>
    public IReadOnlyDictionary<string, string>? Get(string identityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKey);
        return _entries.TryGetValue(identityKey, out var entry) ? entry.Properties : null;
    }

    /// <summary>
    /// Merges properties into the cache; existing keys are not overwritten, so a later probe
    /// cannot replace an already validated value with a worse one.
    /// <para>把属性合并进缓存；已有键不被覆盖，后续探测不会用较劣的值替换已验证的值。</para>
    /// </summary>
    public void Store(string identityKey, IEnumerable<KeyValuePair<string, string>> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKey);
        ArgumentNullException.ThrowIfNull(properties);

        _entries.AddOrUpdate(identityKey,
            _ => new ImmutableEntry(new Dictionary<string, string>(properties, StringComparer.Ordinal)),
            (_, existing) => Merge(existing, properties));
    }

    /// <summary>Drops the entry for a removed device. 设备移除时丢弃条目。</summary>
    public void Invalidate(string identityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKey);
        _entries.TryRemove(identityKey, out _);
    }

    /// <summary>Drops every entry (explicit refresh). 清空全部条目（显式刷新）。</summary>
    public void Clear() => _entries.Clear();

    private static ImmutableEntry Merge(ImmutableEntry existing, IEnumerable<KeyValuePair<string, string>> properties)
    {
        var merged = new Dictionary<string, string>(existing.Properties, StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            merged.TryAdd(key, value);
        }

        return new ImmutableEntry(merged);
    }
}