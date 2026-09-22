namespace UotanStudio.Messages;

using Dock.Model.Mvvm.Controls;
using UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>活动文档变更消息，当文档区中当前激活的文档切换时发送，
/// 供属性面板等订阅方根据新选中的文档刷新显示内容。</para>
/// Active document changed message, sent when the currently activated document in the document area
/// switches, allowing subscribers such as the properties panel to refresh their display for the
/// newly selected document.
/// </summary>
public class ActiveDocumentChangedMessage
{
    /// <summary>Preserves document identity for editors without a legacy typed payload.</summary>
    public ActiveDocumentChangedMessage(Document? activeDocument) => ActiveDocument = activeDocument;

    /// <summary>Creates a selection notification for a screen-mirror document.</summary>
    public ActiveDocumentChangedMessage(ScreenMirrorViewModel screenMirror) => ActiveDocument = screenMirror;
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
    /// <para>获取新激活的 <see cref="ManifestEditorViewModel"/> 文档；非 Manifest 文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="ManifestEditorViewModel"/> document; <c>null</c> for
    /// non-Manifest documents.
    /// </summary>
    public ManifestEditorViewModel? ManifestDocument { get; }

    /// <summary>
    /// <para>获取新激活的 <see cref="ArscEditorViewModel"/> 文档；非 ARSC 文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="ArscEditorViewModel"/> document; <c>null</c> for non-ARSC
    /// documents.
    /// </summary>
    public ArscEditorViewModel? ArscDocument { get; }

    /// <summary>
    /// <para>获取新激活的 <see cref="ApkEditorViewModel"/> 文档；非 APK 文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="ApkEditorViewModel"/> document; <c>null</c> for non-APK
    /// documents.
    /// </summary>
    public ApkEditorViewModel? ApkDocument { get; }

    /// <summary>
    /// <para>获取新激活的 <see cref="FirmwareInfoViewModel"/> 文档；非固件信息文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="FirmwareInfoViewModel"/> document; <c>null</c> for
    /// non-firmware-info documents.
    /// </summary>
    public FirmwareInfoViewModel? FirmwareDocument { get; }

    /// <summary>
    /// <para>获取新激活的 <see cref="HexEditorViewModel"/> 文档；非 Hex 文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="HexEditorViewModel"/> document; <c>null</c> for non-Hex
    /// documents.
    /// </summary>
    public HexEditorViewModel? HexDocument { get; }

    /// <summary>
    /// <para>获取新激活的 <see cref="FileManagerEditorViewModel"/> 文档；非文件管理器文档时为 <c>null</c>。</para>
    /// Gets the newly activated <see cref="FileManagerEditorViewModel"/> document; <c>null</c> for
    /// non-file-manager documents.
    /// </summary>
    public FileManagerEditorViewModel? FileManagerDocument { get; }

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

    /// <summary>
    /// <para>创建活动文档变更消息（ManifestEditorViewModel 路径）。</para>
    /// Creates an active document changed message (ManifestEditorViewModel path).
    /// </summary>
    /// <param name="manifestDocument">
    /// <para>新激活的 Manifest 编辑器视图模型；可为 <c>null</c>。</para>
    /// The newly activated Manifest editor view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(ManifestEditorViewModel? manifestDocument)
    {
        ManifestDocument = manifestDocument;
        ActiveDocument = manifestDocument;
    }

    /// <summary>
    /// <para>创建活动文档变更消息（ArscEditorViewModel 路径）。</para>
    /// Creates an active document changed message (ArscEditorViewModel path).
    /// </summary>
    /// <param name="arscDocument">
    /// <para>新激活的 ARSC 编辑器视图模型；可为 <c>null</c>。</para>
    /// The newly activated ARSC editor view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(ArscEditorViewModel? arscDocument)
    {
        ArscDocument = arscDocument;
        ActiveDocument = arscDocument;
    }

    /// <summary>
    /// <para>创建活动文档变更消息（ApkEditorViewModel 路径）。</para>
    /// Creates an active document changed message (ApkEditorViewModel path).
    /// </summary>
    /// <param name="apkDocument">
    /// <para>新激活的 APK 编辑器视图模型；可为 <c>null</c>。</para>
    /// The newly activated APK editor view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(ApkEditorViewModel? apkDocument)
    {
        ApkDocument = apkDocument;
        ActiveDocument = apkDocument;
    }

    /// <summary>
    /// <para>创建活动文档变更消息（FirmwareInfoViewModel 路径）。</para>
    /// Creates an active document changed message (FirmwareInfoViewModel path).
    /// </summary>
    /// <param name="firmwareDocument">
    /// <para>新激活的固件信息视图模型；可为 <c>null</c>。</para>
    /// The newly activated firmware-info view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(FirmwareInfoViewModel? firmwareDocument)
    {
        FirmwareDocument = firmwareDocument;
        ActiveDocument = firmwareDocument;
    }

    /// <summary>
    /// <para>创建活动文档变更消息（HexEditorViewModel 路径）。</para>
    /// Creates an active document changed message (HexEditorViewModel path).
    /// </summary>
    /// <param name="hexDocument">
    /// <para>新激活的 Hex 编辑器视图模型；可为 <c>null</c>。</para>
    /// The newly activated Hex editor view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(HexEditorViewModel? hexDocument)
    {
        HexDocument = hexDocument;
        ActiveDocument = hexDocument;
    }

    /// <summary>
    /// <para>创建活动文档变更消息（FileManagerEditorViewModel 路径）。</para>
    /// Creates an active document changed message (FileManagerEditorViewModel path).
    /// </summary>
    /// <param name="fileManagerDocument">
    /// <para>新激活的文件管理器编辑器视图模型；可为 <c>null</c>。</para>
    /// The newly activated file-manager editor view model; may be <c>null</c>.
    /// </param>
    public ActiveDocumentChangedMessage(FileManagerEditorViewModel? fileManagerDocument)
    {
        FileManagerDocument = fileManagerDocument;
        ActiveDocument = fileManagerDocument;
    }
}