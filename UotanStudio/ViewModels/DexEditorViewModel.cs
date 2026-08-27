using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Dex;
using UotanStudio.Firmware.Dex.Model;
using UotanStudio.Services;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>DEX 编辑器视图模型（仿设备树面板）：左栏结构树（类/方法/字段）+ 右栏反汇编文本，
/// 支持解析、统计与重建写出。</para>
/// DEX editor view model (modeled on the device-tree panel): a left structure tree (class/method/field)
/// plus a right disassembly text pane, supporting parsing, statistics and rebuild-write.
/// </summary>
public partial class DexEditorViewModel : Document
{
    /// <summary>
    /// <para>保存按钮的本地化标题。</para>
    /// The localized title of the save button.
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Dex_Save");

    /// <summary>
    /// <para>加载中提示文案的本地化标题。</para>
    /// The localized title of the busy/loading label.
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Dex_Disassembling");

    /// <summary>
    /// <para>是否正在后台解析/反汇编（联动忙碌指示动画）。</para>
    /// Whether the background parse/disassembly is running (drives the busy indicator animation).
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>文档对应的文件路径。</para>
    /// The file path of the document.
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// <para>右栏反汇编文本（只读）。</para>
    /// The right-pane disassembly text (read-only).
    /// </summary>
    [ObservableProperty] private string _source = string.Empty;

    /// <summary>
    /// <para>左栏结构树根节点（类）。</para>
    /// The left-pane structure tree root nodes (classes).
    /// </summary>
    public ObservableCollection<DexTreeNodeViewModel> Classes { get; } = [];

    /// <summary>
    /// <para>当前选中的树节点（左栏选择联动）。</para>
    /// The currently selected tree node (left-pane selection linkage).
    /// </summary>
    [ObservableProperty] private DexTreeNodeViewModel? _selectedNode;

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
    /// <para>解析后的 DEX 模型（用于统计与重建）。</para>
    /// The parsed DEX model (used for statistics and rebuild).
    /// </summary>
    private DexFile? _dex;

    /// <summary>
    /// <para>类总数；未加载时为 0。</para>
    /// Total class count; 0 when not loaded.
    /// </summary>
    public int ClassCount => _dex?.Classes.Count ?? 0;

    /// <summary>
    /// <para>方法总数（声明的直接+虚方法）；未加载时为 0。</para>
    /// Total declared method count (direct+virtual); 0 when not loaded.
    /// </summary>
    public int MethodCount { get; private set; }

    /// <summary>
    /// <para>字段总数（静态+实例）；未加载时为 0。</para>
    /// Total field count (static+instance); 0 when not loaded.
    /// </summary>
    public int FieldCount { get; private set; }

    /// <summary>
    /// <para>字符串表条目数；未加载时为 0。</para>
    /// String table entry count; 0 when not loaded.
    /// </summary>
    public int StringCount => _dex?.Strings.Count ?? 0;

    /// <summary>
    /// <para>类型表条目数；未加载时为 0。</para>
    /// Type table entry count; 0 when not loaded.
    /// </summary>
    public int TypeCount => _dex?.Types.Count ?? 0;

    /// <summary>
    /// <para>DEX 版本号（如 035）；未加载时为空。</para>
    /// The DEX version (e.g. 035); empty when not loaded.
    /// </summary>
    public string Version => _dex?.Header.Version ?? string.Empty;

    /// <summary>
    /// <para>保存命令：从模型重建 DEX 字节并写回原文件。</para>
    /// Save command: rebuilds the DEX bytes from the model and writes them back to the file.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// <para>创建 DEX 编辑器视图模型并初始化保存命令。</para>
    /// Creates the DEX editor view model and initializes the save command.
    /// </summary>
    public DexEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    /// <summary>
    /// <para>异步加载 DEX 文件：读取字节 → 解析 → 构建结构树与反汇编文本。</para>
    /// Asynchronously loads a DEX file: read bytes, parse, and build the structure tree and disassembly text.
    /// </summary>
    /// <param name="path">DEX 文件路径。<para>DEX file path.</para></param>
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

            // 将 CPU 密集的解析、反汇编与结构树构建放到线程池执行，避免阻塞 UI 线程。
            // Offload the CPU-bound parse, disassembly and tree build to the thread pool to keep the UI responsive.
            DexLoadResult result;
            try
            {
                result = await Task.Run(() => LoadModel(bytes), default).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                CompileError = $"Failed to parse DEX: {ex.Message}";
                Classes.Clear();
                Source = string.Empty;
                return;
            }

