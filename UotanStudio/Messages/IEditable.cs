namespace UotanStudio.Messages;

/// <summary>
/// <para>可编辑文档接口，由支持标准剪贴板操作的 ViewModel 实现。</para>
/// Editable document interface, implemented by ViewModels that support standard clipboard operations.
/// </summary>
public interface IEditable
{
    void Cut();
    void Copy();
    void Paste();
    void Delete();
    void SelectAll();
}
