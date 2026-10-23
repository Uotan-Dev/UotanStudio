using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Core;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Services.Ui.Workspace;

/// <summary>
/// Single adapter for document selection and refresh notifications, including typed payloads.
/// <para>文档选中与刷新通知的单一适配入口，包含具类型负载。</para>
/// </summary>
internal static class DocumentNotifications
{
    /// <summary>
    /// Publishes the document notification synchronously.
    /// <para>同步发布文档通知消息。</para>
    /// </summary>
    /// <param name="document">
    /// Document to describe, or null for no active document.
    /// <para>要描述的文档；无活动文档时为 null。</para>
    /// </param>
    internal static void Publish(IDockable? document)
        => WeakReferenceMessenger.Default.Send(Create(document));

    internal static ActiveDocumentChangedMessage Create(IDockable? document) => document switch
    {
        ScreenMirrorViewModel mirror => new(mirror),
        DtbEditorViewModel dtb => new(dtb),
        DexEditorViewModel dex => new(dex),
        ManifestEditorViewModel manifest => new(manifest),
        ArscEditorViewModel arsc => new(arsc),
        ApkEditorViewModel apk => new(apk),
        FirmwareInfoViewModel firmware => new(firmware),
        HexEditorViewModel hex => new(hex),
        FileViewModel file => new(file),
        FileManagerEditorViewModel manager => new(manager),
        _ => new(document as Dock.Model.Mvvm.Controls.Document)
    };
}