using UotanStudio.Pages.DockControl;

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
    /// <para>获取请求的打开方式；默认 <c>Auto</c> 由打开方按类型+后缀判定。</para>
    /// Gets the requested open mode; default <c>Auto</c> lets the opener decide by type+extension.
    /// </summary>
    public EditorOpenMode Mode { get; }

    /// <summary>
    /// <para>创建打开文档请求消息。</para>
    /// Creates an open document request message.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的文档文件路径。</para>
    /// The path of the document file to open.
    /// </param>
    /// <param name="mode">
    /// <para>请求的打开方式（默认 <c>Auto</c>）。</para>
    /// The requested open mode (default <c>Auto</c>).
    /// </param>
    public OpenDocumentRequestMessage(string path, EditorOpenMode mode = EditorOpenMode.Auto)
    {
        Path = path;
        Mode = mode;
    }
}