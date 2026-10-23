using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.Models;

/// <summary>
/// Represents one open-source project entry in the credits catalog. The description is resolved
/// from the locale resources on every read, so the credits follow language switches immediately.
/// <para>表示致谢清单中的一个开源项目条目。说明文本每次读取时都从语言资源解析，因此致谢清单会随语言切换立即更新。</para>
/// </summary>
public sealed class OpenSourceProject : ObservableObject
{
    /// <summary>
    /// Gets or sets the project name.
    /// <para>获取或设置项目名称。</para>
    /// </summary>
    /// <value>The project name.<para>项目名称。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the locale resource key of the project description.
    /// <para>获取或设置项目说明的语言资源键。</para>
    /// </summary>
    /// <value>The description resource key.<para>说明资源键。</para></value>
    public string DescriptionKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the localized description of the project's purpose.
    /// <para>获取项目用途的本地化说明。</para>
    /// </summary>
    /// <value>The description in the active language.<para>当前语言下的说明。</para></value>
    public string Description => LanguageService.Instance.GetString(DescriptionKey);

    /// <summary>
    /// Gets or sets the open-source repository URL.
    /// <para>获取或设置开源仓库地址。</para>
    /// </summary>
    /// <value>The source repository URL.<para>源码仓库地址。</para></value>
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the license name.
    /// <para>获取或设置开源协议名称。</para>
    /// </summary>
    /// <value>The license name.<para>开源协议名称。</para></value>
    public string LicenseName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the link to the full license text.
    /// <para>获取或设置开源协议全文链接。</para>
    /// </summary>
    /// <value>The license text link.<para>协议全文链接。</para></value>
    public string LicenseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the author name.
    /// <para>获取或设置作者名称。</para>
    /// </summary>
    /// <value>The author name.<para>作者名称。</para></value>
    public string AuthorName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the author profile URL.
    /// <para>获取或设置作者主页链接。</para>
    /// </summary>
    /// <value>The author profile link.<para>作者主页链接。</para></value>
    public string AuthorGitHubUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the author avatar URL.
    /// <para>获取或设置作者头像链接。</para>
    /// </summary>
    /// <value>The author avatar link.<para>作者头像链接。</para></value>
    public string AuthorAvatarUrl { get; init; } = string.Empty;

    /// <summary>
    /// Raises a change notification for the localized description after a language switch.
    /// <para>语言切换后为本地化说明触发变更通知。</para>
    /// </summary>
    internal void RefreshLocalizedText() => OnPropertyChanged(nameof(Description));
}