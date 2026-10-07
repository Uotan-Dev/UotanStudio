using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Magic;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>属性视图模型，动态显示当前选中节点的属性或选中镜像的详细参数，
/// 订阅节点编辑器选中变更消息与镜像选中变更消息。</para>
/// Properties view model, dynamically displaying the currently selected node's properties or the
/// selected image's detailed parameters, subscribed to node-editor and image selection changes.
/// </summary>
internal partial class PropertiesViewModel : Tool
{
    /// <summary>Stable setting value with a localized label. 稳定设置值与本地化标签。</summary>
    public sealed class PropertyChoice : ObservableObject
    {
        public string Value { get; }

        public string Label
        {
            get;
            private set => SetProperty(ref field, value);
        } = string.Empty;

        /// <summary>Resource key used to refresh this label after a language switch.</summary>
        internal string? LabelResourceKey { get; }

        private string FallbackLabel { get; }

        public PropertyChoice(string value, string label, string? labelResourceKey = null, string? fallbackLabel = null)
        {
            Value = value;
            LabelResourceKey = labelResourceKey;
            FallbackLabel = fallbackLabel ?? label;

            Label = !string.IsNullOrEmpty(labelResourceKey)
                && string.Equals(label, labelResourceKey, StringComparison.Ordinal)
                ? FallbackLabel
                : label;
        }

        internal void RefreshLocalizedLabel()
        {
            if (string.IsNullOrEmpty(LabelResourceKey))
            {
                return;
            }

            var localized = LanguageService.Instance.GetString(LabelResourceKey);
            var next = string.Equals(localized, LabelResourceKey, StringComparison.Ordinal)
                ? FallbackLabel
                : localized;
            if (!string.Equals(Label, next, StringComparison.Ordinal))
            {
                Label = next;
            }
        }
    }
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
        /// <para>此行是否使用语法语言下拉选择器。</para>
        /// Whether this row uses the syntax-language dropdown.
        /// </summary>
        public bool IsLanguageSelector { get; init; }

        /// <summary>
        /// <para>此行是否使用文本编码下拉选择器。</para>
        /// Whether this row uses the text-encoding dropdown.
        /// </summary>
        public bool IsEncodingSelector { get; init; }
        /// <summary>Choices for editable session settings. 会话设置下拉选项。</summary>
        public IReadOnlyList<PropertyChoice> Choices { get; init; } = [];
        /// <summary>Stable identifier for session setting controls. 会话设置控件稳定标识。</summary>
        public string SettingKey { get; init; } = string.Empty;
        /// <summary>Whether the row contains a session setting selector. 是否显示会话下拉框。</summary>
        public bool IsChoiceSelector => Choices.Count > 0;
        [ObservableProperty] private PropertyChoice? _selectedChoice;
        internal Action<string>? ChoiceChanged { get; set; }
        partial void OnSelectedChoiceChanged(PropertyChoice? value)
        {
            if (value is not null) ChoiceChanged?.Invoke(value.Value);
        }

        /// <summary>
        /// <para>此行是否以纯文本显示属性值（非任何选择器行）。</para>
        /// Whether this row displays the value as plain text (not any selector row).
        /// </summary>
        public bool IsPlainValue => !IsLanguageSelector && !IsEncodingSelector && !IsChoiceSelector;

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
    /// <para>当前活动的文本文档引用（语言选择器回写目标）；非文本文档时为 <c>null</c>。</para>
    /// Reference to the current text document (target for language-selector write-back);
    /// <c>null</c> when the active document is not a text document.
    /// </summary>
    private FileViewModel? _currentTextDocument;

    /// <summary>
    /// <para>当前活动的 Hex 文档引用（编码选择器回写目标）；非 Hex 文档时为 <c>null</c>。</para>
    /// Reference to the current Hex document (target for encoding-selector write-back);
    /// <c>null</c> when the active document is not a Hex document.
    /// </summary>
    private HexEditorViewModel? _currentHexDocument;

    private long _imageDetailsGeneration;

    /// <summary>
    /// <para>语言选择器是否可见（仅文本文档类型显示）。</para>
    /// Whether the language selector is visible (shown only for text document types).
    /// </summary>
    [ObservableProperty]
    private bool _hasLanguageSelector;

    /// <summary>
    /// <para>可用语法语言列表（显示名称），供 ComboBox 绑定。</para>
    /// Available syntax language list (display names) for ComboBox binding.
    /// </summary>
    public ObservableCollection<string> AvailableLanguages { get; } = [];

    /// <summary>
    /// <para>编码选择器可选项；由 <see cref="FileViewModel.SupportedEncodings"/> 提供与编辑器一致的集合。</para>
    /// Encoding dropdown options; sourced from <see cref="FileViewModel.SupportedEncodings"/> so the
    /// editor and the properties panel share one set.
    /// </summary>
    public ObservableCollection<string> AvailableEncodings { get; } = [];

