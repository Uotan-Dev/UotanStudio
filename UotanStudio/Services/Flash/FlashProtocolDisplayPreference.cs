using System.Text.Json.Serialization;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Persisted display preference for one protocol section on the flash page.
/// <para>烧录页面单个协议分类的持久化显示偏好。</para>
/// </summary>
public sealed class FlashProtocolDisplayPreference
{
    /// <summary>Gets or sets the stable protocol key. <para>获取或设置稳定的协议键。</para></summary>
    /// <value><c>edl</c>, <c>mtk</c>, <c>rockchip</c> or <c>sunxi</c>.<para><c>edl</c>、<c>mtk</c>、<c>rockchip</c> 或 <c>sunxi</c>。</para></value>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the protocol section is shown. <para>获取或设置是否显示该协议分类。</para></summary>
    /// <value><see langword="true"/> to display the section.<para>显示分类时为 <see langword="true"/>。</para></value>
    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;

    internal FlashProtocolDisplayPreference Clone() => new()
    {
        Key = Key,
        Visible = Visible
    };
}

/// <summary>
/// Stable identifiers, defaults and normalization rules for flash protocol display preferences.
/// <para>烧录协议显示偏好的稳定标识、默认值与规范化规则。</para>
/// </summary>
public static class FlashProtocolCatalog
{
    /// <summary>Default comma-separated partition list skipped during readback.<para>回读时默认跳过的逗号分隔分区列表。</para></summary>
    public const string DefaultReadbackSkipPartitions =
        "USRDATA,userdata,last_parti,mindowsesp,mindowswin,mindowsdat,rawdump";

    /// <summary>Qualcomm EDL protocol key.<para>高通 EDL 协议键。</para></summary>
    public const string Edl = "edl";

    /// <summary>MediaTek protocol key.<para>联发科协议键。</para></summary>
    public const string Mtk = "mtk";

    /// <summary>Rockchip protocol key.<para>瑞芯微协议键。</para></summary>
    public const string Rockchip = "rockchip";

    /// <summary>Allwinner Sunxi protocol key.<para>全志协议键。</para></summary>
    public const string Sunxi = "sunxi";

    /// <summary>Gets the default protocol order.<para>获取默认协议顺序。</para></summary>
    public static IReadOnlyList<string> DefaultOrder { get; } = [Edl, Mtk, Rockchip, Sunxi];

    /// <summary>Creates the default all-visible display preferences.<para>创建默认全部可见的显示偏好。</para></summary>
    /// <returns>A new mutable preference list.<para>新的可变偏好列表。</para></returns>
    public static List<FlashProtocolDisplayPreference> CreateDefaultDisplay() =>
        [.. DefaultOrder.Select(key => new FlashProtocolDisplayPreference { Key = key, Visible = true })];

    /// <summary>
    /// Normalizes persisted preferences to one entry per known protocol, preserving the supplied order and visibility.
    /// <para>把持久化偏好规范为每个已知协议一项，同时保留输入顺序与可见性。</para>
    /// </summary>
    /// <param name="preferences">Persisted preferences; may contain duplicates or unknown keys.<para>持久化偏好；可能含重复或未知键。</para></param>
    /// <returns>A complete normalized preference list.<para>补全后的规范偏好列表。</para></returns>
    public static List<FlashProtocolDisplayPreference> Normalize(
        IEnumerable<FlashProtocolDisplayPreference>? preferences)
    {
        var normalized = new List<FlashProtocolDisplayPreference>(DefaultOrder.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preference in preferences ?? [])
        {
            var key = NormalizeKey(preference.Key);
            if (key is null || !seen.Add(key))
            {
                continue;
            }

            normalized.Add(new FlashProtocolDisplayPreference { Key = key, Visible = preference.Visible });
        }

        foreach (var key in DefaultOrder)
        {
            if (seen.Add(key))
            {
                normalized.Add(new FlashProtocolDisplayPreference { Key = key, Visible = true });
            }
        }

        return normalized;
    }

    /// <summary>Normalizes a protocol key to its canonical lowercase identifier.<para>把协议键规范为稳定的全小写标识。</para></summary>
    /// <param name="key">The key to normalize.<para>要规范化的键。</para></param>
    /// <returns>The canonical key, or <see langword="null"/> for an unknown key.<para>规范键；未知键返回 <see langword="null"/>。</para></returns>
    public static string? NormalizeKey(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        Edl => Edl,
        Mtk => Mtk,
        Rockchip => Rockchip,
        Sunxi => Sunxi,
        _ => null
    };

    /// <summary>Gets the localized title resource key for a protocol.<para>获取协议对应的本地化标题资源键。</para></summary>
    /// <param name="key">The protocol key.<para>协议键。</para></param>
    /// <returns>The locale key, or an empty string for an unknown protocol.<para>本地化键；未知协议返回空字符串。</para></returns>
    public static string GetTitleKey(string key) => key switch
    {
        Edl => "Flash_Edl",
        Mtk => "Flash_Mtk",
        Rockchip => "Flash_Rockchip",
        Sunxi => "Flash_Sunxi",
        _ => string.Empty
    };
}