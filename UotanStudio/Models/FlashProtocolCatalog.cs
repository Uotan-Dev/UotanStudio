namespace UotanStudio.Models;

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
    public static List<DisplayPreference> CreateDefaultDisplay() =>
        DisplayPreferenceCatalog.CreateDefault(DefaultOrder);

    /// <summary>
    /// Normalizes persisted preferences to one entry per known protocol, preserving the supplied order and visibility.
    /// <para>把持久化偏好规范为每个已知协议一项，同时保留输入顺序与可见性。</para>
    /// </summary>
    /// <param name="preferences">Persisted preferences; may contain duplicates or unknown keys.<para>持久化偏好；可能含重复或未知键。</para></param>
    /// <returns>A complete normalized preference list.<para>补全后的规范偏好列表。</para></returns>
    public static List<DisplayPreference> Normalize(IEnumerable<DisplayPreference>? preferences) =>
        DisplayPreferenceCatalog.Normalize(preferences, DefaultOrder, NormalizeKey);

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