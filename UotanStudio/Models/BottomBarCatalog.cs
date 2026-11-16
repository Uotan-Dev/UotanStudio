namespace UotanStudio.Models;

/// <summary>
/// Stable identifiers, defaults and normalization rules for bottom-bar display preferences.
/// <para>底栏显示偏好的稳定标识、默认值与规范化规则。</para>
/// </summary>
public static class BottomBarCatalog
{
    /// <summary>Workspace tab key.<para>工作区标签键。</para></summary>
    public const string Workspace = "workspace";

    /// <summary>Flash tab key.<para>烧录标签键。</para></summary>
    public const string Flash = "flash";

    /// <summary>Node editor tab key.<para>节点标签键。</para></summary>
    public const string Node = "node";

    /// <summary>Delivery tab key.<para>交付标签键。</para></summary>
    public const string Delivery = "delivery";

    /// <summary>Settings tab key.<para>设置标签键。</para></summary>
    public const string Settings = "settings";

    /// <summary>Gets the default bottom-bar order.<para>获取默认底栏顺序。</para></summary>
    public static IReadOnlyList<string> DefaultOrder { get; } = [Workspace, Flash, Node, Delivery, Settings];

    /// <summary>Creates the default all-visible bottom-bar preferences.<para>创建默认全部可见的底栏偏好。</para></summary>
    /// <returns>A new mutable preference list.<para>新的可变偏好列表。</para></returns>
    public static List<DisplayPreference> CreateDefaultDisplay() =>
        DisplayPreferenceCatalog.CreateDefault(DefaultOrder);

    /// <summary>
    /// Normalizes persisted preferences to one entry per known item, preserving order and visibility.
    /// <para>把持久化偏好规范为每个已知项一项，同时保留顺序与可见性。</para>
    /// </summary>
    /// <param name="preferences">Persisted preferences; may contain duplicates or unknown keys.<para>持久化偏好；可能含重复或未知键。</para></param>
    /// <returns>A complete normalized list preserving the supplied visibility values.<para>补全后的规范列表，保留输入的可见性。</para></returns>
    public static List<DisplayPreference> Normalize(IEnumerable<DisplayPreference>? preferences) =>
        DisplayPreferenceCatalog.Normalize(preferences, DefaultOrder, NormalizeKey);

    /// <summary>Normalizes a bottom-item key to its canonical lowercase identifier.<para>把底栏项键规范为稳定的全小写标识。</para></summary>
    /// <param name="key">The key to normalize.<para>要规范化的键。</para></param>
    /// <returns>The canonical key, or <see langword="null"/> for an unknown key.<para>规范键；未知键返回 <see langword="null"/>。</para></returns>
    public static string? NormalizeKey(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        Workspace => Workspace,
        Flash => Flash,
        Node => Node,
        Delivery => Delivery,
        Settings => Settings,
        _ => null
    };

    /// <summary>Gets the localized title resource key for a bottom item.<para>获取底栏项对应的本地化标题资源键。</para></summary>
    /// <param name="key">The bottom-item key.<para>底栏项键。</para></param>
    /// <returns>The locale key, or an empty string for an unknown item.<para>本地化键；未知项返回空字符串。</para></returns>
    public static string GetTitleKey(string key) => key switch
    {
        Workspace => "Tab_Workspace",
        Flash => "Tab_Flash",
        Node => "Tab_Node",
        Delivery => "Tab_Delivery",
        Settings => "Tab_Settings",
        _ => string.Empty
    };
}