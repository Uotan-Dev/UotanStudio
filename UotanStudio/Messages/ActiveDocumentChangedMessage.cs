namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;
using Dock.Model.Mvvm.Controls;
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
    /// <para>获取新激活的 <see cref="FileViewModel"/> 文档；非文本/二进制文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="FileViewModel"/> document; <c>null</c> for non-file
    /// documents (e.g. DTB editor).
    /// </summary>
    public FileViewModel? Document { get; }

    /// <summary>
    /// <para>获取新激活的 <see cref="DtbEditorViewModel"/> 文档；非设备树文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="DtbEditorViewModel"/> document; <c>null</c> for non-DTB
    /// documents.
    /// </summary>
    public DtbEditorViewModel? DtbDocument { get; }

    /// <summary>
    /// <para>获取新激活的 <see cref="DexEditorViewModel"/> 文档；非 DEX 文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="DexEditorViewModel"/> document; <c>null</c> for non-DEX
    /// documents.
    /// </summary>
    public DexEditorViewModel? DexDocument { get; }

    /// <summary>
    /// <para>获取新激活文档的通用 <see cref="Document"/> 句柄；便于订阅方做类型分支。</para>
    /// Gets the generic <see cref="Document"/> handle of the newly activated document, for
    /// type-dispatching by subscribers.
    /// </summary>
    public Document? ActiveDocument { get; }

    /// <summary>
    /// <para>创建活动文档变更消息（FileViewModel 路径）。</para>
    /// Creates an active document changed message (FileViewModel path).
    /// </summary>
    /// <param name="document">
    /// <para>新激活的文档视图模型；可为 <c>null</c>。</para>
    /// The newly activated document view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(FileViewModel? document)
    {
        Document = document;
        ActiveDocument = document;
    }

    /// <summary>
    /// <para>创建活动文档变更消息（DtbEditorViewModel 路径）。</para>
    /// Creates an active document changed message (DtbEditorViewModel path).
    /// </summary>
    /// <param name="dtbDocument">
    /// <para>新激活的 DTB 编辑器视图模型；可为 <c>null</c>。</para>
    /// The newly activated DTB editor view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(DtbEditorViewModel? dtbDocument)
    {
        DtbDocument = dtbDocument;
        ActiveDocument = dtbDocument;
    }

    /// <summary>
    /// <para>创建活动文档变更消息（DexEditorViewModel 路径）。</para>
    /// Creates an active document changed message (DexEditorViewModel path).
    /// </summary>
    /// <param name="dexDocument">
    /// <para>新激活的 DEX 编辑器视图模型；可为 <c>null</c>。</para>
    /// The newly activated DEX editor view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(DexEditorViewModel? dexDocument)
    {
        DexDocument = dexDocument;
        ActiveDocument = dexDocument;
    }
}