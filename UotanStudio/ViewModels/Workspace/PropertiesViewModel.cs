using UotanStudio.ViewModels.Editors;
using UotanStudio.Services.Projects;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Common;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>属性视图模型，动态显示当前选中节点的属性或选中镜像的详细参数，
/// 订阅节点编辑器选中变更消息与镜像选中变更消息。</para>
/// Properties view model, dynamically displaying the currently selected node's properties or the
/// selected image's detailed parameters, subscribed to node-editor and image selection changes.
/// </summary>
internal partial class PropertiesViewModel : Tool
{
    /// <summary>
    /// <para>单个属性行视图模型，承载名称-值对。</para>
    /// Single property row view model, carrying a name-value pair.
    /// </summary>
    public sealed partial class PropertyRow : ObservableObject
    {
        /// <summary>
        /// <para>属性名称。</para>
        /// Property name.
        /// </summary>
        [ObservableProperty] private string _name = string.Empty;

        /// <summary>
        /// <para>属性值文本。</para>
        /// Property value text.
        /// </summary>
        [ObservableProperty] private string _value = string.Empty;

        /// <summary>
        /// <para>创建属性行。</para>
        /// Creates a property row.
        /// </summary>
        public PropertyRow(string name, string value)
        {
            _name = name;
            _value = value;
        }
    }

    /// <summary>
    /// <para>当前显示的属性行集合，UI 绑定此项。</para>
    /// Collection of property rows currently displayed, bound by the UI.
    /// </summary>
    public ObservableCollection<PropertyRow> Properties { get; } = [];

    /// <summary>
    /// <para>当前选中的节点标题，用于显示。</para>
    /// Title of the currently selected node, for display.
    /// </summary>
    [ObservableProperty] private string _selectedNodeTitle = string.Empty;

    /// <summary>
    /// <para>是否已选中节点。</para>
    /// Whether a node is selected.
    /// </summary>
    [ObservableProperty] private bool _hasSelection;

    /// <summary>
    /// <para>当前展示的内容类型（节点或镜像）。</para>
    /// The type of content currently displayed (node or image).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentTypeDisplay))]
    private PropertiesContentType _contentType = PropertiesContentType.None;

    /// <summary>
    /// <para>本地化后的内容类型标签（属性面板头部徽章）。</para>
    /// Localized content type label (badge in the properties panel header).
    /// </summary>
    public string ContentTypeDisplay
    {
        get
        {
            var typeName = ContentType switch
            {
                PropertiesContentType.Node => LanguageService.Instance.GetString("Dock_Properties_Type_Node"),
                PropertiesContentType.Image => LanguageService.Instance.GetString("Dock_Properties_Type_Image"),
                PropertiesContentType.FileAnalysis => LanguageService.Instance.GetString("Dock_Properties_Type_FileAnalysis"),
                PropertiesContentType.TextDocument => LanguageService.Instance.GetString("Dock_Properties_Type_TextDocument"),
                PropertiesContentType.DtbDocument => LanguageService.Instance.GetString("Dock_Properties_Type_DtbDocument"),
                PropertiesContentType.DexDocument => LanguageService.Instance.GetString("Dock_Properties_Type_DexDocument"),
                PropertiesContentType.ManifestDocument => LanguageService.Instance.GetString("Dock_Properties_Type_ManifestDocument"),
                PropertiesContentType.ArscDocument => LanguageService.Instance.GetString("Dock_Properties_Type_ArscDocument"),
                PropertiesContentType.ApkDocument => LanguageService.Instance.GetString("Dock_Properties_Type_ApkDocument"),
                _ => string.Empty,
            };

            return string.Format(LanguageService.Instance.GetString("Dock_Properties_TypeFormat"), typeName);
        }
    }

