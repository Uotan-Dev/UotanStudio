namespace UotanStudio.Messages;

/// <summary>
/// <para>请求打开或聚焦工作区对比文档，可携带一个右键选中的文件或目录。</para>
/// Requests opening or focusing the workspace compare document, optionally adding an explorer selection.
/// </summary>
public sealed class OpenCompareRequestMessage
{
    /// <summary><para>选中的路径；为空时仅打开或聚焦文档。</para>The selected path; null only opens or focuses the document.</summary>
    public string? Path { get; init; }
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