namespace UotanStudio.Build;

/// <summary>
/// Identifies the distribution channel of the running build.
/// <para>标识当前构建所属的发行通道。</para>
/// </summary>
public enum BuildChannel
{
    /// <summary>
    /// A stable release build.
    /// <para>稳定正式版构建。</para>
    /// </summary>
    Release = 0,

    /// <summary>
    /// A beta build produced from the latest integration state.
    /// <para>基于最新集成分支状态的 Beta 构建。</para>
    /// </summary>
    Beta = 1,
}

/// <summary>
/// Exposes the version, distribution channel and source revision compiled into the application.
/// <para>提供编译进应用程序的版本、发行通道与源码修订信息。</para>
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// Gets the canonical source repository URL.
    /// <para>获取源码仓库地址。</para>
    /// </summary>
    /// <value>The source repository URL.<para>源码仓库地址。</para></value>
    public const string RepositoryUrl = "https://github.com/Uotan-Dev/UotanStudio";

    /// <summary>
    /// Gets the semantic version of the running build.
    /// <para>获取当前构建的语义化版本。</para>
    /// </summary>
    /// <value>The semantic version.<para>语义化版本。</para></value>
    public static string Version => BuildInfoData.Version;

    /// <summary>
    /// Gets the distribution channel of the running build.
    /// <para>获取当前构建的发行通道。</para>
    /// </summary>
    /// <value>The parsed distribution channel; unknown values resolve to <see cref="BuildChannel.Beta"/>.<para>解析后的发行通道；未知值回退到 <see cref="BuildChannel.Beta"/>。</para></value>
    public static BuildChannel Channel => ParseChannel(BuildInfoData.Channel);

    /// <summary>
    /// Gets the locale resource key that names the current channel.
    /// <para>获取命名当前通道的本地化资源键。</para>
    /// </summary>
    /// <value>The channel display resource key.<para>通道显示名称的资源键。</para></value>
    public static string ChannelKey => GetChannelKey(Channel);

    /// <summary>
    /// Gets the locale resource key that describes the current channel.
    /// <para>获取描述当前通道的本地化资源键。</para>
    /// </summary>
    /// <value>The channel description resource key.<para>通道说明的资源键。</para></value>
    public static string ChannelDescriptionKey => GetChannelDescriptionKey(Channel);

    /// <summary>
    /// Gets the locale resource key that names one distribution channel.
    /// <para>获取命名指定发行通道的本地化资源键。</para>
    /// </summary>
    /// <param name="channel">The channel to name.<para>要命名的通道。</para></param>
    /// <returns>The resource key.<para>资源键。</para></returns>
    public static string GetChannelKey(BuildChannel channel) => channel switch
    {
        BuildChannel.Release => "About_Channel_Release",
        _ => "About_Channel_Beta",
    };

    /// <summary>
    /// Gets the locale resource key that describes one distribution channel.
    /// <para>获取描述指定发行通道的本地化资源键。</para>
    /// </summary>
    /// <param name="channel">The channel to describe.<para>要描述的通道。</para></param>
    /// <returns>The resource key.<para>资源键。</para></returns>
    public static string GetChannelDescriptionKey(BuildChannel channel) => channel switch
    {
        BuildChannel.Release => "About_Channel_Release_Description",
        _ => "About_Channel_Beta_Description",
    };

    /// <summary>
    /// Gets the full 40-character Git commit hash compiled into the application, or an empty
    /// string when the build ran without source-control metadata.
    /// <para>获取编译进应用程序的完整 40 位 Git 提交哈希；无源码管理元数据时为空字符串。</para>
    /// </summary>
    /// <value>The full commit hash or an empty string.<para>完整提交哈希或空字符串。</para></value>
    public static string Commit => BuildInfoData.Commit;

    /// <summary>
    /// Gets the abbreviated Git commit hash shown in the user interface.
    /// <para>获取界面显示的短 Git 提交哈希。</para>
    /// </summary>
    /// <value>The abbreviated commit hash or an empty string.<para>短提交哈希或空字符串。</para></value>
    public static string CommitShort => BuildInfoData.CommitShort;

    /// <summary>
    /// Gets whether the build carries a source revision.
    /// <para>获取构建是否携带源码修订信息。</para>
    /// </summary>
    /// <value><see langword="true"/> when a commit hash is available.<para>存在提交哈希时为 <see langword="true"/>。</para></value>
    public static bool HasCommit => BuildInfoData.Commit.Length > 0;

    /// <summary>
    /// Gets the web address of the compiled commit, or the repository root when no commit is
    /// available.
    /// <para>获取编译提交的网页地址；无提交信息时返回仓库根地址。</para>
    /// </summary>
    /// <value>The commit page URL.<para>提交页面地址。</para></value>
    public static string CommitUrl => HasCommit
        ? string.Concat(RepositoryUrl, "/commit/", BuildInfoData.Commit)
        : RepositoryUrl;

    private static BuildChannel ParseChannel(string channel) => channel switch
    {
        _ when string.Equals(channel, "release", System.StringComparison.OrdinalIgnoreCase) => BuildChannel.Release,
        _ => BuildChannel.Beta,
    };
}