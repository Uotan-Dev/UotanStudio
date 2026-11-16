using System.Text.Json.Serialization;

namespace UotanStudio.Models;

/// <summary>
/// Persisted order and visibility preference for one configurable display item.
/// <para>单个可配置显示项的持久化顺序与可见性偏好。</para>
/// </summary>
public sealed class DisplayPreference
{
    /// <summary>Gets or sets the stable item key.<para>获取或设置稳定的项目键。</para></summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the item is shown.<para>获取或设置是否显示该项目。</para></summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;

    internal DisplayPreference Clone() => new()
    {
        Key = Key,
        Visible = Visible
    };
}

/// <summary>Shared normalization for ordered, visibility-aware display preferences.<para>有序、带可见性的显示偏好共享规范化逻辑。</para></summary>
internal static class DisplayPreferenceCatalog
{
    internal static List<DisplayPreference> CreateDefault(IReadOnlyList<string> order) =>
        [.. order.Select(key => new DisplayPreference { Key = key, Visible = true })];

    internal static List<DisplayPreference> Normalize(
        IEnumerable<DisplayPreference>? preferences,
        IReadOnlyList<string> order,
        Func<string?, string?> normalizeKey)
    {
        var normalized = new List<DisplayPreference>(order.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preference in preferences ?? [])
        {
            var key = normalizeKey(preference.Key);
            if (key is null || !seen.Add(key))
            {
                continue;
            }

            normalized.Add(new DisplayPreference { Key = key, Visible = preference.Visible });
        }

        foreach (var key in order)
        {
            if (seen.Add(key))
            {
                normalized.Add(new DisplayPreference { Key = key, Visible = true });
            }
        }

        return normalized;
    }
}