    /// <summary>
    /// <para>构造属性视图模型，订阅节点选中变更消息与镜像选中变更消息并显示无选择占位。</para>
    /// Creates the properties view model, subscribing to node-selection-changed and image-selection-changed
    /// messages and showing the no-selection placeholder.
    /// </summary>
    public PropertiesViewModel()
    {
        RefreshForNode(null);
        WeakReferenceMessenger.Default.Register<NodeSelectionChangedMessage>(this, OnNodeSelectionChanged);
        WeakReferenceMessenger.Default.Register<ImageSelectionChangedMessage>(this, OnImageSelectionChanged);
        WeakReferenceMessenger.Default.Register<FileAnalysisCompletedMessage>(this, OnFileAnalysisCompleted);
        WeakReferenceMessenger.Default.Register<ActiveDocumentChangedMessage>(this, OnActiveDocumentChanged);
        WeakReferenceMessenger.Default.Register<DtbNodeSelectionChangedMessage>(this, OnDtbNodeSelectionChanged);
        WeakReferenceMessenger.Default.Register<ManifestNodeSelectionChangedMessage>(this, OnManifestNodeSelectionChanged);
        WeakReferenceMessenger.Default.Register<ArscNodeSelectionChangedMessage>(this, OnArscNodeSelectionChanged);
    }

    /// <summary>
    /// <para>响应节点选中变更消息，刷新属性行。</para>
    /// Responds to node-selection-changed messages by refreshing the property rows.
    /// </summary>
    private void OnNodeSelectionChanged(object recipient, NodeSelectionChangedMessage message) => RefreshForNode(message.Node);

    /// <summary>
    /// <para>响应镜像选中变更消息，刷新属性行。</para>
    /// Responds to image-selection-changed messages by refreshing the property rows.
    /// </summary>
    private void OnImageSelectionChanged(object recipient, ImageSelectionChangedMessage message) => RefreshForImage(message.Image);

    /// <summary>
    /// <para>响应文件分析完成消息，刷新属性行展示分析结果。</para>
    /// Responds to file-analysis-completed messages by refreshing property rows with analysis results.
    /// </summary>
    private void OnFileAnalysisCompleted(object recipient, FileAnalysisCompletedMessage message) => RefreshForFileAnalysis(message);

    /// <summary>
    /// <para>响应活动文档切换消息，根据新选中文档的分析结果刷新属性行；
    /// 文档为 <c>null</c> 或非二进制（无分析结果）时清空面板。</para>
    /// Responds to active-document-changed messages by refreshing property rows with the newly
    /// active document's analysis results; clears the panel when the document is <c>null</c> or has
    /// no analysis (non-binary).
    /// </summary>
    /// <summary>
    /// <para>响应活动文档切换消息，按文档类型（固件分析/文本/设备树）刷新属性行；
    /// 文档为 <c>null</c> 时清空面板。</para>
    /// Responds to active-document-changed messages by refreshing property rows according to the
    /// document type (firmware analysis / text / device tree); clears the panel when the document is
    /// <c>null</c>.
    /// </summary>
    private void OnActiveDocumentChanged(object recipient, ActiveDocumentChangedMessage message)
    {
        // DTB 编辑器文档：走设备树展示路径。
        // DTB editor document: route through the device-tree display path.
        if (message.DtbDocument is { } dtb)
        {
            RefreshForDtbDocument(dtb);
            return;
        }

        // DEX 编辑器文档：走 DEX 展示路径。
        // DEX editor document: route through the DEX display path.
        if (message.DexDocument is { } dex)
        {
            RefreshForDexDocument(dex);
            return;
        }

        // Manifest 编辑器文档：走 Manifest 展示路径。
        // Manifest editor document: route through the Manifest display path.
        if (message.ManifestDocument is { } manifest)
        {
            RefreshForManifestDocument(manifest);
            return;
        }

        // ARSC 编辑器文档：走 ARSC 展示路径。
        // ARSC editor document: route through the ARSC display path.
        if (message.ArscDocument is { } arsc)
        {
            RefreshForArscDocument(arsc);
            return;
        }

        // APK 编辑器文档：走 APK 展示路径。
        // APK editor document: route through the APK display path.
        if (message.ApkDocument is { } apk)
        {
            RefreshForApkDocument(apk);
            return;
        }

        var doc = message.Document;
        if (doc is null)
        {
            if (ContentType == PropertiesContentType.FileAnalysis
                || ContentType == PropertiesContentType.TextDocument
                || ContentType == PropertiesContentType.DtbDocument
                || ContentType == PropertiesContentType.DexDocument
                || ContentType == PropertiesContentType.ManifestDocument
                || ContentType == PropertiesContentType.ArscDocument
                || ContentType == PropertiesContentType.ApkDocument)
            {
                Properties.Clear();
                SelectedNodeTitle = string.Empty;
                HasSelection = false;
                ContentType = PropertiesContentType.None;
            }
            return;
        }

        // 二进制文档：用已缓存的分析结果刷新属性面板。
        // Binary document: refresh with the document's cached analysis results.
        if (doc.IsBinary)
        {
            RefreshForFileAnalysis(new FileAnalysisCompletedMessage(
                doc.Analysis.ToList(),
                doc.DetectedFormat,
                doc.FilePath));
            return;
        }

        // 文本文档：展示文本相关属性。
        // Text document: display text-related properties.
        RefreshForTextDocument(doc);
    }

