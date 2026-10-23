using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.Models;

/// <summary>
/// Development team member with an avatar, display name, role description and external profile links.
/// The role description is resolved from the locale resources on every read, so it follows language
/// switches immediately.
/// <para>开发成员信息：头像、显示名称、职责介绍与外部主页链接。职责介绍每次读取时都从语言资源解析，因此会随语言切换立即更新。</para>
/// </summary>
public sealed class DevelopmentMember : ObservableObject
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
    /// Gets or sets the locale resource key of the role description.
    /// <para>获取或设置职责介绍的语言资源键。</para>
    /// </summary>
    /// <value>The role description resource key.<para>职责介绍资源键。</para></value>
    public string DescriptionKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the localized role description.
    /// <para>获取本地化的职责介绍。</para>
    /// </summary>
    /// <value>The role description in the active language.<para>当前语言下的职责介绍。</para></value>
    public string Description => LanguageService.Instance.GetString(DescriptionKey);


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

    /// <summary>
    /// Raises a change notification for the localized role description after a language switch.
    /// <para>语言切换后为本地化职责介绍触发变更通知。</para>
    /// </summary>
    internal void RefreshLocalizedText() => OnPropertyChanged(nameof(Description));
}