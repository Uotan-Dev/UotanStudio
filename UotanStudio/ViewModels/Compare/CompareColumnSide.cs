namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>文件列在对比中的角色：未参与对比时按普通文件浏览器显示（目录树或文件内容），
/// 参与对比后分别显示对比结果的左侧/右侧单元格。</para>
/// The role of a file column in the comparison: a column that does not take part in a comparison
/// browses like a plain file browser (directory tree or file content), while a participating column
/// shows the comparison's left or right cells.
/// </summary>
public enum CompareColumnSide
{
    /// <summary>
    /// <para>浏览列：显示目录树或单文件内容，不参与对比。</para>
    /// Browsing column: shows a directory tree or a single file's content, not part of a comparison.
    /// </summary>
    Browse,

    /// <summary>
    /// <para>对比左列：显示对比行的左侧单元格。</para>
    /// Compare left column: shows the left cells of the comparison rows.
    /// </summary>
    Left,

    /// <summary>
    /// <para>对比右列：显示对比行的右侧单元格。</para>
    /// Compare right column: shows the right cells of the comparison rows.
    /// </summary>
    Right,
}