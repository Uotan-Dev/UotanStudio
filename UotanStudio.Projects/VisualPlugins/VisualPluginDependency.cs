namespace UotanStudio.Projects;

/// <summary>
/// An installed package prerequisite with optional numeric major.minor.patch bounds.
/// <para>带可选 major.minor.patch 数值边界的已安装包先决条件。</para>
/// </summary>
public sealed class VisualPluginDependency
{
    /// <summary>
    /// Required composite package id.
    /// <para>所需的复合包 id。</para>
    /// </summary>
    /// <value>
    /// Identifier the resolver looks for; defaults to the empty string until assigned.
    /// <para>解析器要查找的标识符；赋值前默认为空字符串。</para>
    /// </value>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Inclusive minimum version; null means no lower bound.
    /// <para>含下边界的最低版本；null 表示没有下界。</para>
    /// </summary>
    /// <value>
    /// Version text that must itself parse as a canonical three-component version, or <see langword="null"/>.
    /// <para>必须能解析为规范三段版本的版本文本，或为 <see langword="null"/>。</para>
    /// </value>
    public string? MinimumVersion { get; set; }

    /// <summary>
    /// Exclusive maximum version; null means no upper bound.
    /// <para>不含上边界的最高版本；null 表示没有上界。</para>
    /// </summary>
    /// <value>
    /// Version text that must itself parse as a canonical three-component version, or <see langword="null"/>.
    /// <para>必须能解析为规范三段版本的版本文本，或为 <see langword="null"/>。</para>
    /// </value>
    public string? MaximumVersionExclusive { get; set; }

    /// <summary>
    /// Checks an installed version. Unbounded requirements also support legacy free-form versions.
    /// <para>检查已安装版本。无任何边界的依赖也兼容旧式的自由格式版本。</para>
    /// </summary>
    /// <param name="version">
    /// Installed version text to test against the bounds.
    /// <para>待按边界检验的已安装版本文本。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when both bounds are absent (regardless of the text), or when the version and every declared bound parse canonically and the version lies within them; a non-canonical version or an unparsable bound yields <see langword="false"/>.
    /// <para>两个边界均缺失时返回 <see langword="true"/>（不关心文本内容）；或版本与所有已声明边界都能规范解析、且版本落在边界之内时也返回 <see langword="true"/>；非规范版本或无法解析的边界返回 <see langword="false"/>。</para>
    /// </returns>
    public bool MatchesVersion(string version)
    {
        if (MinimumVersion is null && MaximumVersionExclusive is null)
        {
            return true;
        }
        if (!TryParseVersion(version, out var actual))
        {
            return false;
        }
        if (MinimumVersion is not null
            && (!TryParseVersion(MinimumVersion, out var minimum) || actual < minimum))
        {
            return false;
        }
        return MaximumVersionExclusive is null
            || TryParseVersion(MaximumVersionExclusive, out var maximum) && actual < maximum;
    }

    /// <summary>Accepts canonical three-component nonnegative Int32 versions, without prefixes or suffixes.
    /// <para>仅接受规范的、非负 Int32 的三段版本号，不允许任何前缀或后缀。</para></summary>
    /// <param name="text">Candidate version text; null and strings longer than 32 characters are rejected.
    /// <para>候选版本文本；null 以及超过 32 个字符的字符串会被拒绝。</para></param>
    /// <param name="version">The parsed version on success; reset to <c>0.0.0</c> when parsing fails.
    /// <para>成功时写入解析出的版本；解析失败时重置为 <c>0.0.0</c>。</para></param>
    /// <returns><see langword="true"/> only for an exact major.minor.patch form whose three components round-trip to the original text with no build, revision, padding or trailing separators.
    /// <para>仅当文本恰为 major.minor.patch 形式、三段可原样往返且不含 build、revision、填充或多余分隔符时返回 <see langword="true"/>。</para></returns>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null || text.Length > 32 || !Version.TryParse(text, out var parsed)
            || parsed.Build < 0 || parsed.Revision >= 0 || parsed.ToString(3) != text)
        {
            return false;
        }
        version = parsed;
        return true;
    }
}