using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.AndroidMeta.Resources;
using UotanStudio.Services;
using UotanStudio.Services.Workspace;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Manifest (AndroidManifest.xml / AXML) editor view model: a left element tree plus a right
/// attribute-editing / XML-preview pane, supporting parse, visual editing and rebuild-write.
/// <para>Manifest（AndroidManifest.xml / AXML）编辑器视图模型：左栏元素结构树 +
/// 右栏选中元素的属性编辑与 XML 文本预览，支持解析、可视化编辑与重建写出。</para>
/// </summary>
public partial class ManifestEditorViewModel : Document, ITool, ICommandSaveableDocument, ISearchableDocument
{
    /// <summary>
    /// The localized title of the save button.
    /// <para>保存按钮的本地化标题。</para>
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Manifest_Save");

    /// <summary>
    /// The localized title of the busy/loading label.
    /// <para>加载中提示文案的本地化标题。</para>
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Manifest_Busy");

    /// <summary>
    /// Whether the background parse is running (drives the busy indicator animation).
    /// <para>是否正在后台解析（联动忙碌指示动画）。</para>
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// The file path of the document.
    /// <para>文档对应的文件路径。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePathDisplay))]
    private string? _filePath;

    /// <summary>
    /// Localized file path display text (top toolbar).
    /// <para>本地化后的文件路径显示文本（顶部工具栏）。</para>
    /// </summary>
    public string FilePathDisplay =>
        string.IsNullOrEmpty(FilePath)
            ? string.Empty
            : string.Format(
                LanguageService.Instance.GetString("Dock_File_PathFormat"),
                FilePath);

    /// <summary>
    /// The XML text preview (editable; recompiled back to AXML on save using the original model's
    /// types as reference).
    /// <para>XML 文本预览（可编辑；保存时按原模型类型参照重编译回 AXML）。</para>
    /// </summary>
    [ObservableProperty] private string _source = string.Empty;

    /// <summary>
    /// Whether the XML text was modified by the editor (decides whether save must recompile).
    /// <para>XML 文本是否被编辑器修改过（决定保存时是否需要重编译）。</para>
    /// </summary>
    private bool _sourceEdited;

    /// <summary>
    /// Suppresses the text-change dirty flag (during load, cleanup and model write-back).
    /// <para>抑制文本变更脏标记（加载、清理与模型回写期间）。</para>
    /// </summary>
    private bool _suppressSourceEdit;

    /// <summary>
    /// Active XML preview editor for workspace actions.
    /// <para>供工作区操作使用的 XML 预览编辑器。</para>
    /// </summary>
    internal AvaloniaEdit.TextEditor? Editor { get; set; }

    partial void OnSourceChanged(string value)
    {
        if (_suppressSourceEdit)
        {
            return;
        }

        _sourceEdited = true;
        IsDirty = true;
    }

    /// <inheritdoc/>
    string? ISearchableDocument.SearchText => Source;

    /// <inheritdoc/>
    string? ISearchableDocument.FilePath => FilePath;

    /// <inheritdoc/>
    AvaloniaEdit.TextEditor? ISearchableDocument.Editor => Editor;

    /// <inheritdoc/>
    bool ISearchableDocument.SupportsSearchReplacement => true;

    /// <inheritdoc/>
    void ISearchableDocument.ApplySearchText(string text) => Source = text;

    /// <summary>
    /// The last parse error text; empty means no error.
    /// <para>最后一次解析错误文本；为空表示无错误。</para>
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// The original raw bytes (used for write-back and refresh).
    /// <para>原始字节数据（用于回写与刷新）。</para>
    /// </summary>
    [ObservableProperty] private byte[]? _rawBytes;

    /// <summary>
    /// Document dirty state.
    /// <para>文档已修改状态。</para>
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    partial void OnIsDirtyChanged(bool value) => IsModified = value;

    /// <summary>
    /// The currently selected tree node (left-pane selection linkage, drives the right properties pane).
    /// <para>当前选中的树节点（左栏选择联动，驱动右栏属性面板）。</para>
    /// </summary>
    [ObservableProperty] private ManifestTreeNodeViewModel? _selectedNode;

