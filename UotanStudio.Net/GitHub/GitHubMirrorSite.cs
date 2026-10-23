namespace UotanStudio.Net.GitHub;

/// <summary>A GitHub accelerator mirror; an empty BaseUrl means the origin.
/// <para>GitHub 加速镜像站点描述；BaseUrl 为空表示源站直连。</para></summary>
public sealed record GitHubMirrorSite
{
    /// <summary>Display name of the mirror, used in logs and error text. <para>镜像站显示名称，用于日志与错误文本。</para></summary>
    /// <value>The mandatory site label. <para>必填的站点名称。</para></value>
    public required string Name { get; init; }

    /// <summary>Prefix the GitHub URL is appended to; an empty value marks the direct-origin entry, which is never rewritten. <para>拼接在 GitHub 地址之前的前缀；为空表示直连源站条目，该条目不做地址改写。</para></summary>
    /// <value>The mandatory mirror prefix, which may be empty. <para>必填的镜像前缀，允许为空字符串。</para></value>
    public required string BaseUrl { get; init; }

    /// <summary>Whether the mirror takes part in enumeration and speed probing. <para>该镜像是否参与源枚举与测速。</para></summary>
    /// <value><see langword="true"/> unless the initializer sets otherwise. <para>除非初始化时显式设置，否则为 <see langword="true"/>。</para></value>
    public bool IsEnabled { get; init; } = true;
}