using UotanStudio.Views.Controls;

namespace UotanStudio.Messages;

/// <summary>
/// <para>打开文档请求消息，当资源管理器等面板请求在文档区打开某个文件时发送。</para>
/// Open document request message, sent when a panel such as the project explorer requests opening a
/// file in the document area.
/// </summary>
public record OpenDocumentRequestMessage(string Path, EditorOpenMode Mode = EditorOpenMode.Auto);