    /// <summary>
    /// Left-pane structure tree root nodes.
    /// <para>左栏结构树根节点（manifest 元素）。</para>
    /// </summary>
    public ObservableCollection<ManifestTreeNodeViewModel> Nodes { get; } = [];

    /// <summary>
    /// Attribute rows of the selected element (right-pane attribute editing).
    /// <para>选中元素的属性行（右栏属性编辑）。</para>
    /// </summary>
    public ObservableCollection<ManifestAttributeRow> Attributes { get; } = [];

    /// <summary>
    /// The parsed AXML document (used for rebuild).
    /// <para>解析后的 AXML 文档（用于重建）。</para>
    /// </summary>
    private AXmlDocument? _doc;

    /// <summary>
    /// Total element count; 0 when not loaded.
    /// <para>元素总数；未加载时为 0。</para>
    /// </summary>
    public int ElementCount { get; private set; }

    /// <summary>
    /// Total attribute count; 0 when not loaded.
    /// <para>属性总数；未加载时为 0。</para>
    /// </summary>
    public int AttributeCount { get; private set; }

    /// <summary>
    /// Save command: rebuilds the AXML bytes from the model and writes them back to the file.
    /// <para>保存命令：从模型重建 AXML 字节并写回原文件。</para>
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// Creates the Manifest editor view model and initializes the save command.
    /// <para>创建 Manifest 编辑器视图模型并初始化保存命令。</para>
    /// </summary>
    public ManifestEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    /// <summary>
    /// Asynchronously loads an AXML file: read bytes, parse, and build the tree and XML text.
    /// <para>异步加载 AXML 文件：读取字节 → 解析 → 构建结构树与 XML 文本。</para>
    /// </summary>
    /// <param name="path">AXML 文件路径。<para>AXML file path.</para></param>
    public async Task LoadAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        Title = Path.GetFileName(path);

        IsBusy = true;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            RawBytes = bytes;

            AXmlDocument doc;
            try
            {
                doc = await Task.Run(() => AXmlParser.Parse(bytes), default).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                CompileError = UserError.Describe(ex, "UserError_ParseFailed", "AXML");
                Nodes.Clear();
                Source = string.Empty;
                return;
            }

            _doc = doc;
            ElementCount = 0;
            AttributeCount = 0;

            Nodes.Clear();
            var rootNode = BuildNode(doc.Root);
            rootNode.IsExpanded = true;
            Nodes.Add(rootNode);

            _suppressSourceEdit = true;
            try
            {
                Source = doc.ToXml();
            }
            finally
            {
                _suppressSourceEdit = false;
            }

            _sourceEdited = false;
            CompileError = string.Empty;
            IsDirty = false;

