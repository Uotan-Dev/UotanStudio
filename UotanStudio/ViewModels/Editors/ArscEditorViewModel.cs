using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Arsc;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>ARSC（resources.arsc）资源表编辑器视图模型：左栏包→类型→配置→条目四级资源树 +
/// 右栏选中条目的可视化详情（键名、类型、格式化值与原始数据），支持解析、值级编辑与重建保存。</para>
/// ARSC (resources.arsc) resource-table editor view model: a left package→type→configuration→entry
/// tree plus a right details pane (key name, type, formatted value and raw data) for the selected
/// entry, supporting parse, value-level editing and rebuild-save.
/// </summary>
public partial class ArscEditorViewModel : Document
{
    /// <summary>
    /// <para>保存按钮的本地化标题。</para>
    /// The localized title of the save button.
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Arsc_Save");

    /// <summary>
    /// <para>加载中提示文案的本地化标题。</para>
    /// The localized title of the busy/loading label.
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Arsc_Busy");

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
    /// <para>解析错误文本；为空表示无错误。</para>
    /// The parse error text; empty means no error.
    /// </summary>
    [ObservableProperty] private string _compileError = string.Empty;

    /// <summary>
    /// <para>文档已修改状态（值级编辑后置位，保存后清除）。</para>
    /// Document dirty state (set after a value edit, cleared on save).
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// <para>当前选中的树节点（左栏选择联动，驱动右栏详情面板）。</para>
    /// The currently selected tree node (left-pane selection linkage, drives the right details pane).
    /// </summary>
    [ObservableProperty] private ArscTreeNodeViewModel? _selectedNode;

    /// <summary>
    /// <para>左栏资源树根节点（每个包一个根）。</para>
    /// Left-pane tree root nodes (one per package).
    /// </summary>
    public ObservableCollection<ArscTreeNodeViewModel> Nodes { get; } = [];

    /// <summary>
    /// <para>右栏详情面板的键值行。</para>
    /// Key/value rows of the right details pane.
    /// </summary>
    public ObservableCollection<ArscDetailRow> Details { get; } = [];

    /// <summary>
    /// <para>解析后的资源表模型（供详情取值与重建保存）。</para>
    /// The parsed resource-table model (for the details pane and rebuild-save).
    /// </summary>
    private ResourceTable? _table;

    /// <summary>
    /// <para>包总数；未加载时为 0。</para>
    /// Total package count; 0 when not loaded.
    /// </summary>
    public int PackageCount { get; private set; }

    /// <summary>
    /// <para>类型表总数；未加载时为 0。</para>
    /// Total type-table count; 0 when not loaded.
    /// </summary>
    public int TypeCount { get; private set; }

    /// <summary>
    /// <para>条目总数；未加载时为 0。</para>
    /// Total entry count; 0 when not loaded.
    /// </summary>
    public int EntryCount { get; private set; }

    /// <summary>
    /// <para>保存命令：经 <see cref="ArscWriter"/> 重建并写回原文件。</para>
    /// Save command: rebuilds via <see cref="ArscWriter"/> and writes back to the file.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// <para>创建 ARSC 编辑器视图模型。</para>
    /// Creates the ARSC editor view model.
    /// </summary>
    public ArscEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    /// <summary>
    /// <para>异步加载 ARSC 文件：读取字节 → 解析 → 构建资源树。</para>
    /// Asynchronously loads an ARSC file: read bytes, parse, and build the resource tree.
    /// </summary>
    /// <param name="path">ARSC 文件路径。<para>ARSC file path.</para></param>
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

            ResourceTable table;
            try
            {
                table = await Task.Run(() => ArscParser.Parse(bytes), default).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                CompileError = $"Failed to parse ARSC: {ex.Message}";
                Nodes.Clear();
                Details.Clear();
                return;
            }

            _table = table;
            PackageCount = table.Packages.Count;
            TypeCount = table.Packages.Sum(p => p.Types.Count);
            EntryCount = table.Packages.Sum(p => p.Types.Sum(t => t.Entries.Count(e => e is not null)));

            Nodes.Clear();
            foreach (var pkg in table.Packages)
            {
                Nodes.Add(BuildPackageNode(pkg, table));
            }

