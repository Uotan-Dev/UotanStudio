namespace UotanStudio.Messages;

/// <summary>
/// <para>打开文档请求消息，当资源管理器等面板请求在文档区打开某个文件时发送。</para>
/// Open document request message, sent when a panel such as the project explorer requests opening a
/// file in the document area.
/// </summary>
public class OpenDocumentRequestMessage
{
    /// <summary>
    /// <para>获取要打开的文档文件路径。</para>
    /// Gets the path of the document file to open.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// <para>创建打开文档请求消息。</para>
    /// Creates an open document request message.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的文档文件路径。</para>
    /// The path of the document file to open.
    /// </param>
    public OpenDocumentRequestMessage(string path)
    {
        Path = path;
    }
}