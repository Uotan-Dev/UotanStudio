using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.AXml;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>Manifest（AndroidManifest.xml / AXML）编辑器视图模型：左栏元素结构树 +
/// 右栏选中元素的属性编辑与 XML 文本预览，支持解析、可视化编辑与重建写出。</para>
/// Manifest (AndroidManifest.xml / AXML) editor view model: a left element tree plus a right
/// attribute-editing / XML-preview pane, supporting parse, visual editing and rebuild-write.
/// </summary>
public partial class ManifestEditorViewModel : Document
{
    /// <summary>
    /// <para>保存按钮的本地化标题。</para>
    /// The localized title of the save button.
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Manifest_Save");

    /// <summary>
    /// <para>加载中提示文案的本地化标题。</para>
    /// The localized title of the busy/loading label.
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Manifest_Busy");

    /// <summary>
    /// <para>是否正在后台解析（联动忙碌指示动画）。</para>
    /// Whether the background parse is running (drives the busy indicator animation).
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>文档对应的文件路径。</para>
    /// The file path of the document.
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// <para>XML 文本预览（只读）。</para>
    /// The XML text preview (read-only).
    /// </summary>
    [ObservableProperty] private string _source = string.Empty;

    /// <summary>
    /// <para>最后一次解析错误文本；为空表示无错误。</para>
    /// The last parse error text; empty means no error.
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// <para>原始字节数据（用于回写与刷新）。</para>
    /// The original raw bytes (used for write-back and refresh).
    /// </summary>
    [ObservableProperty] private byte[]? _rawBytes;

    /// <summary>
    /// <para>文档已修改状态。</para>
    /// Document dirty state.
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// <para>当前选中的树节点（左栏选择联动，驱动右栏属性面板）。</para>
    /// The currently selected tree node (left-pane selection linkage, drives the right properties pane).
    /// </summary>
    [ObservableProperty] private ManifestTreeNodeViewModel? _selectedNode;

    /// <summary>
    /// <para>左栏结构树根节点（manifest 元素）。</para>
    /// Left-pane structure tree root nodes.
    /// </summary>
    public ObservableCollection<ManifestTreeNodeViewModel> Nodes { get; } = [];

    /// <summary>
    /// <para>选中元素的属性行（右栏属性编辑）。</para>
    /// Attribute rows of the selected element (right-pane attribute editing).
    /// </summary>
    public ObservableCollection<ManifestAttributeRow> Attributes { get; } = [];

    /// <summary>
    /// <para>解析后的 AXML 文档（用于重建）。</para>
    /// The parsed AXML document (used for rebuild).
    /// </summary>
    private AXmlDocument? _doc;

    /// <summary>
    /// <para>元素总数；未加载时为 0。</para>
    /// Total element count; 0 when not loaded.
    /// </summary>
    public int ElementCount { get; private set; }

    /// <summary>
    /// <para>属性总数；未加载时为 0。</para>
    /// Total attribute count; 0 when not loaded.
    /// </summary>
    public int AttributeCount { get; private set; }

    /// <summary>
    /// <para>保存命令：从模型重建 AXML 字节并写回原文件。</para>
    /// Save command: rebuilds the AXML bytes from the model and writes them back to the file.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// <para>创建 Manifest 编辑器视图模型并初始化保存命令。</para>
    /// Creates the Manifest editor view model and initializes the save command.
    /// </summary>
    public ManifestEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    /// <summary>
    /// <para>异步加载 AXML 文件：读取字节 → 解析 → 构建结构树与 XML 文本。</para>
    /// Asynchronously loads an AXML file: read bytes, parse, and build the tree and XML text.
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
                CompileError = $"Failed to parse AXML: {ex.Message}";
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