            _dex = result.Dex;
            Classes.Clear();
            foreach (var node in result.ClassNodes)
            {
                Classes.Add(node);
            }

            Source = result.Source;
            CompileError = string.Empty;
            IsDirty = false;
            MethodCount = result.MethodCount;
            FieldCount = result.FieldCount;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>保存：从当前模型重建 DEX 字节并写回原文件（重算校验和与签名）。</para>
    /// Saves: rebuilds the DEX bytes from the current model and writes them back to the file
    /// (recomputing the checksum and signature).
    /// </summary>
    public async Task SaveAsync()
    {
        if (_dex is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        try
        {
            var outBytes = _dex.ToArray();
            await File.WriteAllBytesAsync(FilePath, outBytes).ConfigureAwait(true);
            RawBytes = outBytes;
            IsDirty = false;
            CompileError = string.Empty;
            LogService.Instance.Write(LogLevel.Info, "DexEditor", $"Rebuilt DEX written to {FilePath}");
        }
        catch (Exception ex)
        {
            CompileError = $"Failed to rebuild DEX: {ex.Message}";
        }
    }

    /// <summary>
    /// <para>在后台线程执行 DEX 解析、反汇编、结构树构建与统计，返回供 UI 线程应用的结果。</para>
    /// Parses the DEX, disassembles, builds the structure tree and computes statistics on a
    /// background thread, returning results to be applied on the UI thread.
    /// </summary>
    /// <param name="bytes">DEX 原始字节。<para>The raw DEX bytes.</para></param>
    /// <returns>包含解析模型、反汇编文本、结构树与统计的结果对象。</returns>
    private static DexLoadResult LoadModel(byte[] bytes)
    {
        var dex = DexFile.Load(bytes);

        var nodes = new List<DexTreeNodeViewModel>(dex.Classes.Count);
        var methodCount = 0;
        var fieldCount = 0;
        foreach (var cls in dex.Classes)
        {
            nodes.Add(BuildClassNode(dex, cls));
            methodCount += cls.DirectMethods.Count + cls.VirtualMethods.Count;
            fieldCount += cls.StaticFields.Count + cls.InstanceFields.Count;
        }

        return new DexLoadResult(dex, nodes, Disassembler.DisassembleAll(dex), methodCount, fieldCount);
    }

    private static DexTreeNodeViewModel BuildClassNode(DexFile dex, ClassDefinition cls)
    {
        var className = dex.FormatType(cls.TypeIndex);
        var node = new DexTreeNodeViewModel(className, "class", AccessFlags.Format(cls.AccessFlags));

        foreach (var f in cls.StaticFields.Concat(cls.InstanceFields))
        {
            var field = dex.GetField(f.FieldIndex);
            if (field is null) continue;
            var fieldName = dex.GetString(field.NameIndex);
            var fieldType = dex.FormatType(field.TypeIndex);
            node.Children.Add(new DexTreeNodeViewModel(fieldName, "field", $": {fieldType}"));
        }

        foreach (var m in cls.DirectMethods.Concat(cls.VirtualMethods))
        {
            var method = dex.GetMethod(m.MethodIndex);
            if (method is null) continue;
            var methodName = dex.GetString(method.NameIndex);
            var proto = dex.GetProto(method.ProtoIndex);
            var signature = BuildSignature(dex, proto);
            node.Children.Add(new DexTreeNodeViewModel(methodName, "method", signature));
        }

        return node;
    }

    private static string BuildSignature(DexFile dex, ProtoDefinition? proto)
    {
        if (proto is null)
        {
            return "()?";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append('(');
        for (var i = 0; i < proto.ParameterTypeIndices.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(dex.FormatType(proto.ParameterTypeIndices[i]));
        }
        sb.Append(')');
        sb.Append(dex.FormatType(proto.ReturnTypeIndex));
        return sb.ToString();
    }

    /// <summary>
    /// <para>后台加载 DEX 的一次性结果数据。</para>
    /// One-shot result data of a background DEX load.
    /// </summary>
    /// <param name="Dex">解析后的 DEX 模型。<para>The parsed DEX model.</para></param>
    /// <param name="ClassNodes">左栏结构树根节点（类）。<para>Left-pane structure tree root nodes (classes).</para></param>
    /// <param name="Source">反汇编文本。<para>The disassembly text.</para></param>
    /// <param name="MethodCount">方法总数。<para>Total method count.</para></param>
    /// <param name="FieldCount">字段总数。<para>Total field count.</para></param>
    private sealed record DexLoadResult(
        DexFile Dex,
        List<DexTreeNodeViewModel> ClassNodes,
        string Source,
        int MethodCount,
        int FieldCount);
}