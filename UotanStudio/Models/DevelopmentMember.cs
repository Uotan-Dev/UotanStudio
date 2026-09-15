namespace UotanStudio.Models;

/// <summary>
/// <para>开发成员信息：头像、名称、职责介绍与外部主页链接，用于设置页的「开发成员」区块。</para>
/// Development team member: avatar, name, role description and external profile links, shown in the
/// settings page's developer section.
/// </summary>
public sealed class DevelopmentMember
{
    /// <summary>
    /// <para>获取或设置显示名称。</para>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置英文名或昵称（没有时留空，界面不显示）。</para>
    /// Gets or sets the English name or handle (left empty when there is none; the UI then hides it).
    /// </summary>
    public string EnglishName { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置职责介绍。</para>
    /// Gets or sets the role description.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置头像资源 URI（<c>avares://</c>）。</para>
    /// Gets or sets the avatar asset URI (<c>avares://</c>).
    /// </summary>
    public string AvatarUri { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置 GitHub 主页地址。</para>
    /// Gets or sets the GitHub profile URL.
    /// </summary>
    public string GitHubUrl { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置酷安主页地址。</para>
    /// Gets or sets the Coolapk profile URL.
    /// </summary>
    public string CoolapkUrl { get; init; } = string.Empty;
}