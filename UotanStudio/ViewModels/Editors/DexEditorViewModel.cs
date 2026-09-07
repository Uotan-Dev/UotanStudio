using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>DEX 编辑器视图模型：右栏显示全量反汇编（Smali）文本，支持解析、统计与重建写出。
/// 不提供左栏结构树导航——大 DEX 打开时数万树节点与 TreeView 控件是主要内存开销，
/// 删除树后仅保留反汇编代码，内存占用显著下降。</para>
/// DEX editor view model: the right pane shows the full disassembly (Smali) text, with parse,
/// statistics and rebuild-write support. There is no left structure-tree navigation — tens of
/// thousands of tree nodes and TreeView controls were the main memory cost when opening a large
/// DEX; dropping the tree keeps only the disassembly text and cuts the memory footprint sharply.
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePathDisplay))]
    private string? _filePath;

    /// <summary>
    /// <para>本地化后的文件路径显示文本（顶部工具栏）。</para>
    /// Localized file path display text (top toolbar).
    /// </summary>
    public string FilePathDisplay =>
        string.IsNullOrEmpty(FilePath)
            ? string.Empty
            : string.Format(LanguageService.Instance.GetString("Dock_File_PathFormat"), FilePath);

    /// <summary>
    /// <para>反汇编文本（只读，加载时全量生成）。</para>
    /// The disassembly text (read-only; generated in full on load).
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
    /// <para>异步加载 DEX 文件：读取字节 → 解析 → 全量反汇编生成文本。</para>
    /// Asynchronously loads a DEX file: read bytes, parse, and generate the full disassembly text.
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

            // 将 CPU 密集的解析与反汇编放到线程池执行，避免阻塞 UI 线程。
            // Offload the CPU-bound parse and disassembly to the thread pool to keep the UI responsive.
            DexLoadResult result;
            try
            {
                result = await Task.Run(() => LoadModel(bytes), default).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                CompileError = $"Failed to parse DEX: {ex.Message}";
                Source = string.Empty;
                return;
            }

            _dex = result.Dex;
            Source = result.Source;
            CompileError = string.Empty;
            IsDirty = false;
            MethodCount = result.MethodCount;
            FieldCount = result.FieldCount;

            // 解析完成后释放原始字节，SaveAsync 从 _dex 模型重建。
            // Release raw bytes after parsing; SaveAsync rebuilds from the _dex model.
            RawBytes = null;
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
    /// <para>在后台线程执行 DEX 解析、全量反汇编与统计，返回供 UI 线程应用的结果。</para>
    /// Parses the DEX, disassembles it in full and computes statistics on a background thread,
    /// returning results to be applied on the UI thread.
    /// </summary>
    /// <param name="bytes">DEX 原始字节。<para>The raw DEX bytes.</para></param>
    /// <returns>包含解析模型、反汇编文本与统计的结果对象。</returns>
    private static DexLoadResult LoadModel(byte[] bytes)
    {
        var dex = DexFile.Load(bytes);

        var methodCount = 0;
        var fieldCount = 0;
        foreach (var cls in dex.Classes)
        {
            methodCount += cls.DirectMethods.Count + cls.VirtualMethods.Count;
            fieldCount += cls.StaticFields.Count + cls.InstanceFields.Count;
        }

        // 全量反汇编：解析时为惰性解码（不预生成指令对象），此处按需解码每个方法生成 Smali 文本。
        // Full disassembly: parsing is lazily decoded (no instruction objects are materialized up
        // front); each method is decoded on demand here to produce the Smali text.
        var source = Disassembler.DisassembleAll(dex);
        return new DexLoadResult(dex, source, methodCount, fieldCount);
    }

    /// <summary>
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）：清空反汇编文本、原始字节与解析模型。
    /// 此后 VM 不再强持有大对象，内存可被 GC 回收。</para>
    /// Releases the model and view data held by this document (called when the document is closed):
    /// clears the disassembly text, raw bytes and parsed model. The VM no longer holds large objects,
    /// letting the GC reclaim the memory.
    /// </summary>
    public void Cleanup()
    {
        Source = string.Empty;
        CompileError = string.Empty;
        RawBytes = null;
        _dex = null;
        MethodCount = 0;
        FieldCount = 0;
    }

    /// <summary>
    /// <para>后台加载 DEX 的一次性结果数据。</para>
    /// One-shot result data of a background DEX load.
    /// </summary>
    /// <param name="Dex">解析后的 DEX 模型。<para>The parsed DEX model.</para></param>
    /// <param name="Source">全量反汇编文本。<para>The full disassembly text.</para></param>
    /// <param name="MethodCount">方法总数。<para>Total method count.</para></param>
    /// <param name="FieldCount">字段总数。<para>Total field count.</para></param>
    private sealed record DexLoadResult(
        DexFile Dex,
        string Source,
        int MethodCount,
        int FieldCount);
}