namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比面板的展示模式。</para>
/// The compare panel's display mode.
/// </summary>
public enum CompareMode
{
    /// <summary>
    /// <para>空态：尚无比较内容。</para>
    /// Empty state: no comparison yet.
    /// </summary>
    None,

    /// <summary>
    /// <para>文件夹比较。</para>
    /// Folder comparison.
    /// </summary>
    Folder,

    /// <summary>
    /// <para>文本比较。</para>
    /// Text comparison.
    /// </summary>
    Text,

    /// <summary>
    /// <para>二进制比较（元数据或十六进制差异视图）。</para>
    /// Binary comparison (metadata or hex-diff view).
    /// </summary>
    Binary,
}

/// <summary>
/// <para>二进制比较的展示视图。</para>
/// The binary comparison's display view.
/// </summary>
public enum BinaryViewKind
{
    /// <summary>
    /// <para>元数据视图（格式名/摘要/键值对/条目/组件）。</para>
    /// Metadata view (format name / summary / key-values / entries / components).
    /// </summary>
    Metadata,

    /// <summary>
    /// <para>十六进制差异视图。</para>
    /// Hex-diff view.
    /// </summary>
    Hex,
}