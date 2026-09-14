using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Core;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Services.Workspace;

/// <summary>Single adapter for document selection and refresh notifications, including legacy typed payloads.</summary>
internal static class DocumentNotifications
{
    /// <summary>
    /// Publishes synchronously at the caller's existing notification point. Repeated notifications are
    /// intentional: content completion and clicking a mirror also refresh the properties panel.
    /// </summary>
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
        _ => new((FileViewModel?)null)
    };
}