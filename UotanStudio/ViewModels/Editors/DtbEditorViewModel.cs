using System.Buffers.Binary;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.DeviceTree.Dt;
using UotanStudio.Messages;
using UotanStudio.Services.Workspace;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// DTB device-tree dual-pane editor view model: a left device-node tree plus an editable DTS source pane,
/// with bidirectional linkage and recompile-save (DTS→DTB).
/// <para>DTB 设备树双栏编辑器视图模型：左侧设备节点树 + 右侧可编辑 DTS 源码，
/// 支持双向联动与回编译保存（DTS→DTB）。</para>
/// </summary>
public partial class DtbEditorViewModel : Document, IEditable, ICommandSaveableDocument, ISearchableDocument
{
    private Dictionary<string, int> _lineMap = [];

    /// <summary>
    /// The localized title of the save button.
    /// <para>保存按钮的本地化标题。</para>
    /// </summary>
    public static string SaveLabel => UotanStudio.Services.LanguageService.Instance.GetString("Dock_Dtb_Save");

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
                UotanStudio.Services.LanguageService.Instance.GetString("Dock_File_PathFormat"),
                FilePath);

    /// <summary>
    /// The editable DTS source text.
    /// <para>可编辑的 DTS 源码文本。</para>
    /// </summary>
    [ObservableProperty] private string _source = string.Empty;

    /// <summary>
    /// The device-node tree (left pane binding).
    /// <para>设备树节点树（左栏绑定）。</para>
    /// </summary>
    public ObservableCollection<DtbNodeViewModel> Nodes { get; } = [];

    /// <summary>
    /// The currently selected node (left-pane selection linkage).
    /// <para>当前选中的节点（左栏选择联动）。</para>
    /// </summary>
    [ObservableProperty] private DtbNodeViewModel? _selectedNode;

    /// <summary>
    /// The last compile/parse error text; empty means no error.
    /// <para>最后一次编译/解析错误文本；为空表示无错误。</para>
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// The original raw bytes (used for write-back and refresh).
    /// <para>原始字节数据（用于回写与刷新）。</para>
    /// </summary>
    [ObservableProperty] private byte[]? _rawBytes;

    /// <summary>Whether the source is a DTBO table containing one or more overlay blobs.</summary>
    [ObservableProperty] private bool _isDtboContainer;

    /// <summary>
    /// Document dirty state.
    /// <para>文档已修改状态。</para>
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    partial void OnIsDirtyChanged(bool value) => IsModified = value;

    /// <summary>
    /// Total node count of the device tree (including the root); 0 when not loaded.
    /// <para>设备树节点总数（含根节点）；未加载时为 0。</para>
    /// </summary>
    public int NodeCount { get; private set; }

    /// <summary>
    /// The root node's <c>compatible</c> property (comma-separated when multiple); empty string when
    /// absent.
    /// <para>根节点的 <c>compatible</c> 属性值（多个以逗号分隔）；无则返回空字符串。</para>
    /// </summary>
    public string RootCompatible { get; private set; } = string.Empty;

    /// <summary>
    /// The root node's <c>#address-cells</c> property; empty string when absent.
    /// <para>根节点的 <c>#address-cells</c> 属性值；无则返回空字符串。</para>
    /// </summary>
    public string RootAddressCells { get; private set; } = string.Empty;

    /// <summary>
    /// The root node's <c>#size-cells</c> property; empty string when absent.
    /// <para>根节点的 <c>#size-cells</c> 属性值；无则返回空字符串。</para>
    /// </summary>
    public string RootSizeCells { get; private set; } = string.Empty;

    /// <summary>
    /// Number of memory reservation entries; 0 when not loaded.
    /// <para>设备树内存保留条目数；未加载时为 0。</para>
    /// </summary>
    public int MemoryReservationCount { get; private set; }

    /// <summary>
    /// The full path of the currently selected node; empty string when none is selected.
    /// <para>当前选中节点的完整路径；无选中时为空字符串。</para>
    /// </summary>
    public string SelectedNodePath => SelectedNode?.FullPath ?? string.Empty;

    /// <summary>
    /// Property count of the currently selected node; 0 when none is selected.
    /// <para>当前选中节点的属性数；无选中时为 0。</para>
    /// </summary>
    public int SelectedNodePropertyCount => SelectedNode?.Node.Properties.Count ?? 0;

    /// <summary>
    /// Child count of the currently selected node; 0 when none is selected.
    /// <para>当前选中节点的子节点数；无选中时为 0。</para>
    /// </summary>
    public int SelectedNodeChildCount => SelectedNode?.Node.Children.Count ?? 0;

    /// <summary>
    /// Save command: parse the DTS source, recompile it to a DTB and write it back to the file.
    /// <para>保存命令：将 DTS 源码解析并回编译为 DTB 写回原文件。</para>
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// Creates the DTB editor view model and initializes the save command.
    /// <para>创建 DTB 编辑器视图模型并初始化保存命令。</para>
    /// </summary>
    public DtbEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    partial void OnSourceChanged(string value)
    {
        IsDirty = true;
    }

    /// <summary>
    /// Asynchronously loads a DTB file: read bytes, parse the device tree, and produce the DTS source and node tree.
    /// <para>异步加载 DTB 文件：读取字节 → 解析设备树 → 生成 DTS 源码与节点树。</para>
    /// </summary>
    /// <param name="path">
    /// The DTB file path.
    /// <para>DTB 文件路径。</para>
    /// </param>
    public async Task LoadAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        Title = Path.GetFileName(path);

        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        RawBytes = bytes;

        DeviceTree? tree;
        try
        {
            if (bytes.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)) == FdtConstants.DtboMagic)
            {
                var (_, _, dtbs) = DtboFormat.Read(bytes);
                if (dtbs.Count == 0)
                {
                    throw new InvalidDataException("DTBO image contains no overlay entries.");
                }

                IsDtboContainer = true;
                tree = DtbFormat.Read(dtbs[0]);
            }
            else
            {
                IsDtboContainer = false;
                tree = DtbFormat.Read(bytes);
            }
        }
        catch (Exception ex)
        {
            CompileError = global::UotanStudio.Services.UserError.Describe(ex, "UserError_ParseFailed", "DTB");
            return;
        }

        Nodes.Clear();
        Nodes.Add(new DtbNodeViewModel(tree.Root));

        _lineMap = DtsFormat.GetNodeLineMap(tree);
        Source = DtsFormat.Write(tree);
        CompileError = string.Empty;
        IsDirty = false;

        RawBytes = null;

        UpdateTreeStatistics(tree);
    }

    /// <summary>
    /// Active source editor for workspace actions.
    /// <para>供工作区操作使用的源码编辑器。</para>
    /// </summary>
    internal AvaloniaEdit.TextEditor? Editor { get; set; }

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

    /// <inheritdoc/>
    public void Cut() => Editor?.Cut();
    /// <inheritdoc/>
    public void Copy() => Editor?.Copy();
    /// <inheritdoc/>
    public void Paste() => Editor?.Paste();
    /// <inheritdoc/>
    public void SelectAll() => Editor?.SelectAll();
    /// <inheritdoc/>
    public void Delete()
    {
        if (Editor is { SelectionLength: > 0 } editor) editor.SelectedText = string.Empty;
    }

    /// <summary>
    /// Saves: parses the DTS source, validates it, recompiles to DTB bytes, writes back to the file and refreshes.
    /// <para>保存：解析 DTS 源码 → 校验 → 回编译为 DTB 字节 → 写回原文件并刷新。</para>
    /// </summary>
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        if (IsDtboContainer)
        {
            CompileError = global::UotanStudio.Services.LanguageService.Instance.GetString("UserError_DtboReadOnly");
            return;
        }

        var source = Source;
        var result = DtsFormat.Parse(source);
        CompileError = result.IsSuccess
            ? string.Empty
            : global::UotanStudio.Services.UserError.WithDetails(string.Join("\n", result.Errors.Select(e => $"[L{e.Line}:C{e.Column}] {e.Severity}: {e.Message}")), "UserError_ParseFailed", "DTS");

        if (!result.IsSuccess || result.Tree is null)
        {
            return;
        }

        var outBytes = DtbFormat.Write(result.Tree);
        await File.WriteAllBytesAsync(FilePath, outBytes).ConfigureAwait(true);

        RawBytes = outBytes;
        _lineMap = DtsFormat.GetNodeLineMap(result.Tree);
        IsDirty = Source != source;
    }

    /// <summary>
    /// Expands and selects the node with the given full path in the left tree (source-caret linkage).
    /// <para>按节点完整路径展开并选中左栏树中的节点（右栏光标联动）。</para>
    /// </summary>
    /// <param name="fullPath">
    /// The node full path, e.g. /soc/i2c@0.
    /// <para>节点完整路径，如 /soc/i2c@0。</para>
    /// </param>
    public void TrySelectNode(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || fullPath == "/")
        {
            SelectedNode = Nodes.FirstOrDefault();
            return;
        }

        foreach (var node in Flatten(Nodes))
        {
            if (node.FullPath == fullPath)
            {
                foreach (var ancestor in Flatten(Nodes))
                {
                    if (fullPath.StartsWith(ancestor.FullPath + "/", System.StringComparison.Ordinal))
                    {
                        ancestor.IsExpanded = true;
                    }
                }

                node.IsSelected = true;
                SelectedNode = node;
                return;
            }
        }
    }

    /// <summary>
    /// Gets the DTS source start line for a node's full path (-1 if not found).
    /// <para>获取节点完整路径对应的 DTS 源码起始行（-1 表示未找到）。</para>
    /// </summary>
    /// <param name="fullPath">
    /// The node full path.
    /// <para>节点完整路径。</para>
    /// </param>
    public int GetNodeSourceLine(string fullPath) =>
        _lineMap.TryGetValue(fullPath, out var line) ? line : -1;

    /// <summary>
    /// Maps a source line back to its owning node (returns the full path; null when unmatched).
    /// <para>由源码行号反查所在节点（返回其完整路径；未命中返回 null）。</para>
    /// </summary>
    /// <param name="line">
    /// The 1-based source line.
    /// <para>1 基源码行号。</para>
    /// </param>
    public string? FindNodePathForLine(int line)
    {
        string? best = null;
        var bestLine = -1;
        foreach (var kv in _lineMap)
        {
            if (kv.Value <= line && kv.Value > bestLine)
            {
                best = kv.Key;
                bestLine = kv.Value;
            }
        }

        return best;
    }

    private static System.Collections.Generic.IEnumerable<DtbNodeViewModel> Flatten(
        System.Collections.Generic.IEnumerable<DtbNodeViewModel> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Recursively counts device-tree nodes and extracts the root's <c>compatible</c>,
    /// <c>#address-cells</c>, <c>#size-cells</c> plus memory-reservation count.
    /// <para>递归统计设备树节点总数，并提取根节点的 <c>compatible</c> / <c>#address-cells</c> /
    /// <c>#size-cells</c> 与内存保留条目数。</para>
    /// </summary>
    /// <param name="tree">
    /// The parsed device tree.
    /// <para>已解析的设备树。</para>
    /// </param>
    private void UpdateTreeStatistics(DeviceTree tree)
    {
        var total = 0;
        CountNodes(tree.Root, ref total);
        NodeCount = total;

        RootCompatible = GetPropertyAsString(tree.Root, "compatible");
        RootAddressCells = GetPropertyCellAsString(tree.Root, "#address-cells");
        RootSizeCells = GetPropertyCellAsString(tree.Root, "#size-cells");
        MemoryReservationCount = tree.MemoryReservations.Count;

        OnPropertyChanged(nameof(SelectedNodePath));
        OnPropertyChanged(nameof(SelectedNodePropertyCount));
        OnPropertyChanged(nameof(SelectedNodeChildCount));
    }

    /// <summary>
    /// Recursively counts nodes including <paramref name="node"/> itself.
    /// <para>递归统计节点数（含 <paramref name="node"/> 自身）。</para>
    /// </summary>
    private static void CountNodes(DeviceTreeNode node, ref int total)
    {
        total++;
        foreach (var child in node.Children)
        {
            CountNodes(child, ref total);
        }
    }

    /// <summary>
    /// Reads the named property as a string from the node; returns empty string when absent or empty.
    /// Multiple NUL-separated string values are rejoined with commas.
    /// <para>读取节点上指定属性的字符串值；属性缺失或为空时返回空字符串。
    /// 多个以 NUL 分隔的字符串值会用逗号重新连接。</para>
    /// </summary>
    private static string GetPropertyAsString(DeviceTreeNode node, string name)
    {
        foreach (var prop in node.Properties)
        {
            if (prop.Name == name && !prop.IsEmpty)
            {
                var raw = prop.Value;
                var end = raw.IndexOf((byte)0);
                if (end <= 0)
                {
                    return System.Text.Encoding.UTF8.GetString(raw);
                }

                List<string> parts = [];
                var span = raw;
                while (span.Length > 0)
                {
                    var idx = span.IndexOf((byte)0);
                    if (idx < 0)
                    {
                        parts.Add(System.Text.Encoding.UTF8.GetString(span));
                        break;
                    }

                    if (idx > 0)
                    {
                        parts.Add(System.Text.Encoding.UTF8.GetString(span[..idx]));
                    }

                    span = span[(idx + 1)..];
                }

                return string.Join(", ", parts);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Reads a 32-bit big-endian cell property on the node and returns its first cell as a decimal string;
    /// returns empty string when the property is absent, empty, or shorter than 4 bytes. Used for
    /// <c>#address-cells</c> / <c>#size-cells</c>.
    /// <para>读取节点上 32 位大端 cell 类型的属性并返回其首个 cell 的十进制字符串；
    /// 属性缺失、为空或不足 4 字节时返回空字符串。用于 <c>#address-cells</c> / <c>#size-cells</c> 等。</para>
    /// </summary>
    private static string GetPropertyCellAsString(DeviceTreeNode node, string name)
    {
        var prop = node.FindProperty(name);
        if (prop is null || prop.IsEmpty || prop.Length < 4)
        {
            return string.Empty;
        }

        return prop.GetCells()[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Releases the state held by this document (called when the document is closed).
    /// <para>释放文档持有的状态（文档关闭时调用）。</para>
    /// </summary>
    public void Cleanup()
    {
        RawBytes = null;
        Source = string.Empty;
        _lineMap = [];
        Nodes.Clear();
        SelectedNode = null;
        CompileError = string.Empty;
        FilePath = null;
        NodeCount = 0;
        RootCompatible = string.Empty;
        RootAddressCells = string.Empty;
        RootSizeCells = string.Empty;
        MemoryReservationCount = 0;
        IsDirty = false;
    }
}