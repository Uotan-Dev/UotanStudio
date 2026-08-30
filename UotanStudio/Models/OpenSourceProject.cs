namespace UotanStudio.Models;

/// <summary>
/// <para>表示本项目使用或参考的一个开源项目信息，用于设置页面的开源致谢区块。</para>
/// Represents an open-source project used by or referenced in this application,
/// displayed in the open-source credits section of the settings page.
/// </summary>
public sealed class OpenSourceProject
{
    /// <summary>
    /// <para>获取或设置项目名称（如 "Avalonia"）。</para>
    /// Gets or sets the project name (e.g., "Avalonia").
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置项目用途的简要说明。</para>
    /// Gets or sets a short description of the project's purpose.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置开源仓库地址（GitHub 等）。</para>
    /// Gets or sets the open-source repository URL (GitHub, etc.).
    /// </summary>
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置开源协议名称（如 "MIT"、"Apache-2.0"）。</para>
    /// Gets or sets the license name (e.g., "MIT", "Apache-2.0").
    /// </summary>
    public string LicenseName { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置开源协议全文链接。</para>
    /// Gets or sets the link to the full license text.
    /// </summary>
    public string LicenseUrl { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置作者名称。</para>
    /// Gets or sets the author name.
    /// </summary>
    public string AuthorName { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置作者的 GitHub 主页链接。</para>
    /// Gets or sets the author's GitHub profile URL.
    /// </summary>
    public string AuthorGitHubUrl { get; init; } = string.Empty;

    /// <summary>
    /// <para>获取或设置作者的 GitHub 头像链接。</para>
    /// Gets or sets the author's GitHub avatar URL.
    /// </summary>
    public string AuthorAvatarUrl { get; init; } = string.Empty;
}