            Source = doc.ToXml();
            CompileError = string.Empty;
            IsDirty = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>保存：从当前模型重建 AXML 字节并写回原文件。</para>
    /// Saves: rebuilds the AXML bytes from the current model and writes them back to the file.
    /// </summary>
    public async Task SaveAsync()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        try
        {
            var outBytes = await Task.Run(() => AXmlWriter.Write(_doc), default).ConfigureAwait(true);
            await File.WriteAllBytesAsync(FilePath, outBytes).ConfigureAwait(true);
            RawBytes = outBytes;
            IsDirty = false;
            CompileError = string.Empty;
            LogService.Instance.Write(LogLevel.Info, "ManifestEditor", $"Rebuilt AXML written to {FilePath}");
        }
        catch (Exception ex)
        {
            CompileError = $"Failed to rebuild AXML: {ex.Message}";
        }
    }

    /// <summary>
    /// <para>选中节点变化：把该元素的属性加载到属性面板并刷新统计。</para>
    /// Selected node changed: loads the element's attributes into the properties pane.
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
            Attributes.Add(new ManifestAttributeRow(attr, _doc?.StringPool));
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
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）。</para>
    /// Releases the model and view data held by this document (called when the document is closed).
    /// </summary>
    public void Cleanup()
    {
        SelectedNode = null;
        Nodes.Clear();
        Attributes.Clear();
        Source = string.Empty;
        CompileError = string.Empty;
        RawBytes = null;
        _doc = null;
        ElementCount = 0;
        AttributeCount = 0;
    }
}

/// <summary>
/// <para>Manifest 属性行（右栏属性编辑）：名称 + 可编辑值，修改时写回 AXML 元素。</para>
/// Manifest attribute row (right-pane editing): name + editable value, written back to the AXML element.
/// </summary>
public partial class ManifestAttributeRow : ObservableObject
{
    private readonly AXmlAttribute _attribute;
    private readonly AXmlStringPool? _stringPool;

    /// <summary>
    /// <para>创建属性行。</para>
    /// Creates an attribute row.
    /// </summary>
    /// <param name="attribute">对应的 AXML 属性。<para>The backing AXML attribute.</para></param>
    /// <param name="stringPool">字符串池（值格式化用）。<para>The string pool (for value formatting).</para></param>
    public ManifestAttributeRow(AXmlAttribute attribute, AXmlStringPool? stringPool)
    {
        _attribute = attribute;
        _stringPool = stringPool;
        Name = attribute.Name;
        Value = attribute.FormatValue(stringPool);
    }

    /// <summary>属性名称。<para>Attribute name.</para></summary>
    public string Name { get; }

    /// <summary>
    /// <para>属性值（可编辑）。</para>
    /// The attribute value (editable).
    /// </summary>
    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value)
    {
        // 写回模型：字符串型属性直接更新 RawValue（原始值）；否则尝试按类型解析。
        // Write back: string-typed attributes update RawValue; others try to parse by type.
        if (_attribute.RawValue is not null)
        {
            _attribute.RawValue = value;
            return;
        }

        switch (_attribute.ValueType)
        {
            case AXmlValueType.IntDec when long.TryParse(value, out var dec):
                _attribute.ValueData = unchecked((uint)dec);
                break;
            case AXmlValueType.IntHex when value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                           && uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                                               System.Globalization.CultureInfo.InvariantCulture, out var hex):
                _attribute.ValueData = hex;
                break;
            case AXmlValueType.IntBoolean when bool.TryParse(value, out var b):
                _attribute.ValueData = b ? 1u : 0u;
                break;
            case AXmlValueType.String when _stringPool is not null:
                // 字符串池重建：把新值 Intern 进池并更新 ValueData 索引，保存时由 AXmlWriter
                // 按池顺序重建，任意字符串值均可编辑。
                // String-pool rebuild: intern the new value and update the ValueData index; the
                // AXmlWriter rebuilds the pool on save, so arbitrary string values are editable.
                _attribute.ValueData = (uint)_stringPool.Intern(value);
                break;
        }
    }
}