using UotanStudio.Services.Workspace.Documents;
using UotanStudio.Views.Controls;

namespace UotanStudio.Messages;

/// <summary>
/// Message requesting that a file opens as a document in the workspace.
/// <para>请求把某个文件作为文档打开到工作区的消息。</para>
/// </summary>
/// <param name="Path">
/// The path of the file to open.
/// <para>要打开的文件路径。</para>
/// </param>
/// <param name="Mode">
/// The open mode to use, defaulting to automatic resolution.
/// <para>使用的打开方式，默认按自动判定处理。</para>
/// </param>
public record OpenDocumentRequestMessage(string Path, EditorOpenMode Mode = EditorOpenMode.Auto)
{
    /// <summary>
    /// Builds the equivalent unified document-open request.
    /// <para>构造等价的统一文档打开请求。</para>
    /// </summary>
    /// <returns>The equivalent open request carrying the same path and mode. <para>携带相同路径与打开方式的等价请求。</para></returns>
    internal DocumentOpenRequest ToRequest() => new() { Path = Path, Mode = Mode };
}