namespace UotanStudio.Messages;

/// <summary>
/// Interface of a document that supports the standard clipboard editing commands.
/// <para>支持标准剪贴板编辑指令的文档接口。</para>
/// </summary>
public interface IEditable
{
    void Cut();
    void Copy();
    void Paste();
    void Delete();
    void SelectAll();
}