    /// <summary>
    /// <para>当前选中的语法语言显示名称；用户在下拉栏切换时触发 <see cref="OnSelectedLanguageChanged"/>。</para>
    /// Currently selected syntax language display name; triggers <see cref="OnSelectedLanguageChanged"/>
    /// when the user switches it in the dropdown.
    /// </summary>
    [ObservableProperty]
    private string _selectedLanguage = string.Empty;

    /// <summary>
    /// <para>当前选中的文本编码名称；用户在下拉栏切换时回写到文档并触发重新保存。</para>
    /// Currently selected text-encoding name; written back to the document (marking it dirty for
    /// the next save) when the user switches it in the dropdown.
    /// </summary>
    [ObservableProperty]
    private string _selectedEncoding = string.Empty;

    /// <summary>
    /// <para>编码选择器变更抑制标志（程序化设置 <see cref="SelectedEncoding"/> 时避免触发回写）。</para>
    /// Encoding-selector change suppression flag (prevents write-back when programmatically
    /// setting <see cref="SelectedEncoding"/>).
    /// </summary>
    private bool _suppressEncodingChange;

    /// <summary>
    /// <para>当用户在语言下拉栏中选择新语言时，将对应的语言标识写回当前文本文档。</para>
    /// When the user selects a new language in the dropdown, writes the corresponding language id back
    /// to the current text document.
    /// </summary>
    partial void OnSelectedLanguageChanged(string value)
    {
        if (_suppressLanguageChange || _currentTextDocument is not { } doc)
        {
            return;
        }

        if (string.IsNullOrEmpty(value) || (AvailableLanguages.Count > 0 && value == AvailableLanguages[0]))
        {

            var autoId = SyntaxLanguageRegistry.GetLanguageId(doc.FilePath ?? doc.Title) ?? string.Empty;
            doc.Language = autoId;
        }
        else
        {
            var match = SyntaxLanguageRegistry.GetAllLanguages().FirstOrDefault(l => l.DisplayName == value);
            doc.Language = match.Id;
        }
    }

    /// <summary>
    /// <para>本地化后的内容类型属性值。</para>
    /// Localized content-type property value.
    /// </summary>
    public string ContentTypeDisplay
    {
        get
        {
            var typeName = ContentType switch
            {
                PropertiesContentType.ElfDocument => LanguageService.Instance.GetString("Dock_Elf_Title"),
                PropertiesContentType.PartitionDocument => LanguageService.Instance.GetString("Dock_Properties_Type_PartitionDocument"),
                PropertiesContentType.LogDocument => LanguageService.Instance.GetString("Dock_Properties_Type_LogDocument"),
                PropertiesContentType.ScreenMirror => LanguageService.Instance.GetString("Tab_ScreenMirror"),
                PropertiesContentType.Image => LanguageService.Instance.GetString("Dock_Properties_Type_Image"),
                PropertiesContentType.FileAnalysis => LanguageService.Instance.GetString("Dock_Properties_Type_FileAnalysis"),
                PropertiesContentType.TextDocument => LanguageService.Instance.GetString("Dock_Properties_Type_TextDocument"),
                PropertiesContentType.DtbDocument => LanguageService.Instance.GetString("Dock_Properties_Type_DtbDocument"),
                PropertiesContentType.DexDocument => LanguageService.Instance.GetString("Dock_Properties_Type_DexDocument"),
                PropertiesContentType.ManifestDocument => LanguageService.Instance.GetString("Dock_Properties_Type_ManifestDocument"),
                PropertiesContentType.ArscDocument => LanguageService.Instance.GetString("Dock_Properties_Type_ArscDocument"),
                PropertiesContentType.ApkDocument => LanguageService.Instance.GetString("Dock_Properties_Type_ApkDocument"),
                PropertiesContentType.FirmwareDocument => LanguageService.Instance.GetString("Dock_Properties_Type_FirmwareDocument"),
                PropertiesContentType.HexDocument => LanguageService.Instance.GetString("Dock_Properties_Type_HexDocument"),
                PropertiesContentType.CompareEntry => LanguageService.Instance.GetString("Dock_Properties_Type_CompareEntry"),
                _ => string.Empty,
            };

            return typeName;
        }
    }