            CompileError = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>选中节点变化：按节点层级加载详情面板内容。</para>
    /// Selected node changed: loads the details pane by node level.
    /// </summary>
    partial void OnSelectedNodeChanged(ArscTreeNodeViewModel? value)
    {
        Details.Clear();
        if (value is null || _table is null)
        {
            return;
        }

        switch (value.Payload)
        {
            case ResPackage pkg:
                AddDetail("Package", $"{pkg.Name} (0x{pkg.Id:x2})");
                AddDetail("Type tables", pkg.Types.Count.ToString());
                break;

            case ResType type:
                AddDetail("Type ID", "0x" + type.Id.ToString("x2"));
                AddDetail("Configuration", type.ConfigDescription);
                AddDetail("Config hex", type.ConfigHex);
                AddDetail("Entries", type.Entries.Count.ToString());
                break;

            case ResEntryEntry entry:
                ShowEntryDetails(entry);
                break;
        }
    }

    private void ShowEntryDetails(ResEntryEntry entry)
    {
        AddDetail("Key", entry.Key);
        var value = entry.Entry.Value;
        if (value is null)
        {
            AddDetail("Value", "<no entry>");
            return;
        }

        AddDetail("Type", value.Type.ToString());
        // 值级编辑：非复杂条目的可编辑类型（字符串/整数/十六进制/布尔）提供可写行，
        // 修改时解析并按类型写回模型（String 经全局池 Intern 更新 ValueData）。
        // Value-level editing: editable types (string/int/hex/bool) on simple entries get a writable
        // row; edits are parsed back per type (String interning into the global pool for ValueData).
        if (!value.IsComplex && IsEditableType(value.Type))
        {
            Details.Add(new ArscDetailRow("Value", value.FormatValue(_table!.GlobalPool, entry.PackageId), text =>
            {
                if (ApplyValueEdit(value, entry, text))
                {
                    IsDirty = true;
                    Details.FirstOrDefault(d => d.Key == "Value")!.Value = value.FormatValue(_table.GlobalPool, entry.PackageId);
                }
            }));
        }
        else
        {
            AddDetail("Value", value.FormatValue(_table!.GlobalPool, entry.PackageId));
        }

        if (value.IsComplex)
        {
            AddDetail("Parent", "0x" + value.Parent.ToString("x8"));
            foreach (var map in value.Maps)
            {
                AddDetail($"map[0x{map.Name:x8}]", map.Value.FormatValue(_table.GlobalPool, entry.PackageId));
            }
        }
        else
        {
            AddDetail("Raw data", "0x" + value.Data.ToString("x8"));
        }
    }

    /// <summary>
    /// <para>判断值类型是否支持可视化编辑。</para>
    /// Whether the value type supports visual editing.
    /// </summary>
    private static bool IsEditableType(ResValueType type) => type switch
    {
        ResValueType.String or ResValueType.IntDec or ResValueType.IntHex
            or ResValueType.IntBoolean or ResValueType.IntColorArgb8 or ResValueType.IntColorRgb8 => true,
        _ => false
    };

    /// <summary>
    /// <para>按类型解析编辑文本并写回条目值；返回是否成功。</para>
    /// Parses the edited text by value type and writes it back to the entry; returns success.
    /// </summary>
    private static bool ApplyValueEdit(ResValue value, ResEntryEntry entry, string text)
    {
        switch (value.Type)
        {
            case ResValueType.String:
                value.Data = (uint)entry.GlobalPool.Intern(text);
                return true;
            case ResValueType.IntDec when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec):
                value.Data = unchecked((uint)dec);
                return true;
            case ResValueType.IntHex when text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                         && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex):
                value.Data = hex;
                return true;
            case ResValueType.IntBoolean when bool.TryParse(text, out var b):
                value.Data = b ? 1u : 0u;
                return true;
            case ResValueType.IntColorArgb8 or ResValueType.IntColorRgb8
                when text.StartsWith('#')
                     && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color):
                value.Data = color;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// <para>保存：经 <see cref="ArscWriter"/> 重建模型并写回原文件。</para>
    /// Saves: rebuilds the model via <see cref="ArscWriter"/> and writes back to the file.
    /// </summary>
    public async Task SaveAsync()
    {
        if (_table is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        try
        {
            var outBytes = await Task.Run(() => ArscWriter.Write(_table), default).ConfigureAwait(true);
            await File.WriteAllBytesAsync(FilePath, outBytes).ConfigureAwait(true);
            IsDirty = false;
            CompileError = string.Empty;
            LogService.Instance.Write(LogLevel.Info, "ArscEditor", $"Rebuilt ARSC written to {FilePath}");
        }
        catch (Exception ex)
        {
            CompileError = $"Failed to rebuild ARSC: {ex.Message}";
        }
    }

    private static ArscTreeNodeViewModel BuildPackageNode(ResPackage pkg, ResourceTable table)
    {
        var packageNode = new ArscTreeNodeViewModel($"{pkg.Name} (0x{pkg.Id:x2})", pkg);

        foreach (var group in pkg.Types.GroupBy(t => t.Id).OrderBy(g => g.Key))
        {
            var typeName = TypeDisplayName(group.Key);
            var typeNode = new ArscTreeNodeViewModel($"0x{group.Key:x2} {typeName} ({group.Count()})", group.First());
            packageNode.Children.Add(typeNode);

            foreach (var type in group)
            {
                var configNode = new ArscTreeNodeViewModel(type.ConfigDescription, type);
                typeNode.Children.Add(configNode);

                foreach (var entry in type.Entries)
                {
                    if (entry is null)
                    {
                        continue;
                    }

                    var key = pkg.KeyStrings.GetString(entry.KeyIndex);
                    var display = string.IsNullOrEmpty(key)
                        ? $"<entry {type.Entries.IndexOf(entry)}>"
                        : key;
                    var entryNode = new ArscTreeNodeViewModel(display, new ResEntryEntry
                    {
                        Entry = entry,
                        Key = key,
                        PackageId = pkg.Id,
                        GlobalPool = table.GlobalPool,
                        ValueText = entry.Value?.FormatValue(table.GlobalPool, pkg.Id) ?? string.Empty
                    });
                    configNode.Children.Add(entryNode);
                }
            }
        }

        return packageNode;
    }

    private static string TypeDisplayName(byte typeId) => typeId switch
    {
        1 => "attr",
        2 => "string",
        3 => "style",
        4 => "color",
        5 => "dimen",
        6 => "drawable",
        7 => "layout",
        8 => "anim",
        9 => "mipmap",
        10 => "menu",
        12 => "xml",
        _ => "type"
    };

    private void AddDetail(string key, string value)
    {
        Details.Add(new ArscDetailRow(key, value));
    }

    /// <summary>
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）。</para>
    /// Releases the model and view data held by this document (called when the document is closed).
    /// </summary>
    public void Cleanup()
    {
        SelectedNode = null;
        Nodes.Clear();
        Details.Clear();
        CompileError = string.Empty;
        _table = null;
        PackageCount = 0;
        TypeCount = 0;
        EntryCount = 0;
    }
}

