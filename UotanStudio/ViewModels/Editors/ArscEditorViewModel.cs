using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Arsc;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>ARSC（resources.arsc）资源表编辑器视图模型：左栏包→类型→配置→条目四级资源树 +
/// 右栏选中条目的可视化详情（键名、类型、格式化值与原始数据），支持解析与浏览。</para>
/// ARSC (resources.arsc) resource-table editor view model: a left package→type→configuration→entry
/// tree plus a right details pane (key name, type, formatted value and raw data) for the selected
/// entry, supporting parsing and browsing.
/// </summary>
public partial class ArscEditorViewModel : Document
{
    /// <summary>
    /// <para>加载中提示文案的本地化标题。</para>
    /// The localized title of the busy/loading label.
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Dex_Disassembling");

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
    /// <para>解析后的资源表模型（供详情取值）。</para>
    /// The parsed resource-table model (for the details pane).
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
    /// <para>创建 ARSC 编辑器视图模型。</para>
    /// Creates the ARSC editor view model.
    /// </summary>
    public ArscEditorViewModel()
    {
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
        AddDetail("Value", value.FormatValue(_table!.GlobalPool, entry.PackageId));
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
/// <para>右栏详情面板的键值行。</para>
/// A key/value row of the right details pane.
/// </summary>
public sealed class ArscDetailRow
{
    /// <summary>
    /// <para>创建详情行。</para>
    /// Creates a detail row.
    /// </summary>
    public ArscDetailRow(string key, string value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>详情键名。<para>Detail key name.</para></summary>
    public string Key { get; }

    /// <summary>详情值。<para>Detail value.</para></summary>
    public string Value { get; }
}

/// <summary>
/// <para>条目详情负载：条目模型 + 解析后的键名与包 ID（详情面板取值用）。</para>
/// Entry-details payload: the entry model plus its resolved key name and package ID (for the details pane).
/// </summary>
public sealed class ResEntryEntry
{
    /// <summary>条目模型。<para>The entry model.</para></summary>
    public ResEntry Entry { get; init; } = new();

    /// <summary>解析后的键名。<para>The resolved key name.</para></summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>所属包 ID。<para>The owning package ID.</para></summary>
    public byte PackageId { get; init; }

    /// <summary>格式化值文本（树节点可展示）。<para>Formatted value text (for tree display).</para></summary>
    public string ValueText { get; init; } = string.Empty;
}
