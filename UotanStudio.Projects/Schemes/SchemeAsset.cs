namespace UotanStudio.Projects.Schemes;

/// <summary>
/// Declared scheme asset addressed relative to the scheme file.
/// <para>相对 Scheme 文件寻址的声明式资源。</para>
/// </summary>
public sealed class SchemeAsset
{
    /// <summary>
    /// Stable asset identifier.
    /// <para>稳定的资源标识符。</para>
    /// </summary>
    /// <value>Identifier unique inside the owning scheme.<para>所属 Scheme 内唯一的标识符。</para></value>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the asset.
    /// <para>资源的显示名称。</para>
    /// </summary>
    /// <value>Human-readable caption shown by hosts.<para>宿主展示的可读标题。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Path relative to the directory containing the scheme file.
    /// <para>相对 Scheme 文件所在目录的路径。</para>
    /// </summary>
    /// <value>A relative, non-escaping path using forward slashes.<para>使用正斜杠、不可越界的相对路径。</para></value>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Expected lower-case SHA-256 hex digest of the asset bytes.
    /// <para>资源字节的预期小写 SHA-256 十六进制摘要。</para>
    /// </summary>
    /// <value>The digest, or <see langword="null"/> when the author declares no guard.
    /// <para>摘要；作者未声明校验值时返回 <see langword="null"/>。</para></value>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Expected asset length in bytes.
    /// <para>预期的资源字节长度。</para>
    /// </summary>
    /// <value>The declared length, or <c>0</c> when the author declares none.
    /// <para>声明的长度；作者未声明时为 <c>0</c>。</para></value>
    public long Length { get; set; }
}