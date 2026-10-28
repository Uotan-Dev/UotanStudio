namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Identifiers of the flash page's side sections and the conversion between the four expander flags and the
/// identifiers persisted in the application settings.
/// <para>烧录页面侧边分类的标识，以及四个展开开关与设置中持久化标识之间的转换。</para>
/// </summary>
internal static class FlashSectionState
{
    /// <summary>Identifier of the Qualcomm EDL section. <para>高通 EDL 分类的标识。</para></summary>
    internal const string Edl = "edl";

    /// <summary>Identifier of the MediaTek section. <para>联发科分类的标识。</para></summary>
    internal const string Mtk = "mtk";

    /// <summary>Identifier of the Rockchip section. <para>瑞芯微分类的标识。</para></summary>
    internal const string Rockchip = "rockchip";

    /// <summary>Identifier of the image operations section. <para>镜像操作分类的标识。</para></summary>
    internal const string Image = "image";

    /// <summary>
    /// Lists the identifiers of the expanded sections in their fixed presentation order.
    /// <para>按固定的界面顺序列出已展开分类的标识。</para>
    /// </summary>
    /// <param name="edl">Whether the Qualcomm EDL section is expanded. <para>高通 EDL 分类是否展开。</para></param>
    /// <param name="mtk">Whether the MediaTek section is expanded. <para>联发科分类是否展开。</para></param>
    /// <param name="rockchip">Whether the Rockchip section is expanded. <para>瑞芯微分类是否展开。</para></param>
    /// <param name="image">Whether the image operations section is expanded. <para>镜像操作分类是否展开。</para></param>
    /// <returns>The expanded identifiers; empty when every section is collapsed.
    /// <para>已展开的标识；全部折叠时为空列表。</para></returns>
    internal static List<string> Capture(bool edl, bool mtk, bool rockchip, bool image)
    {
        List<string> expanded = [];
        if (edl) expanded.Add(Edl);
        if (mtk) expanded.Add(Mtk);
        if (rockchip) expanded.Add(Rockchip);
        if (image) expanded.Add(Image);
        return expanded;
    }

    /// <summary>
    /// Reports whether a persisted section identifier marks the section as expanded; comparison ignores case and
    /// unknown identifiers never match.
    /// <para>判断持久化的分类标识是否把该分类标记为展开；比较忽略大小写，未知标识永不匹配。</para>
    /// </summary>
    /// <param name="sections">Persisted identifiers; <see langword="null"/> means every section is collapsed.
    /// <para>持久化的标识；<see langword="null"/> 表示所有分类都折叠。</para></param>
    /// <param name="section">Identifier to look for. <para>要查找的标识。</para></param>
    /// <returns><see langword="true"/> when the identifier is listed.
    /// <para>标识存在时返回 <see langword="true"/>。</para></returns>
    internal static bool IsExpanded(IReadOnlyList<string>? sections, string section)
        => sections is not null && sections.Contains(section, StringComparer.OrdinalIgnoreCase);
}