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
    /// Binary comparison (metadata or hex-diff view).
    /// <para>二进制比较（元数据或十六进制差异视图）。</para>
    /// </summary>
    Binary,
}

/// <summary>
/// The binary comparison's display view.
/// <para>二进制比较的展示视图。</para>
/// </summary>
public enum BinaryViewKind
{
    /// <summary>
    /// Metadata view (format name / summary / key-values / entries / components).
    /// <para>元数据视图（格式名/摘要/键值对/条目/组件）。</para>
    /// </summary>
    Metadata,

    /// <summary>
    /// Hex-diff view.
    /// <para>十六进制差异视图。</para>
    /// </summary>
    Hex,
}