    /// <summary>
    /// <para>构造属性视图模型，订阅节点选中变更消息与镜像选中变更消息并显示无选择占位。</para>
    /// Creates the properties view model, subscribing to node-selection-changed and image-selection-changed
    /// messages and showing the no-selection placeholder.
    /// </summary>
    public PropertiesViewModel()
    {
        WeakReferenceMessenger.Default.Register<ImageSelectionChangedMessage>(this, OnImageSelectionChanged);
        WeakReferenceMessenger.Default.Register<FileAnalysisCompletedMessage>(this, OnFileAnalysisCompleted);
        WeakReferenceMessenger.Default.Register<ActiveDocumentChangedMessage>(this, OnActiveDocumentChanged);
        WeakReferenceMessenger.Default.Register<DtbNodeSelectionChangedMessage>(this, OnDtbNodeSelectionChanged);
        WeakReferenceMessenger.Default.Register<ManifestNodeSelectionChangedMessage>(this, OnManifestNodeSelectionChanged);
        WeakReferenceMessenger.Default.Register<ArscNodeSelectionChangedMessage>(this, OnArscNodeSelectionChanged);
        WeakReferenceMessenger.Default.Register<FileManagerSelectionChangedMessage>(this, OnFileManagerSelectionChanged);
        WeakReferenceMessenger.Default.Register<CompareSelectionChangedMessage>(this, OnCompareSelectionChanged);
        WeakReferenceMessenger.Default.Register<PropertiesViewModel, LanguageStateChangedMessage>(this,
            static (recipient, _) => recipient.RefreshLocalizedPropertyNamesOnUiThread());
    }

    /// <summary>
    /// <para>响应镜像选中变更消息，刷新属性行。</para>
    /// Responds to image-selection-changed messages by refreshing the property rows.
    /// </summary>
    private void OnImageSelectionChanged(object recipient, ImageSelectionChangedMessage message) => RefreshForImage(message.Image);

    /// <summary>
    /// <para>响应文件管理器条目选中变更消息，刷新属性行。</para>
    /// Responds to file-manager entry selection changed messages by refreshing the property rows.
    /// </summary>
    private void OnFileManagerSelectionChanged(object recipient, FileManagerSelectionChangedMessage message) => RefreshForFileManagerEntry(message.Entry, message.Document);