            RawBytes = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves: rebuilds the AXML bytes from the current model and writes them back to the file.
    /// <para>保存：从当前模型重建 AXML 字节并写回原文件。</para>
    /// </summary>
    public async Task SaveAsync()
    {
        if (IsBusy || _doc is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        var temporary = FilePath + ".save-" + Guid.NewGuid().ToString("N");
        IsBusy = true;
        try
        {
            if (_sourceEdited)
            {
                AXmlDocument recompiled;
                try
                {
                    recompiled = AXmlTextFormat.Parse(Source, _doc);
                }
                catch (Exception ex)
                {
                    CompileError = UserError.Describe(ex, "UserError_RebuildFailed", "XML");
                    return;
                }

                _doc = recompiled;
                _suppressSourceEdit = true;
                try
                {
                    Source = _doc.ToXml();
                }
                finally
                {
                    _suppressSourceEdit = false;
                }

                _sourceEdited = false;
            }

            var outBytes = await Task.Run(() => AXmlWriter.Write(_doc), default).ConfigureAwait(true);
            await File.WriteAllBytesAsync(temporary, outBytes).ConfigureAwait(true);
            File.Move(temporary, FilePath, overwrite: true);
            RawBytes = outBytes;
            IsDirty = false;
            CompileError = string.Empty;
            LogService.Instance.Write(LogLevel.Info, "ManifestEditor", $"Rebuilt AXML written to {FilePath}");
        }
        catch (Exception ex)
        {
            CompileError = UserError.Describe(ex, "UserError_RebuildFailed", "AXML");
        }
        finally
        {
            IsBusy = false;
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Selected node changed: loads the element's attributes into the properties pane.
    /// <para>选中节点变化：把该元素的属性加载到属性面板并刷新统计。</para>
    /// </summary>
    partial void OnSelectedNodeChanged(ManifestTreeNodeViewModel? value)
    {
        Attributes.Clear();
        if (value is null)
        {
            return;
        }

        foreach (var attr in value.Element.Attributes)
        {
            Attributes.Add(new ManifestAttributeRow(attr, _doc?.StringPool, () =>
            {
                IsDirty = true;
                RefreshSourceFromModel();
            }));
        }
    }

    /// <summary>
    /// Writes model changes back into the XML preview text so the search text stays consistent with
    /// attribute-pane edits.
    /// <para>把模型变更回写到 XML 预览文本，使搜索文本与属性面板编辑保持一致。</para>
    /// </summary>
    private void RefreshSourceFromModel()
    {
        if (_doc is null)
        {
            return;
        }

        _suppressSourceEdit = true;
        try
        {
            Source = _doc.ToXml();
        }
        finally
        {
            _suppressSourceEdit = false;
        }
    }

    private ManifestTreeNodeViewModel BuildNode(AXmlElement element)
    {
        ElementCount++;
        AttributeCount += element.Attributes.Count;
        var node = new ManifestTreeNodeViewModel(element);
        foreach (var child in element.Children)
        {
            node.Children.Add(BuildNode(child));
        }
        return node;
    }

    /// <summary>
    /// Releases the model and view data held by this document (called when the document is closed).
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）。</para>
    /// </summary>
    public void Cleanup()
    {
        SelectedNode = null;
        Nodes.Clear();
        Attributes.Clear();
        _suppressSourceEdit = true;
        try
        {
            Source = string.Empty;
        }
        finally
        {
            _suppressSourceEdit = false;
        }

        _sourceEdited = false;
        Editor = null;
        CompileError = string.Empty;
        RawBytes = null;
        _doc = null;
        ElementCount = 0;
        AttributeCount = 0;
    }
}

/// <summary>
/// Manifest attribute row (right-pane editing): name + editable value, written back to the AXML element.
/// <para>Manifest 属性行（右栏属性编辑）：名称 + 可编辑值，修改时写回 AXML 元素。</para>
/// </summary>
public partial class ManifestAttributeRow : ObservableObject
{
    private readonly AXmlAttribute _attribute;
    private readonly AXmlStringPool? _stringPool;
    private readonly Action? _onChanged;

    /// <summary>
    /// Creates an attribute row.
    /// <para>创建属性行。</para>
    /// </summary>
    /// <param name="attribute">对应的 AXML 属性。<para>The backing AXML attribute.</para></param>
    /// <param name="stringPool">字符串池（值格式化用）。<para>The string pool (for value formatting).</para></param>
    /// <param name="onChanged">值变更时的回调（标记文档为脏）。<para>Callback invoked on value change (marks document dirty).</para></param>
    public ManifestAttributeRow(AXmlAttribute attribute, AXmlStringPool? stringPool, Action? onChanged = null)
    {
        _attribute = attribute;
        _stringPool = stringPool;
        _onChanged = onChanged;
        Name = attribute.Name;
        Value = attribute.FormatValue(stringPool);
    }

    /// <summary>属性名称。<para>Attribute name.</para></summary>
    public string Name { get; }

    /// <summary>
    /// The attribute value (editable).
    /// <para>属性值（可编辑）。</para>
    /// </summary>
    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value)
    {
        if (_attribute.RawValue is not null)
        {
            _attribute.RawValue = value;
            _onChanged?.Invoke();
            return;
        }

        _attribute.ValueData = _attribute.ValueType switch
        {
            AXmlValueType.IntDec when long.TryParse(value, out var dec) => unchecked((uint)dec),
            AXmlValueType.IntHex when value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                      && uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                                          System.Globalization.CultureInfo.InvariantCulture, out var hex) => hex,
            AXmlValueType.IntBoolean when bool.TryParse(value, out var b) => b ? 1u : 0u,
            AXmlValueType.String when _stringPool is not null => (uint)_stringPool.Intern(value),
            _ => _attribute.ValueData
        };

        _onChanged?.Invoke();
    }
}