namespace UotanStudio.Comm.Protocols.Common.Evidence;

/// <summary>
/// Describes where an external resource came from and under what permission it may be used.
/// <para>描述外部资源的来源以及允许使用的许可条件。</para>
/// Provenance is metadata only: it never authorizes redistribution, and a resource whose license
/// is unknown must not enter a default resource package.
/// <para>来源信息仅为元数据：它绝不授权再分发；许可未知的资源不得进入默认资源包。</para>
/// </summary>
public sealed record ResourceProvenance
{
    /// <summary>
    /// Gets how the resource entered the session.
    /// <para>获取资源进入会话的方式。</para>
    /// </summary>
    public required ResourceOrigin Origin { get; init; }

    /// <summary>
    /// Gets the license identifier recorded for the resource, or null when it is unknown.
    /// <para>获取记录的资源许可标识；未知时为 null。</para>
    /// </summary>
    public string? LicenseTag { get; init; }

    /// <summary>
    /// Gets the source path, URL, or commit the resource was obtained from, when recorded.
    /// <para>获取记录的资源来源路径、URL 或 commit。</para>
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Gets the SHA-256 hex digest of the resource bytes.
    /// <para>获取资源字节的 SHA-256 十六进制摘要。</para>
    /// </summary>
    public required string Sha256Hex { get; init; }
}

/// <summary>
/// Classifies how a loader, programmer, or authentication resource entered a session.
/// <para>对 loader、programmer 或认证资源进入会话的方式进行分类。</para>
/// </summary>
public enum ResourceOrigin
{
    /// <summary>
    /// The user selected or supplied the resource explicitly.
    /// <para>由用户显式选择或提供。</para>
    /// </summary>
    UserSupplied,

    /// <summary>
    /// The resource shipped with the application.
    /// <para>随应用一起发布的资源。</para>
    /// </summary>
    Bundled,

    /// <summary>
    /// The resource was extracted or derived from a container during this session.
    /// <para>在本次会话中从容器提取或派生的资源。</para>
    /// </summary>
    Derived
}