namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// The role of a file column: a non-participating column browses like a plain file browser
/// (directory tree or file content), while a participating column shows comparison left or right cells.
/// <para>文件列在对比中的角色：未参与对比时按普通文件浏览器显示（目录树或文件内容），
/// 参与对比后分别显示对比结果的左侧/右侧单元格。</para>
/// </summary>
public enum CompareColumnSide
{
    /// <summary>
    /// Browsing column: shows a directory tree or a single file's content, not part of a comparison.
    /// <para>浏览列：显示目录树或单文件内容，不参与对比。</para>
    /// </summary>
    Browse,

    /// <summary>
    /// Compare left column: shows the left cells of the comparison rows.
    /// <para>对比左列：显示对比行的左侧单元格。</para>
    /// </summary>
    Left,

    /// <summary>
    /// Compare right column: shows the right cells of the comparison rows.
    /// <para>对比右列：显示对比行的右侧单元格。</para>
    /// </summary>
    Right,
}