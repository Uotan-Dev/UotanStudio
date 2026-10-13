namespace UotanStudio.Models;

/// <summary>
/// Represents one open-source project entry in the credits catalog.
/// <para>表示致谢清单中的一个开源项目条目。</para>
/// </summary>
public sealed class OpenSourceProject
{
    /// <summary>
    /// Gets or sets the project name.
    /// <para>获取或设置项目名称。</para>
    /// </summary>
    /// <value>The project name.<para>项目名称。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets a short description of the project's purpose.
    /// <para>获取或设置项目用途的简要说明。</para>
    /// </summary>
    /// <value>A short description of the project purpose.<para>项目用途的简要说明。</para></value>
    public string Description { get; init; } = string.Empty;

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
}