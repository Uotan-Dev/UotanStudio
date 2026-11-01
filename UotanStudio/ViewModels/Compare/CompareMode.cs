namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// The display mode of a comparison.
/// <para>比较的展示模式。</para>
/// </summary>
public enum CompareMode
{
    /// <summary>
    /// Empty state: no comparison yet.
    /// <para>空态：尚无比较内容。</para>
    /// </summary>
    None,

    /// <summary>
    /// Folder comparison.
    /// <para>文件夹比较。</para>
    /// </summary>
    Folder,

    /// <summary>
    /// Text comparison.
    /// <para>文本比较。</para>
    /// </summary>
    Text,

    /// <summary>
    /// Binary comparison in the hex-diff view.
    /// <para>十六进制差异视图中的二进制比较。</para>
    /// </summary>
    Binary,
}