/// <summary>
/// <para>右栏详情面板的键值行；值可编辑时提供写回回调。</para>
/// A key/value row of the right details pane; editable rows carry a write-back callback.
/// </summary>
public partial class ArscDetailRow : ObservableObject
{
    private readonly Action<string>? _applyEdit;

    /// <summary>
    /// <para>创建只读详情行。</para>
    /// Creates a read-only detail row.
    /// </summary>
    public ArscDetailRow(string key, string value)
        : this(key, value, null)
    {
    }

    /// <summary>
    /// <para>创建详情行；提供 <paramref name="applyEdit"/> 时值可编辑并写回模型。</para>
    /// Creates a detail row; providing <paramref name="applyEdit"/> makes the value editable and
    /// writes it back to the model.
    /// </summary>
    public ArscDetailRow(string key, string value, Action<string>? applyEdit)
    {
        Key = key;
        _value = value;
        _applyEdit = applyEdit;
    }

    /// <summary>详情键名。<para>Detail key name.</para></summary>
    public string Key { get; }

    /// <summary>
    /// <para>详情值；编辑时经写回回调更新模型。</para>
    /// Detail value; edits flow through the write-back callback.
    /// </summary>
    [ObservableProperty] private string _value;

    /// <summary>是否可编辑（无写回回调时为只读）。<para>Whether the row is editable (read-only without a callback).</para></summary>
    public bool IsEditable => _applyEdit is not null;

    partial void OnValueChanged(string value)
    {
        if (_applyEdit is not null)
        {
            _applyEdit(value);
        }
    }
}

/// <summary>
/// <para>条目详情负载：条目模型 + 解析后的键名、包 ID 与全局字符串池（详情/编辑取值用）。</para>
/// Entry-details payload: the entry model plus its resolved key name, package ID and the global
/// string pool (for the details pane and value editing).
/// </summary>
public sealed class ResEntryEntry
{
    /// <summary>条目模型。<para>The entry model.</para></summary>
    public ResEntry Entry { get; init; } = new();

    /// <summary>解析后的键名。<para>The resolved key name.</para></summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>所属包 ID。<para>The owning package ID.</para></summary>
    public byte PackageId { get; init; }

    /// <summary>全局字符串池（String 型值编辑 Intern 用）。<para>The global string pool (for String-typed value editing).</para></summary>
    public UotanStudio.Firmware.AXml.AXmlStringPool GlobalPool { get; init; } = new();

    /// <summary>格式化值文本（树节点可展示）。<para>Formatted value text (for tree display).</para></summary>
    public string ValueText { get; init; } = string.Empty;
}
