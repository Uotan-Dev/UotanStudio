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

    /// <summary>Gets the cached properties for a device identity, or null when absent.
    /// <para>读取指定设备身份的缓存属性，缺失时返回 null。</para></summary>
    /// <param name="identityKey">Normalized device identity key, matched with ordinal semantics.
    /// <para>规范化的设备身份键，按序数语义匹配。</para></param>
    /// <returns>The cached property map, or <see langword="null"/> when no entry is stored for the key.
    /// <para>缓存的属性映射；该键未存有条目时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="identityKey"/> is <see langword="null"/>.
    /// <para><paramref name="identityKey"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="identityKey"/> is empty or whitespace.
    /// <para><paramref name="identityKey"/> 为空或仅包含空白。</para></exception>
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
    /// <param name="identityKey">Normalized device identity key. <para>规范化的设备身份键。</para></param>
    /// <param name="properties">Properties added for keys that are not cached yet; under concurrent updates the sequence may be enumerated more than once. <para>为尚未缓存的键新增的属性；并发更新时该序列可能被枚举多次。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="identityKey"/> or <paramref name="properties"/> is <see langword="null"/>. <para><paramref name="identityKey"/> 或 <paramref name="properties"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="identityKey"/> is empty or whitespace. <para><paramref name="identityKey"/> 为空或仅包含空白。</para></exception>
    public void Store(string identityKey, IEnumerable<KeyValuePair<string, string>> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKey);
        ArgumentNullException.ThrowIfNull(properties);

        _entries.AddOrUpdate(identityKey,
            _ => new ImmutableEntry(new Dictionary<string, string>(properties, StringComparer.Ordinal)),
            (_, existing) => Merge(existing, properties));
    }

    /// <summary>Drops the entry for a removed device.
    /// <para>设备移除时丢弃条目。</para></summary>
    /// <param name="identityKey">Normalized device identity key. <para>规范化的设备身份键。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="identityKey"/> is <see langword="null"/>. <para><paramref name="identityKey"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="identityKey"/> is empty or whitespace. <para><paramref name="identityKey"/> 为空或仅包含空白。</para></exception>
    public void Invalidate(string identityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKey);
        _entries.TryRemove(identityKey, out _);
    }

    /// <summary>Drops every entry (explicit refresh).
    /// <para>清空全部条目（显式刷新）。</para></summary>
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