namespace UotanStudio.Messages;

/// <summary>
/// Requests opening or focusing the workspace compare document, optionally adding an explorer selection.
/// <para>请求打开或聚焦工作区对比文档，可携带一个右键选中的文件或目录。</para>
/// </summary>
public sealed class OpenCompareRequestMessage
{
    /// <summary>
    /// The path the request selects for the compare document.
    /// <para>请求为对比文档选中的路径。</para>
    /// </summary>
    /// <value>
    /// A file or directory path to add, empty when the request only opens or focuses the document.
    /// <para>要加入的文件或目录路径，请求仅打开或聚焦文档时为空。</para>
    /// </value>
    public string? Path { get; init; }

    /// <summary>
    /// Whether the request must open its own tab instead of reusing an existing one.
    /// <para>请求是否必须新建标签而不是复用已有标签。</para>
    /// </summary>
    /// <value>
    /// True when a fresh tab is required even though a matching tab already exists.
    /// <para>即使已存在同类标签仍要求新建标签时为真。</para>
    /// </value>
    public bool OpenInNewTab { get; init; }
}

/// <summary>
/// Message requesting to open a file-compare document (text or binary decided by the receiver via content sniffing).
/// <para>请求打开文件比较文档的消息（文本或二进制由接收方按内容探测决定）。</para>
/// </summary>
public sealed class OpenFileCompareRequestMessage
{
    /// <summary>
    /// The left-hand file path of the comparison.
    /// <para>比较的左侧文件路径。</para>
    /// </summary>
    /// <value>
    /// The path opened on the left side of the file comparison.
    /// <para>文件比较中作为左侧打开的路径。</para>
    /// </value>
    public required string LeftPath { get; init; }

    /// <summary>
    /// The right-hand file path of the comparison.
    /// <para>比较的右侧文件路径。</para>
    /// </summary>
    /// <value>
    /// The path opened on the right side of the file comparison.
    /// <para>文件比较中作为右侧打开的路径。</para>
    /// </value>
    public required string RightPath { get; init; }
}

/// <summary>
/// Message requesting a file comparison that opens in a tab of its own. An empty left path marks a
/// file that exists only on the right side.
/// <para>请求在新标签中打开文件比较文档的消息。左侧路径为空表示该文件只存在于右侧。</para>
/// </summary>
public sealed class OpenFileCompareInNewTabRequestMessage
{
    /// <summary>
    /// The left-hand file path of the comparison.
    /// <para>比较的左侧文件路径。</para>
    /// </summary>
    /// <value>
    /// The path opened on the left side, empty when that side holds no counterpart file.
    /// <para>作为左侧打开的路径，该侧没有对应文件时为空。</para>
    /// </value>
    public required string LeftPath { get; init; }

    /// <summary>
    /// The right-hand file path of the comparison.
    /// <para>比较的右侧文件路径。</para>
    /// </summary>
    /// <value>
    /// The path opened on the right side, empty when that side holds no counterpart file.
    /// <para>作为右侧打开的路径，该侧没有对应文件时为空。</para>
    /// </value>
    public required string RightPath { get; init; }
}

/// <summary>
/// Message requesting to open a folder-compare document.
/// <para>请求打开文件夹比较文档的消息。</para>
/// </summary>
public sealed class OpenFolderCompareRequestMessage
{
    /// <summary>
    /// The left-hand directory path of the comparison.
    /// <para>比较的左侧目录路径。</para>
    /// </summary>
    /// <value>
    /// The directory opened on the left side of the folder comparison.
    /// <para>文件夹比较中作为左侧打开的目录。</para>
    /// </value>
    public required string LeftDirectory { get; init; }

    /// <summary>
    /// The right-hand directory path of the comparison.
    /// <para>比较的右侧目录路径。</para>
    /// </summary>
    /// <value>
    /// The directory opened on the right side of the folder comparison.
    /// <para>文件夹比较中作为右侧打开的目录。</para>
    /// </value>
    public required string RightDirectory { get; init; }
}