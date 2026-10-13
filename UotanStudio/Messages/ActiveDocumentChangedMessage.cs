namespace UotanStudio.Messages;

using Dock.Model.Mvvm.Controls;
using UotanStudio.ViewModels.Editors;

/// <summary>
/// Message announcing which document became the active one, carrying its identity so subscribers can
/// react.
/// <para>宣告哪个文档变为活动文档的消息，携带其标识以便订阅方作出响应。</para>
/// </summary>
public class ActiveDocumentChangedMessage
{
    /// <summary>
    /// Creates a message that records only the generic document handle.
    /// <para>创建只记录通用文档句柄的消息。</para>
    /// </summary>
    /// <param name="activeDocument">
    /// The document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(Document? activeDocument) => ActiveDocument = activeDocument;

    /// <summary>
    /// Creates a message for an activated document that carries mirrored screen content.
    /// <para>为已激活的投屏文档创建消息。</para>
    /// </summary>
    /// <param name="screenMirror">
    /// The mirrored-screen document that became active.
    /// <para>变为活动状态的投屏文档。</para>
    /// </param>
    public ActiveDocumentChangedMessage(ScreenMirrorViewModel screenMirror) => ActiveDocument = screenMirror;
    /// <summary>
    /// Gets the activated file document, present only when one was activated.
    /// <para>获取已激活的文件文档，仅在激活了文件文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated file document, or <see langword="null"/> when a different kind of document was
    /// activated.
    /// <para>已激活的文件文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public FileViewModel? Document { get; }

    /// <summary>
    /// Gets the activated device tree document, present only when one was activated.
    /// <para>获取已激活的设备树文档，仅在激活了设备树文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated device tree document, or <see langword="null"/> when a different kind of document
    /// was activated.
    /// <para>已激活的设备树文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public DtbEditorViewModel? DtbDocument { get; }

    /// <summary>
    /// Gets the activated DEX document, present only when one was activated.
    /// <para>获取已激活的 DEX 文档，仅在激活了 DEX 文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated DEX document, or <see langword="null"/> when a different kind of document was
    /// activated.
    /// <para>已激活的 DEX 文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public DexEditorViewModel? DexDocument { get; }

    /// <summary>
    /// Gets the activated Manifest document, present only when one was activated.
    /// <para>获取已激活的 Manifest 文档，仅在激活了 Manifest 文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated Manifest document, or <see langword="null"/> when a different kind of document was
    /// activated.
    /// <para>已激活的 Manifest 文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public ManifestEditorViewModel? ManifestDocument { get; }

    /// <summary>
    /// Gets the activated ARSC document, present only when one was activated.
    /// <para>获取已激活的 ARSC 文档，仅在激活了 ARSC 文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated ARSC document, or <see langword="null"/> when a different kind of document was
    /// activated.
    /// <para>已激活的 ARSC 文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public ArscEditorViewModel? ArscDocument { get; }

    /// <summary>
    /// Gets the activated APK document, present only when one was activated.
    /// <para>获取已激活的 APK 文档，仅在激活了 APK 文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated APK document, or <see langword="null"/> when a different kind of document was
    /// activated.
    /// <para>已激活的 APK 文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public ApkEditorViewModel? ApkDocument { get; }

    /// <summary>
    /// Gets the activated firmware information document, present only when one was activated.
    /// <para>获取已激活的固件信息文档，仅在激活了固件信息文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated firmware information document, or <see langword="null"/> when a different kind of
    /// document was activated.
    /// <para>已激活的固件信息文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public FirmwareInfoViewModel? FirmwareDocument { get; }

    /// <summary>
    /// Gets the activated hex document, present only when one was activated.
    /// <para>获取已激活的十六进制文档，仅在激活了十六进制文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated hex document, or <see langword="null"/> when a different kind of document was
    /// activated.
    /// <para>已激活的十六进制文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public HexEditorViewModel? HexDocument { get; }

