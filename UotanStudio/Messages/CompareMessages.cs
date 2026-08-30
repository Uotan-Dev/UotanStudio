namespace UotanStudio.Messages;

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