    /// <summary>
    /// <para>响应设备树节点选中变更消息，刷新选中节点详情。</para>
    /// Responds to DTB node-selection-changed messages by refreshing the selected node details.
    /// </summary>
    private void OnDtbNodeSelectionChanged(object recipient, DtbNodeSelectionChangedMessage message)
    {
        // 仅当当前展示的是设备树文档时才联动更新选中节点详情。
        // Only update the selected-node details when the panel is currently showing a DTB document.
        if (ContentType != PropertiesContentType.DtbDocument)
        {
            return;
        }

        if (message.Editor is null)
        {
            return;
        }

        RefreshForDtbNode(message.Editor, message.Node);
    }

    /// <summary>
    /// <para>刷新指定节点的属性行。节点为 <c>null</c> 时显示占位。</para>
    /// Refreshes property rows for the specified node. Shows a placeholder when the node is <c>null</c>.
    /// </summary>
    public void RefreshForNode(FlowNodeViewModel? node)
    {
        if (node is null && ContentType != PropertiesContentType.Node)
        {
            return;
        }

        Properties.Clear();
        if (node == null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.Node;
        HasSelection = true;
        SelectedNodeTitle = node.Title;

        var svc = LanguageService.Instance;
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_ImageName"), node.Title));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_ImageType"), node.Category.ToString()));
        Properties.Add(new PropertyRow("Status", node.ExecutionStatus.ToString()));
        Properties.Add(new PropertyRow("Location", $"{node.Location.X}, {node.Location.Y}"));
        Properties.Add(new PropertyRow("Inputs", node.Input.Count.ToString()));
        Properties.Add(new PropertyRow("Outputs", node.Output.Count.ToString()));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for node '{node.Title}'");
    }

    /// <summary>
    /// <para>刷新指定镜像的详细参数。镜像为 <c>null</c> 时显示占位。</para>
    /// Refreshes detailed parameters for the specified image. Shows a placeholder when the image is <c>null</c>.
    /// </summary>
    public void RefreshForImage(ImageEntryViewModel? image)
    {
        Properties.Clear();
        if (image is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.Image;
        HasSelection = true;
        SelectedNodeTitle = image.DisplayName;

        var svc = LanguageService.Instance;
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_ImageName"), image.DisplayName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Path"), image.RelativePath));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Format"), image.Format));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Size"), $"{image.SizeBytes:N0} bytes"));

        // 尝试读取固件详细参数
        _ = RefreshImageDetailsAsync(image);

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for image '{image.DisplayName}'");
    }

    /// <summary>
    /// <para>刷新文本文档的属性行（文件名、路径、大小、编码、行数、字符数、语法语言）。</para>
    /// Refreshes property rows for a text document (file name, path, size, encoding, line count,
    /// character count, syntax language).
    /// </summary>
    public void RefreshForTextDocument(FileViewModel doc)
    {
        Properties.Clear();
        if (doc is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.TextDocument;
        HasSelection = true;
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var svc = LanguageService.Instance;
        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Text_FileName"), fileName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Text_FilePath"), doc.FilePath ?? string.Empty));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Text_FileSize"), $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Text_Encoding"), doc.DetectedEncoding ?? "UTF-8"));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Text_Language"), string.IsNullOrEmpty(doc.Language) ? "plain" : doc.Language));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Text_LineCount"), doc.LineCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Text_CharCount"), doc.CharCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for text document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新设备树文档的属性行（文件名、路径、大小、节点总数、根节点 compatible、
    /// #address-cells、#size-cells、内存保留条目数）。</para>
    /// Refreshes property rows for a device-tree document (file name, path, size, total node count,
    /// root compatible, #address-cells, #size-cells, memory-reservation count).
    /// </summary>
    public void RefreshForDtbDocument(DtbEditorViewModel doc)
    {
        Properties.Clear();
        if (doc is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.DtbDocument;
        HasSelection = true;
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var svc = LanguageService.Instance;
        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_FileName"), fileName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_FilePath"), doc.FilePath ?? string.Empty));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_FileSize"), $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_NodeCount"), doc.NodeCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_RootCompatible"), doc.RootCompatible));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_RootAddressCells"), doc.RootAddressCells));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_RootSizeCells"), doc.RootSizeCells));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_MemReservations"), doc.MemoryReservationCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for DTB document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新 Manifest 文档的属性行（文件名、路径、大小、元素/属性/字符串计数）。</para>
    /// Refreshes property rows for a Manifest document (file name, path, size, element/attribute/
    /// string counts).
    /// </summary>
    public void RefreshForManifestDocument(ManifestEditorViewModel doc)
    {
        Properties.Clear();
        if (doc is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.ManifestDocument;
        HasSelection = true;
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var svc = LanguageService.Instance;
        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_FileName"), fileName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_FilePath"), doc.FilePath ?? string.Empty));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_FileSize"), $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_ElementCount"), doc.ElementCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_AttributeCount"), doc.AttributeCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for Manifest document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新 Manifest 选中元素的详情属性行（元素名、命名空间、属性数、子元素数、文本）。</para>
    /// Refreshes property rows for the selected Manifest element (name, namespace, attribute count,
    /// child count, text).
    /// </summary>
    public void RefreshForManifestNode(ManifestEditorViewModel editor, ManifestTreeNodeViewModel? node)
    {
        Properties.Clear();
        if (node is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.ManifestDocument;
        HasSelection = true;
        SelectedNodeTitle = node.Name;

        var svc = LanguageService.Instance;
        var element = node.Element;
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_SelectedNode"), node.Name));
        if (element.Namespace is { Length: > 0 })
        {
            Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_SelectedNodeNs"), element.Namespace));
        }
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_SelectedNodeAttrs"), element.Attributes.Count.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_SelectedNodeChildren"), element.Children.Count.ToString("N0")));
        if (element.Text is { Length: > 0 })
        {
            Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Manifest_SelectedNodeText"), element.Text));
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for Manifest node '{node.Name}'");
    }

    /// <summary>
    /// <para>刷新 APK 文档的属性行（文件名、路径、大小、包名、版本、SDK、ABI、权限数）。</para>
    /// Refreshes property rows for an APK document (file name, path, size, package name, version,
    /// SDK, ABIs, permission count).
    /// </summary>
    public void RefreshForApkDocument(ApkEditorViewModel doc)
    {
        Properties.Clear();
        if (doc is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.ApkDocument;
        HasSelection = true;
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var svc = LanguageService.Instance;
        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_FileName"), fileName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_FilePath"), doc.FilePath ?? string.Empty));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_FileSize"), doc.SizeDisplay));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_PackageName"), doc.PackageName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_VersionName"), doc.VersionName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_VersionCode"), doc.VersionCodeDisplay));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_Sdk"), doc.SdkDisplay));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_Abis"), doc.AbiDisplay));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Apk_Permissions"), doc.Info?.Permissions.Count.ToString("N0") ?? "0"));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for APK document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新 ARSC 文档的属性行（文件名、路径、包/类型表/条目计数）。</para>
    /// Refreshes property rows for an ARSC document (file name, path, package/type-table/entry counts).
    /// </summary>
    public void RefreshForArscDocument(ArscEditorViewModel doc)
    {
        Properties.Clear();
        if (doc is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.ArscDocument;
        HasSelection = true;
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var svc = LanguageService.Instance;
        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_FileName"), fileName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_FilePath"), doc.FilePath ?? string.Empty));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_PackageCount"), doc.PackageCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_TypeCount"), doc.TypeCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_EntryCount"), doc.EntryCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for ARSC document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新 ARSC 选中节点的详情属性行（按包/类型/条目层级展示）。</para>
    /// Refreshes property rows for the selected ARSC node (per package/type/entry level).
    /// </summary>
    public void RefreshForArscNode(ArscEditorViewModel editor, ArscTreeNodeViewModel? node)
    {
        Properties.Clear();
        if (node is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.ArscDocument;
        HasSelection = true;
        SelectedNodeTitle = node.Name;

        var svc = LanguageService.Instance;
        switch (node.Payload)
        {
            case UotanStudio.Firmware.Arsc.ResPackage pkg:
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNode"), pkg.Name));
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNodeKind"), $"0x{pkg.Id:x2}"));
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNodeChildren"), pkg.Types.Count.ToString("N0")));
                break;

            case UotanStudio.Firmware.Arsc.ResType type:
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNode"), $"0x{type.Id:x2}"));
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNodeKind"), "type"));
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNodeDetail"), type.ConfigDescription));
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNodeChildren"), type.Entries.Count.ToString("N0")));
                break;

            case UotanStudio.ViewModels.Editors.ResEntryEntry entry:
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNode"), entry.Key));
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNodeKind"), entry.Entry.Value?.Type.ToString() ?? "<null>"));
                Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Arsc_SelectedNodeDetail"), entry.ValueText));
                break;
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for ARSC node '{node.Name}'");
    }

    /// <summary>
    /// <para>响应 ARSC 节点选中变更消息，刷新选中节点详情。</para>
    /// Responds to ARSC node-selection-changed messages by refreshing the selected node details.
    /// </summary>
    private void OnArscNodeSelectionChanged(object recipient, ArscNodeSelectionChangedMessage message)
    {
        // 仅当当前展示的是 ARSC 文档时才联动更新选中节点详情。
        // Only update the selected-node details when the panel is currently showing an ARSC document.
        if (ContentType != PropertiesContentType.ArscDocument)
        {
            return;
        }

        if (message.Editor is null)
        {
            return;
        }

        RefreshForArscNode(message.Editor, message.Node);
    }

    /// <summary>
    /// <para>响应 Manifest 节点选中变更消息，刷新选中元素详情。</para>
    /// Responds to Manifest node-selection-changed messages by refreshing the selected element details.
    /// </summary>
    private void OnManifestNodeSelectionChanged(object recipient, ManifestNodeSelectionChangedMessage message)
    {
        // 仅当当前展示的是 Manifest 文档时才联动更新选中元素详情。
        // Only update the selected-element details when the panel is currently showing a Manifest document.
        if (ContentType != PropertiesContentType.ManifestDocument)
        {
            return;
        }

        if (message.Editor is null)
        {
            return;
        }

        RefreshForManifestNode(message.Editor, message.Node);
    }

    /// <summary>
    /// <para>刷新设备树选中节点的详情属性行（节点路径、属性数、子节点数）。</para>
    /// Refreshes property rows for the selected device-tree node (node path, property count, child count).
    /// </summary>
    public void RefreshForDtbNode(DtbEditorViewModel editor, DtbNodeViewModel? node)
    {
        Properties.Clear();
        if (node is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.DtbDocument;
        HasSelection = true;
        SelectedNodeTitle = node.FullPath;

        var svc = LanguageService.Instance;
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_SelectedNode"), node.Name));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_SelectedNodePath"), node.FullPath));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_SelectedNodeProps"), node.Node.Properties.Count.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dtb_SelectedNodeChildren"), node.Node.Children.Count.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for DTB node '{node.FullPath}'");
    }

    /// <summary>
    /// <para>刷新 DEX 文档的属性行（文件名、路径、大小、版本、类/方法/字段/字符串/类型计数）。</para>
    /// Refreshes property rows for a DEX document (file name, path, size, version, and class/method/
    /// field/string/type counts).
    /// </summary>
    public void RefreshForDexDocument(DexEditorViewModel doc)
    {
        Properties.Clear();
        if (doc is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.DexDocument;
        HasSelection = true;
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var svc = LanguageService.Instance;
        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_FileName"), fileName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_FilePath"), doc.FilePath ?? string.Empty));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_FileSize"), $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_Version"), doc.Version));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_ClassCount"), doc.ClassCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_MethodCount"), doc.MethodCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_FieldCount"), doc.FieldCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_StringCount"), doc.StringCount.ToString("N0")));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Dex_TypeCount"), doc.TypeCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for DEX document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新指定文件分析结果的属性行。分析为 <c>null</c> 时显示占位。</para>
    /// Refreshes property rows for the specified file analysis results. Shows a placeholder when analysis is <c>null</c>.
    /// </summary>
    public void RefreshForFileAnalysis(FileAnalysisCompletedMessage? analysis)
    {
        Properties.Clear();
        if (analysis is null || analysis.Analysis.Count == 0)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.FileAnalysis;
        HasSelection = true;
        SelectedNodeTitle = analysis.FilePath ?? analysis.DetectedFormat ?? "Analysis";

        // Analysis 已包含 "Format" 行（由 FileViewModel 写入），此处不再单独追加，避免重复。
        // The Analysis list already carries the "Format" row (written by FileViewModel),
        // so adding it again here would duplicate the entry.
        foreach (var row in analysis.Analysis)
        {
            Properties.Add(new PropertyRow(row.Name, row.Value));
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for file analysis '{analysis.FilePath}' ({analysis.Analysis.Count} entries)");
    }

    /// <summary>
    /// <para>异步读取固件详细参数并追加到属性行。</para>
    /// Asynchronously reads firmware detailed parameters and appends them to the property rows.
    /// </summary>
    private async Task RefreshImageDetailsAsync(ImageEntryViewModel image)
    {
        try
        {
            var context = ProjectContextService.Instance;
            var absolutePath = context.IsProjectOpen ? context.ResolvePath(image.RelativePath) : null;
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                return;
            }

            var handler = FormatDetector.Detect(absolutePath);
            if (handler is null)
            {
                return;
            }

            var result = await handler.ReadMetadataAsync(absolutePath).ConfigureAwait(true);
            if (!result.Success)
            {
                return;
            }

            if (result.Metadata != null)
            {
                foreach (var kv in result.Metadata)
                {
                    Properties.Add(new PropertyRow(kv.Key, kv.Value));
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Debug, "Properties", $"Failed to read image details: {ex.Message}");
        }
    }
}