    /// <summary>
    /// Gets the activated file manager document, present only when one was activated.
    /// <para>获取已激活的文件管理器文档，仅在激活了文件管理器文档时有值。</para>
    /// </summary>
    /// <value>
    /// The activated file manager document, or <see langword="null"/> when a different kind of document
    /// was activated.
    /// <para>已激活的文件管理器文档，激活的是其他类型文档时为空。</para>
    /// </value>
    public FileManagerEditorViewModel? FileManagerDocument { get; }

    /// <summary>
    /// Gets the activated document as a handle shared by every document kind.
    /// <para>以各类文档共有的句柄获取已激活的文档。</para>
    /// </summary>
    /// <value>
    /// The activated document handle, or <see langword="null"/> when the message carries no document.
    /// <para>已激活文档的句柄，消息未携带文档时为空。</para>
    /// </value>
    public Document? ActiveDocument { get; }

    /// <summary>
    /// Creates a message for an activated file document.
    /// <para>为已激活的文件文档创建消息。</para>
    /// </summary>
    /// <param name="document">
    /// The file document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的文件文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(FileViewModel? document)
    {
        Document = document;
        ActiveDocument = document;
    }

    /// <summary>
    /// Creates a message for an activated device tree document.
    /// <para>为已激活的设备树文档创建消息。</para>
    /// </summary>
    /// <param name="dtbDocument">
    /// The device tree document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的设备树文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(DtbEditorViewModel? dtbDocument)
    {
        DtbDocument = dtbDocument;
        ActiveDocument = dtbDocument;
    }

    /// <summary>
    /// Creates a message for an activated DEX document.
    /// <para>为已激活的 DEX 文档创建消息。</para>
    /// </summary>
    /// <param name="dexDocument">
    /// The DEX document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的 DEX 文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(DexEditorViewModel? dexDocument)
    {
        DexDocument = dexDocument;
        ActiveDocument = dexDocument;
    }

    /// <summary>
    /// Creates a message for an activated Manifest document.
    /// <para>为已激活的 Manifest 文档创建消息。</para>
    /// </summary>
    /// <param name="manifestDocument">
    /// The Manifest document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的 Manifest 文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(ManifestEditorViewModel? manifestDocument)
    {
        ManifestDocument = manifestDocument;
        ActiveDocument = manifestDocument;
    }

    /// <summary>
    /// Creates a message for an activated ARSC document.
    /// <para>为已激活的 ARSC 文档创建消息。</para>
    /// </summary>
    /// <param name="arscDocument">
    /// The ARSC document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的 ARSC 文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(ArscEditorViewModel? arscDocument)
    {
        ArscDocument = arscDocument;
        ActiveDocument = arscDocument;
    }

    /// <summary>
    /// Creates a message for an activated APK document.
    /// <para>为已激活的 APK 文档创建消息。</para>
    /// </summary>
    /// <param name="apkDocument">
    /// The APK document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的 APK 文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(ApkEditorViewModel? apkDocument)
    {
        ApkDocument = apkDocument;
        ActiveDocument = apkDocument;
    }

    /// <summary>
    /// Creates a message for an activated firmware information document.
    /// <para>为已激活的固件信息文档创建消息。</para>
    /// </summary>
    /// <param name="firmwareDocument">
    /// The firmware information document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的固件信息文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(FirmwareInfoViewModel? firmwareDocument)
    {
        FirmwareDocument = firmwareDocument;
        ActiveDocument = firmwareDocument;
    }

    /// <summary>
    /// Creates a message for an activated hex document.
    /// <para>为已激活的十六进制文档创建消息。</para>
    /// </summary>
    /// <param name="hexDocument">
    /// The hex document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的十六进制文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(HexEditorViewModel? hexDocument)
    {
        HexDocument = hexDocument;
        ActiveDocument = hexDocument;
    }

    /// <summary>
    /// Creates a message for an activated file manager document.
    /// <para>为已激活的文件管理器文档创建消息。</para>
    /// </summary>
    /// <param name="fileManagerDocument">
    /// The file manager document that became active, possibly <see langword="null"/>.
    /// <para>变为活动状态的文件管理器文档，可以为空。</para>
    /// </param>
    public ActiveDocumentChangedMessage(FileManagerEditorViewModel? fileManagerDocument)
    {
        FileManagerDocument = fileManagerDocument;
        ActiveDocument = fileManagerDocument;
    }
}