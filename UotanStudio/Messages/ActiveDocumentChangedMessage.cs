namespace UotanStudio.Messages;

using UotanStudio.ViewModels;

/// <summary>
/// <para>活动文档变更消息，当文档区中当前激活的文档切换时发送，
/// 供属性面板等订阅方根据新选中的文档刷新显示内容。</para>
/// Active document changed message, sent when the currently activated document in the document area
/// switches, allowing subscribers such as the properties panel to refresh their display for the
/// newly selected document.
/// </summary>
public class ActiveDocumentChangedMessage
{
    /// <summary>
    /// <para>获取新激活的文档视图模型；<c>null</c> 表示文档区已无活动文档。</para>
    /// Gets the newly activated document view model; <c>null</c> when the document area has no
    /// active document.
    /// </summary>
    public FileViewModel? Document { get; }

    /// <summary>
    /// <para>创建活动文档变更消息。</para>
    /// Creates an active document changed message.
    /// </summary>
    /// <param name="document">
    /// <para>新激活的文档视图模型；可为 <c>null</c>。</para>
    /// The newly activated document view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(FileViewModel? document)
    {
        Document = document;
    }
}
