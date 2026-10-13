namespace UotanStudio.Models;

/// <summary>
/// Development team member with an avatar, display name, role description and external profile links.
/// <para>开发成员信息：头像、显示名称、职责介绍与外部主页链接。</para>
/// </summary>
public sealed class DevelopmentMember
{
    /// <summary>
    /// Gets or sets the display name.
    /// <para>获取或设置显示名称。</para>
    /// </summary>
    /// <value>The name shown for the member.<para>用于展示的成员名称。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the English name or handle, left empty when there is none.
    /// <para>获取或设置英文名或昵称，没有时留空。</para>
    /// </summary>
    /// <value>The English name or handle of the member.<para>成员的英文名或昵称。</para></value>
    public string EnglishName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the role description.
    /// <para>获取或设置职责介绍。</para>
    /// </summary>
    /// <value>The role description of the member.<para>成员的职责介绍。</para></value>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the avatar asset URI (<c>avares://</c>).
    /// <para>获取或设置头像资源 URI（<c>avares://</c>）。</para>
    /// </summary>
    /// <value>The asset URI of the avatar picture.<para>头像图片的资源 URI。</para></value>
    public string AvatarUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the code-hosting profile URL.
    /// <para>获取或设置代码托管平台的主页地址。</para>
    /// </summary>
    /// <value>The profile URL on a code-hosting site.<para>代码托管站点上的主页地址。</para></value>
    public string GitHubUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the Coolapk profile URL.
    /// <para>获取或设置酷安主页地址。</para>
    /// </summary>
    /// <value>The profile URL on the Coolapk site.<para>酷安社区的主页地址。</para></value>
    public string CoolapkUrl { get; init; } = string.Empty;
}