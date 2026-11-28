using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.Models;

/// <summary>
/// Friendly link entry shown in the settings page: a logo, a display name, a localized description and
/// one external address. The description is resolved from the locale resources on every read, so it
/// follows language switches immediately.
/// <para>设置页展示的友情链接条目：图标、显示名称、本地化介绍与一个外部地址。介绍每次读取时都从语言资源解析，因此会随语言切换立即更新。</para>
/// </summary>
public sealed class FriendlyLink : ObservableObject
{
    /// <summary>
    /// Gets or sets the display name.
    /// <para>获取或设置显示名称。</para>
    /// </summary>
    /// <value>The name shown for the link.<para>用于展示的链接名称。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the locale resource key of the description.
    /// <para>获取或设置介绍的语言资源键。</para>
    /// </summary>
    /// <value>The description resource key.<para>介绍资源键。</para></value>
    public string DescriptionKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the localized description.
    /// <para>获取本地化的介绍。</para>
    /// </summary>
    /// <value>The description in the active language.<para>当前语言下的介绍。</para></value>
    public string Description => LanguageService.Instance.GetString(DescriptionKey);

    /// <summary>
    /// Gets or sets the logo asset URI (<c>avares://</c>).
    /// <para>获取或设置图标资源 URI（<c>avares://</c>）。</para>
    /// </summary>
    /// <value>The asset URI of the logo picture.<para>图标图片的资源 URI。</para></value>
    public string AvatarUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute address opened by the visit button.
    /// <para>获取或设置“点击前往”按钮打开的绝对地址。</para>
    /// </summary>
    /// <value>The absolute URL of the target site.<para>目标站点的绝对 URL。</para></value>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Raises a change notification for the localized description after a language switch.
    /// <para>语言切换后为本地化介绍触发变更通知。</para>
    /// </summary>
    internal void RefreshLocalizedText() => OnPropertyChanged(nameof(Description));
}