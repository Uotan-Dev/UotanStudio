namespace UotanStudio.Messages;

/// <summary>
/// <para>请求打开或聚焦工作区对比文档，可携带一个右键选中的文件或目录。</para>
/// Requests opening or focusing the workspace compare document, optionally adding an explorer selection.
/// </summary>
public sealed class OpenCompareRequestMessage
{
    /// <summary><para>选中的路径；为空时仅打开或聚焦文档。</para>The selected path; null only opens or focuses the document.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// <para>是否强制新开一个标签页（「视图」菜单的多列文件浏览器入口使用）：即使已经存在浏览器也再开一个。</para>
    /// Whether a new tab must be created (used by the View menu's multi-column file browser entry): a browser tab is opened even when one
    /// already exists.
    /// </summary>
    public bool OpenInNewTab { get; init; }
}

/// <summary>
/// <para>请求打开文件比较文档的消息（文本或二进制由接收方按内容探测决定）。</para>
/// Message requesting to open a file-compare document (text or binary decided by the receiver via content sniffing).
/// </summary>
public sealed class OpenFileCompareRequestMessage
{
    /// <summary>左文件路径。<para>Left file path.</para></summary>
    public required string LeftPath { get; init; }

    /// <summary>右文件路径。<para>Right file path.</para></summary>
    public required string RightPath { get; init; }
}

/// <summary>
/// <para>请求在新标签中打开一个文件比较文档：用于对比视图双击文件时把文件对比放到新标签，
/// 不打断当前的目录对比。左侧为空表示该文件只存在于右侧。</para>
/// Message requesting a file comparison to open in a new tab: used when the compare view double-clicks a file, so
/// the file comparison opens in its own tab without interrupting the current folder comparison. An empty left
/// path means the file exists on the right side only.
/// </summary>
public sealed class OpenFileCompareInNewTabRequestMessage
{
    /// <summary>左文件路径；该侧没有对应文件时为空。<para>Left file path; empty when that side has no counterpart.</para></summary>
    public required string LeftPath { get; init; }

    /// <summary>右文件路径；该侧没有对应文件时为空。<para>Right file path; empty when that side has no counterpart.</para></summary>
    public required string RightPath { get; init; }
}

/// <summary>
/// <para>请求打开文件夹比较文档的消息。</para>
/// Message requesting to open a folder-compare document.
/// </summary>
public sealed class OpenFolderCompareRequestMessage
{
    /// <summary>左目录路径。<para>Left directory path.</para></summary>
    public required string LeftDirectory { get; init; }

    /// <summary>右目录路径。<para>Right directory path.</para></summary>
    public required string RightDirectory { get; init; }
}