    /// <summary>
    /// <para>响应对比视图行选中变更消息，刷新属性行展示选中条目详情。</para>
    /// Responds to compare-view row selection changed messages by refreshing property rows with the selected entry.
    /// </summary>
    private void OnCompareSelectionChanged(object recipient, CompareSelectionChangedMessage message) => RefreshForCompareEntry(message.Path, message.IsDirectory);

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
        RefreshActiveDocument(message);
        ObserveDocument(message.ActiveDocument);
    }

    private void RefreshActiveDocument(ActiveDocumentChangedMessage message)
    {
        if (RefreshTypedDocument(message)) return;

        var doc = message.Document;
        if (doc is null)
        {
            if (ContentType == PropertiesContentType.FileAnalysis
                || ContentType == PropertiesContentType.TextDocument
                || ContentType == PropertiesContentType.DtbDocument
                || ContentType == PropertiesContentType.DexDocument
                || ContentType == PropertiesContentType.ManifestDocument
                || ContentType == PropertiesContentType.ArscDocument
                || ContentType == PropertiesContentType.ApkDocument
                || ContentType == PropertiesContentType.FirmwareDocument
                || ContentType == PropertiesContentType.HexDocument
                || ContentType == PropertiesContentType.FileManagerDocument
                || ContentType == PropertiesContentType.FileManagerEntry
                || ContentType == PropertiesContentType.CompareEntry
                || ContentType == PropertiesContentType.ScreenMirror
                || ContentType == PropertiesContentType.ElfDocument
                || ContentType == PropertiesContentType.PartitionDocument
                || ContentType == PropertiesContentType.LogDocument)
            {
                ClearProperties();
                ResetSelection();
            }
            return;
        }

        if (doc.IsBinary)
        {
            RefreshForFileAnalysis(new FileAnalysisCompletedMessage(
                [.. doc.Analysis],
                doc.DetectedFormat,
                doc.FilePath));
            return;
        }

        RefreshForTextDocument(doc);
    }

    /// <summary>Dispatches typed document properties in the existing precedence order.</summary>
    private bool RefreshTypedDocument(ActiveDocumentChangedMessage message)
    {
        switch (message)
        {
            case { ActiveDocument: ElfInfoViewModel elf }: RefreshForElfDocument(elf); return true;
            case { ActiveDocument: PartitionEditorViewModel partition }: RefreshForPartitionDocument(partition); return true;
            case { ActiveDocument: LogEditorViewModel log }: RefreshForLogDocument(log); return true;
            case { ActiveDocument: ScreenMirrorViewModel mirror }: RefreshForScreenMirror(mirror); return true;
            case { DtbDocument: { SelectedNode: { } node } dtb }: RefreshForDtbNode(dtb, node); return true;
            case { DtbDocument: { } dtb }: RefreshForDtbDocument(dtb); return true;
            case { DexDocument: { } dex }: RefreshForDexDocument(dex); return true;
            case { ManifestDocument: { SelectedNode: { } node } manifest }: RefreshForManifestNode(manifest, node); return true;
            case { ManifestDocument: { } manifest }: RefreshForManifestDocument(manifest); return true;
            case { ArscDocument: { SelectedNode: { } node } arsc }: RefreshForArscNode(arsc, node); return true;
            case { ArscDocument: { } arsc }: RefreshForArscDocument(arsc); return true;
            case { ApkDocument: { } apk }: RefreshForApkDocument(apk); return true;
            case { FirmwareDocument: { } firmware }: RefreshForFirmwareDocument(firmware); return true;
            case { HexDocument: { } hex }: RefreshForHexDocument(hex); return true;
            case { FileManagerDocument: { } manager }: RefreshForFileManagerEntry(manager.SelectedEntry, manager); return true;
            default: return false;
        }
    }

    /// <summary>
    /// <para>响应设备树节点选中变更消息，刷新选中节点详情。</para>
    /// Responds to DTB node-selection-changed messages by refreshing the selected node details.
    /// </summary>
    private void OnDtbNodeSelectionChanged(object recipient, DtbNodeSelectionChangedMessage message)
    {

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
    /// <para>刷新指定镜像的详细参数。镜像为 <c>null</c> 时显示占位。</para>
    /// Refreshes detailed parameters for the specified image. Shows a placeholder when the image is <c>null</c>.
    /// </summary>
    public void RefreshForImage(ImageEntryViewModel? image)
    {
        ClearProperties();
        if (image is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.Image);
        SelectedNodeTitle = image.DisplayName;

        Properties.Add(CreateLocalizedRow("Dock_Properties_ImageName", image.DisplayName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Path", image.RelativePath));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Format", image.Format));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Size", $"{image.SizeBytes:N0} bytes"));

        var imageDetailsGeneration = Volatile.Read(ref _imageDetailsGeneration);
        _ = RefreshImageDetailsAsync(image, imageDetailsGeneration);

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for image '{image.DisplayName}'");
    }

    /// <summary>
    /// <para>刷新文本文档的属性行（文件名、路径、大小、编码、行数、字符数、语法语言）。</para>
    /// Refreshes property rows for a text document (file name, path, size, encoding, line count,
    /// character count, syntax language).
    /// </summary>
    public void RefreshForTextDocument(FileViewModel doc)
    {
        ClearProperties();
        if (doc is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.TextDocument);
        SelectedNodeTitle = doc.FilePath ?? doc.Title;
        AddMagicRow(doc.FilePath);

        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Text_FileName", fileName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Text_FilePath", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Text_FileSize", $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Text_LineCount", doc.LineCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Text_CharCount", doc.CharCount.ToString("N0")));

        _currentTextDocument = doc;
        PopulateEncodingSelector(doc);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Text_Encoding", string.Empty, isEncodingSelector: true));

        PopulateLanguageSelector(doc.Language);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Text_Language", string.Empty, isLanguageSelector: true));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for text document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>当用户在编码下拉栏中选择新编码时，回写到当前文本文档（保存时按新编码写盘）。</para>
    /// When the user selects a new encoding in the dropdown, writes it back to the current text
    /// document (the next save writes the file with the chosen encoding).
    /// </summary>
    partial void OnSelectedEncodingChanged(string value)
    {
        if (_suppressEncodingChange) return;

        if (_currentHexDocument is { } hex)
        {
            if (!string.IsNullOrEmpty(value))
            {
                hex.ApplyEncoding(value);
            }

            return;
        }

        if (_currentTextDocument is not { } doc) return;
        if (string.IsNullOrEmpty(value) || (AvailableEncodings.Count > 0 && value == AvailableEncodings[0]))
        {

            return;
        }

        doc.ApplyEncoding(value);
    }

    /// <summary>
    /// <para>填充语言下拉栏并根据当前语言标识选中对应项。</para>
    /// Populates the language dropdown and selects the item matching the current language id.
    /// </summary>
    private void PopulateLanguageSelector(string? currentLanguageId)
    {
        AvailableLanguages.Clear();
        var svc = LanguageService.Instance;
        var autoLabel = svc.GetString("Dock_Properties_Text_LanguageAuto");
        AvailableLanguages.Add(string.IsNullOrEmpty(autoLabel) ? "Auto Detect" : autoLabel);

        foreach (var lang in SyntaxLanguageRegistry.GetAllLanguages())
        {
            AvailableLanguages.Add(lang.DisplayName);
        }

        var match = SyntaxLanguageRegistry.GetAllLanguages().FirstOrDefault(l => l.Id == currentLanguageId);
        _suppressLanguageChange = true;
        try
        {
            SelectedLanguage = match is not { Id.Length: > 0, DisplayName.Length: > 0 }
                ? AvailableLanguages[0]
                : match.DisplayName;
        }
        finally
        {
            _suppressLanguageChange = false;
        }

        HasLanguageSelector = true;
    }

    /// <summary>
    /// <para>填充编码下拉栏（复用 <see cref="FileViewModel.SupportedEncodings"/>）并按文档当前编码选中对应项。</para>
    /// Populates the encoding dropdown (reusing <see cref="FileViewModel.SupportedEncodings"/>) and
    /// selects the entry matching the document's current encoding.
    /// </summary>
    private void PopulateEncodingSelector(FileViewModel doc)
    {
        AvailableEncodings.Clear();
        foreach (var encoding in FileViewModel.SupportedEncodings)
        {
            AvailableEncodings.Add(encoding);
        }

        _suppressEncodingChange = true;
        try
        {
            SelectedEncoding = doc.DetectedEncoding is { } current && AvailableEncodings.Contains(current)
                ? current
                : AvailableEncodings[0];
        }
        finally
        {
            _suppressEncodingChange = false;
        }
    }

    /// <summary>
    /// <para>语言选择器变更抑制标志（程序化设置 <see cref="SelectedLanguage"/> 时避免触发回写）。</para>
    /// Language-selector change suppression flag (prevents write-back when programmatically setting
    /// <see cref="SelectedLanguage"/>).
    /// </summary>
    private bool _suppressLanguageChange;

    /// <summary>
    /// <para>清除语言选择器状态（隐藏下拉栏、清空引用与列表）。</para>
    /// Clears the language-selector state (hides the dropdown, clears reference and list).
    /// </summary>
    private void ClearLanguageSelector()
    {
        _currentTextDocument = null;
        _currentHexDocument = null;
        HasLanguageSelector = false;
        AvailableLanguages.Clear();
        _suppressLanguageChange = true;
        try
        {
            SelectedLanguage = string.Empty;
        }
        finally
        {
            _suppressLanguageChange = false;
        }

        AvailableEncodings.Clear();
        _suppressEncodingChange = true;
        try
        {
            SelectedEncoding = string.Empty;
        }
        finally
        {
            _suppressEncodingChange = false;
        }
    }

    /// <summary>Resets selection state after the caller has cleared its own property rows.</summary>
    private void ResetSelection()
    {
        SelectedNodeTitle = string.Empty;
        HasSelection = false;
        ContentType = PropertiesContentType.None;
    }

    /// <summary>
    /// <para>清空全部属性行并复位语言选择器。</para>
    /// Clears all property rows and resets the language selector.
    /// </summary>
    private void ClearProperties()
    {
        StopObservingDocument();
        Interlocked.Increment(ref _imageDetailsGeneration);
        StopMirrorUpdates();
        ClearLanguageSelector();
        Properties.Clear();
    }

    /// <summary>
    /// <para>设置当前属性内容类型并添加类型行。</para>
    /// Sets the current properties content type and adds the type row.
    /// </summary>
    private void ShowProperties(PropertiesContentType contentType)
    {
        ContentType = contentType;
        HasSelection = true;
        Properties.Add(CreateLocalizedRow("Dock_Properties_Type", ContentTypeDisplay));
    }

    /// <summary>
    /// <para>刷新设备树文档的属性行（文件名、路径、大小、节点总数、根节点 compatible、
    /// #address-cells、#size-cells、内存保留条目数）。</para>
    /// Refreshes property rows for a device-tree document (file name, path, size, total node count,
    /// root compatible, #address-cells, #size-cells, memory-reservation count).
    /// </summary>
    public void RefreshForDtbDocument(DtbEditorViewModel doc)
    {
        ClearProperties();
        if (doc is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.DtbDocument);
        AddMagicRow(doc.FilePath);
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_FileName", fileName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_FilePath", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_FileSize", $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_NodeCount", doc.NodeCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_RootCompatible", doc.RootCompatible));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_RootAddressCells", doc.RootAddressCells));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_RootSizeCells", doc.RootSizeCells));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_MemReservations", doc.MemoryReservationCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for DTB document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新 Manifest 文档的属性行（文件名、路径、大小、元素/属性/字符串计数）。</para>
    /// Refreshes property rows for a Manifest document (file name, path, size, element/attribute/
    /// string counts).
    /// </summary>
    public void RefreshForManifestDocument(ManifestEditorViewModel doc)
    {
        ClearProperties();
        if (doc is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.ManifestDocument);
        AddMagicRow(doc.FilePath);
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_FileName", fileName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_FilePath", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_FileSize", $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_ElementCount", doc.ElementCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_AttributeCount", doc.AttributeCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for Manifest document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新 Manifest 选中元素的详情属性行（元素名、命名空间、属性数、子元素数、文本）。</para>
    /// Refreshes property rows for the selected Manifest element (name, namespace, attribute count,
    /// child count, text).
    /// </summary>
    public void RefreshForManifestNode(ManifestEditorViewModel editor, ManifestTreeNodeViewModel? node)
    {
        ClearProperties();
        if (node is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.ManifestDocument);
        SelectedNodeTitle = node.Name;

        var element = node.Element;
        Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_SelectedNode", node.Name));
        if (element.Namespace is { Length: > 0 })
        {
            Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_SelectedNodeNs", element.Namespace));
        }
        Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_SelectedNodeAttrs", element.Attributes.Count.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_SelectedNodeChildren", element.Children.Count.ToString("N0")));
        if (element.Text is { Length: > 0 })
        {
            Properties.Add(CreateLocalizedRow("Dock_Properties_Manifest_SelectedNodeText", element.Text));
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for Manifest node '{node.Name}'");
    }

    /// <summary>
    /// <para>刷新 APK 文档的属性行（文件名、路径、大小、包名、版本、SDK、ABI、权限数）。</para>
    /// Refreshes property rows for an APK document (file name, path, size, package name, version,
    /// SDK, minimum system, ABIs, permission count).
    /// </summary>
    public void RefreshForApkDocument(ApkEditorViewModel doc)
    {
        ClearProperties();
        if (doc is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.ApkDocument);
        AddMagicRow(doc.FilePath);
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Apk_FileName", fileName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Apk_FilePath", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Apk_FileSize", doc.SizeDisplay));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Apk_PackageName", doc.PackageName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Apk_VersionName", doc.VersionName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Apk_VersionCode", doc.VersionCodeDisplay));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Apk_Permissions", doc.Info?.Permissions.Count.ToString("N0") ?? "0"));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for APK document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新固件信息文档的属性行（文件名、路径、格式名、摘要、元数据键值与组件/条目计数）。</para>
    /// Refreshes property rows for a firmware-info document (file name, path, format name, summary,
    /// metadata key-values and component/entry counts).
    /// </summary>
    public void RefreshForFirmwareDocument(FirmwareInfoViewModel doc)
    {
        ClearProperties();
        if (doc is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.FirmwareDocument);
        AddMagicRow(doc.FilePath);
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Fw_FileName", fileName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Fw_FilePath", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Fw_Format", doc.FormatName));
        if (!string.IsNullOrEmpty(doc.Summary))
        {
            Properties.Add(CreateLocalizedRow("Dock_Properties_Fw_Summary", doc.Summary));
        }
        Properties.Add(CreateLocalizedRow("Dock_Properties_Fw_MetadataCount", doc.Metadata.Count.ToString("N0")));
        if (doc.HasComponents)
        {
            Properties.Add(CreateLocalizedRow("Dock_Properties_Fw_Components", doc.Components.Count.ToString("N0")));
        }

        foreach (var row in doc.Metadata)
        {
            Properties.Add(CreateDynamicRow(row.Name, row.Value));
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for firmware document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新 Hex 文档的属性行（文件名、路径、文件大小、字节数）。</para>
    /// Refreshes property rows for a Hex document (file name, path, file size, byte count).
    /// </summary>
    public void RefreshForHexDocument(HexEditorViewModel doc)
    {
        ClearProperties();
        if (doc is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.HexDocument);
        AddMagicRow(doc.FilePath);
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Hex_FileName", fileName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Hex_FilePath", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Hex_FileSize", $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Hex_ByteCount", doc.SizeDisplay));

        _currentHexDocument = doc;
        PopulateHexEncodingSelector(doc.EncodingName);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Text_Encoding", string.Empty, isEncodingSelector: true));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for Hex document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>填充 Hex 文档的编码下拉栏并选中当前编码。</para>
    /// Populates the Hex document's encoding dropdown and selects the current encoding.
    /// </summary>
    private void PopulateHexEncodingSelector(string currentEncoding)
    {
        AvailableEncodings.Clear();
        foreach (var encoding in FileViewModel.SupportedEncodings)
        {
            AvailableEncodings.Add(encoding);
        }

        _suppressEncodingChange = true;
        try
        {
            SelectedEncoding = AvailableEncodings.Contains(currentEncoding)
                ? currentEncoding
                : AvailableEncodings[0];
        }
        finally
        {
            _suppressEncodingChange = false;
        }
    }

    /// <summary>
    /// <para>刷新 ARSC 文档的属性行（文件名、路径、包/类型表/条目计数）。</para>
    /// Refreshes property rows for an ARSC document (file name, path, package/type-table/entry counts).
    /// </summary>
    public void RefreshForArscDocument(ArscEditorViewModel doc)
    {
        ClearProperties();
        if (doc is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.ArscDocument);
        AddMagicRow(doc.FilePath);
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_FileName", fileName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_FilePath", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_PackageCount", doc.PackageCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_TypeCount", doc.TypeCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_EntryCount", doc.EntryCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for ARSC document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新 ARSC 选中节点的详情属性行（按包/类型/条目层级展示）。</para>
    /// Refreshes property rows for the selected ARSC node (per package/type/entry level).
    /// </summary>
    public void RefreshForArscNode(ArscEditorViewModel editor, ArscTreeNodeViewModel? node)
    {
        ClearProperties();
        if (node is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.ArscDocument);
        SelectedNodeTitle = node.Name;

        switch (node.Payload)
        {
            case UotanStudio.Firmware.AndroidMeta.Arsc.ResPackage pkg:
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNode", pkg.Name));
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNodeKind", $"0x{pkg.Id:x2}"));
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNodeChildren", pkg.Types.Count.ToString("N0")));
                break;

            case UotanStudio.Firmware.AndroidMeta.Arsc.ResType type:
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNode", $"0x{type.Id:x2}"));
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNodeKind", "type"));
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNodeDetail", type.ConfigDescription));
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNodeChildren", type.Entries.Count.ToString("N0")));
                break;

            case UotanStudio.ViewModels.Editors.ResEntryEntry entry:
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNode", entry.Key));
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNodeKind", entry.Entry.Value?.Type.ToString() ?? "<null>"));
                Properties.Add(CreateLocalizedRow("Dock_Properties_Arsc_SelectedNodeDetail", entry.ValueText));
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
        ClearProperties();
        if (node is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.DtbDocument);
        SelectedNodeTitle = node.FullPath;

        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_SelectedNode", node.Name));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_SelectedNodePath", node.FullPath));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_SelectedNodeProps", node.Node.Properties.Count.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dtb_SelectedNodeChildren", node.Node.Children.Count.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for DTB node '{node.FullPath}'");
    }

    /// <summary>
    /// <para>刷新 DEX 文档的属性行（文件名、路径、大小、版本、类/方法/字段/字符串/类型计数）。</para>
    /// Refreshes property rows for a DEX document (file name, path, size, version, and class/method/
    /// field/string/type counts).
    /// </summary>
    public void RefreshForDexDocument(DexEditorViewModel doc)
    {
        ClearProperties();
        if (doc is null)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.DexDocument);
        AddMagicRow(doc.FilePath);
        SelectedNodeTitle = doc.FilePath ?? doc.Title;

        var fileName = string.IsNullOrEmpty(doc.FilePath) ? doc.Title : Path.GetFileName(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_FileName", fileName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_FilePath", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_FileSize", $"{(doc.RawBytes?.Length ?? 0):N0} bytes"));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_Version", doc.Version));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_ClassCount", doc.ClassCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_MethodCount", doc.MethodCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_FieldCount", doc.FieldCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_StringCount", doc.StringCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Dex_TypeCount", doc.TypeCount.ToString("N0")));

        if (!string.IsNullOrEmpty(doc.SelectedEntry))
        {
            Properties.Add(CreateLocalizedRow("Dock_Properties_SelectedEntry", doc.SelectedEntry));
        }
        if (doc.SelectedResource is { } resource)
        {
            Properties.Add(CreateLocalizedRow("Dock_Properties_SelectedResource", resource.Name));
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for DEX document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新文件管理器文档的属性行（镜像路径、格式、大小、文件/目录数）。</para>
    /// Refreshes property rows for a file-manager document (image path, format, size, file/dir counts).
    /// </summary>
    public void RefreshForFileManagerDocument(FileManagerEditorViewModel doc)
    {
        ClearProperties();
        ContentType = PropertiesContentType.FileManagerDocument;
        HasSelection = true;
        SelectedNodeTitle = doc.Title;

        AddMagicRow(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_FileName", doc.Title));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Path", doc.FilePath ?? string.Empty));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Format", doc.FormatName));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Size", doc.ImageSizeText));

        Properties.Add(CreateLocalizedRow("Dock_Properties_FileCount", doc.FileCount.ToString("N0")));
        Properties.Add(CreateLocalizedRow("Dock_Properties_DirectoryCount", doc.DirectoryCount.ToString("N0")));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for file-manager document '{doc.FilePath}'");
    }

    /// <summary>
    /// <para>刷新文件管理器选中条目的属性行（名称、路径、大小、类型）。</para>
    /// Refreshes property rows for the selected file-manager entry (name, path, size, kind).
    /// </summary>
    public void RefreshForFileManagerEntry(FileManagerEntryViewModel? entry, FileManagerEditorViewModel doc)
    {
        if (entry is null)
        {

            RefreshForFileManagerDocument(doc);
            return;
        }

        ClearProperties();

        ContentType = PropertiesContentType.FileManagerEntry;
        HasSelection = true;
        SelectedNodeTitle = entry.Name;

        AddMagicRow(doc.FilePath);
        Properties.Add(CreateLocalizedRow("Dock_Properties_FileName", entry.Name));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Path", entry.Path));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Size", entry.SizeText));
        Properties.Add(CreateLocalizedRow("Dock_Properties_ImageType", entry.KindText));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for file-manager entry '{entry.Path}'");
    }

    /// <summary>
    /// <para>刷新对比视图选中条目的属性行（名称、路径、类型、大小）；路径为 <c>null</c> 时清空面板。</para>
    /// Refreshes property rows for the selected compare-view entry (name, path, kind, size); clears the
    /// panel when the path is <c>null</c>.
    /// </summary>
    public void RefreshForCompareEntry(string? path, bool isDirectory)
    {
        ClearProperties();
        if (string.IsNullOrEmpty(path))
        {
            ResetSelection();
            return;
        }

        ContentType = PropertiesContentType.CompareEntry;
        HasSelection = true;
        SelectedNodeTitle = System.IO.Path.GetFileName(path);
        AddMagicRow(isDirectory ? null : path);

        Properties.Add(CreateLocalizedRow("Dock_Properties_FileName", SelectedNodeTitle));
        Properties.Add(CreateLocalizedRow("Dock_Properties_Path", path));
        Properties.Add(CreateLocalizedRow("Dock_Properties_ImageType",
            LanguageService.Instance.GetString(isDirectory ? "FileManager_KindDirectory" : "FileManager_KindFile")));
        if (!isDirectory && File.Exists(path))
        {
            var size = new FileInfo(path).Length;
            Properties.Add(CreateLocalizedRow("Dock_Properties_Size", ProgressMeter.FormatSize(size)));
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for compare entry '{path}'");
    }

    /// <summary>
    /// <para>刷新指定文件分析结果的属性行。分析为 <c>null</c> 时显示占位。</para>
    /// Refreshes property rows for the specified file analysis results. Shows a placeholder when analysis is <c>null</c>.
    /// </summary>
    public void RefreshForFileAnalysis(FileAnalysisCompletedMessage? analysis)
    {
        ClearProperties();
        if (analysis is null || analysis.Analysis.Count == 0)
        {
            ResetSelection();
            return;
        }

        ShowProperties(PropertiesContentType.FileAnalysis);
        SelectedNodeTitle = analysis.FilePath ?? analysis.DetectedFormat ?? "Analysis";
        AddMagicRow(analysis.FilePath);

        foreach (var row in analysis.Analysis)
        {
            Properties.Add(CreateDynamicRow(row.Name, row.Value));
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for file analysis '{analysis.FilePath}' ({analysis.Analysis.Count} entries)");
    }

    private void AddMagicRow(string? path)
    {
        var description = "Unknown";
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                description = FileMagic.IdentifyFile(path)?.Description ?? "Unknown";
            }
            catch (IOException)
            {
                description = "Unavailable";
            }
            catch (UnauthorizedAccessException)
            {
                description = "Unavailable";
            }
        }

        Properties.Add(CreateLocalizedRow("Dock_Properties_Magic", description));
    }

    /// <summary>
    /// <para>异步读取固件详细参数并追加到属性行。</para>
    /// Asynchronously reads firmware detailed parameters and appends them to the property rows.
    /// </summary>
    private async Task RefreshImageDetailsAsync(ImageEntryViewModel image, long generation)
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

            var result = await Task.Run(() => handler.ReadMetadataAsync(absolutePath)).ConfigureAwait(true);
            if (!IsCurrentImageDetailsRequest(image, generation) || !result.Success)
            {
                return;
            }

            if (result.Metadata != null)
            {
                foreach (var kv in result.Metadata)
                {
                    if (!IsCurrentImageDetailsRequest(image, generation))
                    {
                        return;
                    }

                    Properties.Add(CreateDynamicRow(kv.Key, kv.Value));
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Debug, "Properties", $"Failed to read image details: {ex.Message}");
        }
    }

    private bool IsCurrentImageDetailsRequest(ImageEntryViewModel image, long generation) =>
        Volatile.Read(ref _imageDetailsGeneration) == generation
        && ContentType == PropertiesContentType.Image;
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
    ApkDocument,

    /// <summary>
    /// <para>固件信息文档。</para>
    /// Firmware-info document.
    /// </summary>
    FirmwareDocument,

    /// <summary>
    /// <para>Hex 十六进制文档信息。</para>
    /// Hex document information.
    /// </summary>
    HexDocument,

    /// <summary>
    /// <para>文件管理器文档信息。</para>
    /// File-manager document information.
    /// </summary>
    FileManagerDocument,

    /// <summary>
    /// <para>文件管理器内部条目信息。</para>
    /// File-manager entry information.
    /// </summary>
    FileManagerEntry,

    /// <summary>
    /// <para>对比视图选中条目信息。</para>
    /// Compare-view selected entry information.
    /// </summary>
    CompareEntry,

    /// <summary>Live screen-mirror parameters and rates.</summary>
    ScreenMirror,

    /// <summary>ELF header or selected segment/section metadata.</summary>
    ElfDocument,

    /// <summary>Partition table or selected partition.</summary>
    PartitionDocument,

    /// <summary>Offline log document and filter state.</summary>
    LogDocument
}