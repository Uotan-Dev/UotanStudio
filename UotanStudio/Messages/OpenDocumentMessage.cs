namespace UotanStudio.Messages;

/// <summary>
/// Document open request message, asking the editor to open a file path.
/// <para>文档打开请求消息，请求编辑器打开指定文件路径。</para>
/// </summary>
public class OpenDocumentMessage(string path)
{
    /// <summary>
    /// Gets the file path to open.
    /// <para>要打开的文件路径。</para>
    /// </summary>
    public string Path { get; } = path;
}