/// <summary>
/// <para>属性面板展示的内容类型。</para>
/// The type of content displayed in the properties panel.
/// </summary>
public enum PropertiesContentType
{
    /// <summary>
    /// <para>无内容。</para>
    /// No content.
    /// </summary>
    None,

    /// <summary>
    /// <para>节点属性。</para>
    /// Node properties.
    /// </summary>
    Node,

    /// <summary>
    /// <para>镜像参数。</para>
    /// Image parameters.
    /// </summary>
    Image,

    /// <summary>
    /// <para>文件分析结果。</para>
    /// File analysis results.
    /// </summary>
    FileAnalysis,

    /// <summary>
    /// <para>文本文档信息。</para>
    /// Text document information.
    /// </summary>
    TextDocument,

    /// <summary>
    /// <para>设备树文档信息。</para>
    /// Device-tree document information.
    /// </summary>
    DtbDocument,

    /// <summary>
    /// <para>DEX 文档信息。</para>
    /// DEX document information.
    /// </summary>
    DexDocument,

    /// <summary>
    /// <para>Manifest 文档信息。</para>
    /// Manifest document information.
    /// </summary>
    ManifestDocument,

    /// <summary>
    /// <para>ARSC 文档信息。</para>
    /// ARSC document information.
    /// </summary>
    ArscDocument,

    /// <summary>
    /// <para>APK 文档信息。</para>
    /// APK document information.
    /// </summary>
    ApkDocument
}