using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Keys;
using UotanStudio.Services.Workspace;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// DEX editor view model: the right pane shows the full disassembly (Smali) text, with parse,
/// statistics and rebuild-write support. There is no left structure-tree navigation — tens of
/// thousands of tree nodes and TreeView controls were the main memory cost when opening a large
/// DEX; dropping the tree keeps only the disassembly text and cuts the memory footprint sharply.
/// JAR/APK containers: enumerate classes*.dex entries with a merged multi-DEX view (aggregated
/// Smali and statistics), per-entry rebuild write-back, resource export/replace, and v1/v2/v3
/// signing through JAR-purpose keys from the key library.
/// <para>DEX 编辑器视图模型：右栏显示全量反汇编（Smali）文本，支持解析、统计与重建写出。
/// 不提供左栏结构树导航——大 DEX 打开时数万树节点与 TreeView 控件是主要内存开销，
/// 删除树后仅保留反汇编代码，内存占用显著下降。</para>
/// <para>JAR/APK 容器：枚举 classes*.dex 条目并支持多 DEX 合并视图（跨条目聚合 Smali 与统计）、
/// 单条目重建写回、资源条目导出/替换，以及接入密钥库 JAR 用途密钥的 v1/v2/v3 签名。</para>
/// </summary>
public partial class DexEditorViewModel : Document, ISearchableDocument
{
    /// <summary>
    /// The localized title of the save button.
    /// <para>保存按钮的本地化标题。</para>
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Dex_Save");

    /// <summary>
    /// The localized title of the busy/loading label.
    /// <para>加载中提示文案的本地化标题。</para>
    /// </summary>
    public static string BusyLabel => LanguageService.Instance.GetString("Dock_Dex_Disassembling");

    /// <summary>
    /// Whether the background parse/disassembly is running (drives the busy indicator animation).
    /// <para>是否正在后台解析/反汇编（联动忙碌指示动画）。</para>
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
            : string.Format(LanguageService.Instance.GetString("Dock_File_PathFormat"), FilePath);

    /// <summary>
    /// The disassembly text (read-only; generated in full on load).
    /// <para>反汇编文本（只读，加载时全量生成）。</para>
    /// </summary>
    [ObservableProperty] private string _source = string.Empty;

    /// <summary>
    /// Active disassembly editor for workspace actions.
    /// <para>供工作区操作使用的反汇编编辑器。</para>
    /// </summary>
    internal AvaloniaEdit.TextEditor? Editor { get; set; }

    /// <inheritdoc/>
    string? ISearchableDocument.SearchText => Source;

    /// <inheritdoc/>
    string? ISearchableDocument.FilePath => FilePath;

    /// <inheritdoc/>
    AvaloniaEdit.TextEditor? ISearchableDocument.Editor => Editor;

    /// <inheritdoc/>
    bool ISearchableDocument.SupportsSearchReplacement => false;

    /// <inheritdoc/>
    void ISearchableDocument.ApplySearchText(string text) =>
        throw new NotSupportedException("DEX disassembly is read-only for text replacement.");

    private bool _suppressSourceDirty;

    partial void OnSourceChanged(string value)
    {
        if (!_suppressSourceDirty && _dex is not null && !IsBusy)
        {
            IsDirty = true;
        }
    }

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

    /// <summary>
    /// Whether the source is a JAR/APK container (drives entry/resource/signing panel visibility).
    /// <para>来源是否为 JAR/APK 容器（决定条目/资源/签名面板的可见性）。</para>
    /// </summary>
    [ObservableProperty] private bool _isContainer;

    /// <summary>
    /// Whether the merged multi-DEX view is active (aggregated browsing of all container DEX
    /// entries; saving is disabled in this mode).
    /// <para>是否处于多 DEX 合并视图（容器内全部 DEX 条目的聚合浏览，此时禁止保存）。</para>
    /// </summary>
    [ObservableProperty] private bool _isMergeView;

    /// <summary>
    /// All container entries (classified as DEX/signature/resource).
    /// <para>容器内全部条目（DEX/签名/资源分类）。</para>
    /// </summary>
    public ObservableCollection<JarEntryInfo> ContainerEntries { get; } = [];

    /// <summary>
    /// Resource entries in the container (exportable/replaceable; read-only signature entries
    /// are excluded).
    /// <para>容器内的资源条目（可导出/替换；签名条目只读不在此列出）。</para>
    /// </summary>
    public ObservableCollection<JarEntryInfo> ResourceEntries { get; } = [];

    /// <summary>
    /// Container DEX entry names (the toolbar entry combo source).
    /// <para>容器内的 DEX 条目名（工具栏条目下拉数据源）。</para>
    /// </summary>
    public ObservableCollection<string> DexEntryNames { get; } = [];

    /// <summary>
    /// The currently selected DEX entry name; empty in the merged view.
    /// <para>当前选中的 DEX 条目名；合并视图下为空。</para>
    /// </summary>
    [ObservableProperty] private string? _selectedEntry;

    /// <summary>
    /// The currently selected resource entry (the export/replace target).
    /// <para>当前选中的资源条目（导出/替换操作目标）。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedResource))]
    private JarEntryInfo? _selectedResource;

    /// <summary>
    /// Whether a resource entry is selected (drives the export/replace buttons).
    /// <para>是否已选中资源条目（驱动导出/替换按钮可用性）。</para>
    /// </summary>
    public bool HasSelectedResource => SelectedResource is not null;

    /// <summary>
    /// JAR-purpose signing keys from the key library.
    /// <para>密钥库中的 JAR 用途签名密钥。</para>
    /// </summary>
    public ObservableCollection<ManagedKey> JarKeys { get; } = [];

    /// <summary>
    /// The currently selected JAR signing key.
    /// <para>当前选中的 JAR 签名密钥。</para>
    /// </summary>
    [ObservableProperty] private ManagedKey? _selectedJarKey;

    /// <summary>
    /// The signing result text (the output path on success, the error otherwise).
    /// <para>签名结果文本（成功为输出路径，失败为错误信息）。</para>
    /// </summary>
    [ObservableProperty] private string _signStatus = string.Empty;

    /// <summary>
    /// The parsed DEX model (for statistics and rebuild; empty in the merged view).
    /// <para>解析后的 DEX 模型（用于统计与重建；合并视图下为空）。</para>
    /// </summary>
    private DexFile? _dex;

    /// <summary>
    /// The parsed DEX model (used for statistics and rebuild).
    /// <para>解析后的 DEX 模型（用于统计与重建）。</para>
    /// </summary>
    public DexFile? Dex => _dex;

    /// <summary>
    /// Total class count; 0 when not loaded. Aggregated across entries in the merged view.
    /// <para>类总数；未加载时为 0。合并视图下为全部 DEX 条目的累加。</para>
    /// </summary>
    public int ClassCount { get; private set; }

    /// <summary>
    /// Total declared method count (direct+virtual); 0 when not loaded.
    /// <para>方法总数（声明的直接+虚方法）；未加载时为 0。</para>
    /// </summary>
    public int MethodCount { get; private set; }

    /// <summary>
    /// Total field count (static+instance); 0 when not loaded.
    /// <para>字段总数（静态+实例）；未加载时为 0。</para>
    /// </summary>
    public int FieldCount { get; private set; }

    /// <summary>
    /// String table entry count; 0 when not loaded.
    /// <para>字符串表条目数；未加载时为 0。</para>
    /// </summary>
    public int StringCount { get; private set; }

    /// <summary>
    /// Type table entry count; 0 when not loaded.
    /// <para>类型表条目数；未加载时为 0。</para>
    /// </summary>
    public int TypeCount { get; private set; }

    /// <summary>
    /// The DEX version (e.g. 035); empty when not loaded or in the merged view.
    /// <para>DEX 版本号（如 035）；未加载或合并视图时为空。</para>
    /// </summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>
    /// Save command: rebuilds the DEX bytes from the model and writes them back to the file
    /// (container sources write back the selected entry).
    /// <para>保存命令：从模型重建 DEX 字节并写回原文件（容器来源写回选中条目）。</para>
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// The resource export command.
    /// <para>资源导出命令。</para>
    /// </summary>
    public IRelayCommand ExportResourceCommand { get; }

    /// <summary>
    /// The resource replace command.
    /// <para>资源替换命令。</para>
    /// </summary>
    public IRelayCommand ReplaceResourceCommand { get; }

    /// <summary>
    /// The JAR signing command.
    /// <para>JAR 签名命令。</para>
    /// </summary>
    public IAsyncRelayCommand SignJarCommand { get; }

    /// <summary>
    /// Creates the DEX editor view model and initializes commands.
    /// <para>创建 DEX 编辑器视图模型并初始化命令。</para>
    /// </summary>
    public DexEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ExportResourceCommand = new RelayCommand(ExportSelectedResource);
        ReplaceResourceCommand = new RelayCommand(ReplaceSelectedResource);
        SignJarCommand = new AsyncRelayCommand(SignJarAsync);
    }

    /// <summary>
    /// Asynchronously loads a DEX or JAR/APK container: containers enumerate entries and default
    /// to the merged view; plain DEX files parse directly.
    /// <para>异步加载 DEX 或 JAR/APK 容器：容器时枚举条目并默认进入合并视图；普通 DEX 直接解析。</para>
    /// </summary>
    /// <param name="path">DEX 或容器文件路径。<para>DEX or container file path.</para></param>
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
            IsContainer = JarDexContainer.IsContainerPath(path);
            if (IsContainer)
            {
                await LoadContainerAsync(path).ConfigureAwait(true);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            RawBytes = bytes;

            DexLoadResult result;
            try
            {
                result = await Task.Run(() => LoadModel(bytes), default).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                CompileError = global::UotanStudio.Services.UserError.Describe(ex, "UserError_ParseFailed", "DEX");
                Source = string.Empty;
                return;
            }

            ApplyLoadResult(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads a JAR/APK container: fills the entry/resource lists, refreshes JAR signing keys and
    /// enters the merged view.
    /// <para>加载 JAR/APK 容器：填充条目与资源列表、刷新 JAR 签名密钥，并进入合并视图。</para>
    /// </summary>
    private async Task LoadContainerAsync(string path)
    {
        var entries = await Task.Run(() => JarDexContainer.ListEntries(path), default).ConfigureAwait(true);
        ContainerEntries.Clear();
        ResourceEntries.Clear();
        DexEntryNames.Clear();
        foreach (var entry in entries)
        {
            ContainerEntries.Add(entry);
            if (entry.Kind == JarEntryKind.Dex) DexEntryNames.Add(entry.Name);
            if (entry.Kind == JarEntryKind.Resource) ResourceEntries.Add(entry);
        }

        RefreshJarKeys();

        await SetMergeViewAsync(true).ConfigureAwait(true);
    }

    /// <summary>
    /// Entry combo selection changed: reloads the entry when not in the merged view or loading.
    /// <para>条目下拉选择变化：非合并视图且非加载中时重新加载该条目。</para>
    /// </summary>
    partial void OnSelectedEntryChanged(string? value)
    {
        if (IsContainer && !IsMergeView && value is not null && value != _loadingEntry)
        {
            _ = LoadEntryAsync(value);
        }
    }

    /// <summary>
    /// The entry being loaded programmatically (prevents reentrant selection callbacks).
    /// <para>正在程序化加载的条目名（防止选择回调重入）。</para>
    /// </summary>
    private string? _loadingEntry;

    /// <summary>
    /// Toggles the merged view: on aggregates all DEX entries; off loads the first (or last
    /// selected) DEX entry.
    /// <para>切换合并视图：开启时聚合全部 DEX 条目，关闭时加载第一个（或上次选中的）DEX 条目。</para>
    /// </summary>
    public async Task SetMergeViewAsync(bool merge)
    {
        if (!IsContainer || IsBusy) return;
        IsBusy = true;
        try
        {
            IsMergeView = merge;
            CompileError = string.Empty;
            if (merge)
            {
                SelectedEntry = null;
                var merged = await Task.Run(() => LoadMergedModel(FilePath!), default).ConfigureAwait(true);
                ApplyLoadResult(merged);
            }
            else
            {
                var target = SelectedEntry;
                await LoadEntryAsync(target).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads the given container DEX entry (the first when empty), generating disassembly text
    /// and statistics.
    /// <para>加载容器内指定 DEX 条目（为空时取第一个），生成反汇编文本与统计。</para>
    /// </summary>
    /// <param name="entryName">DEX 条目名；为空取第一个。<para>The DEX entry name; the first when empty.</para></param>
    public async Task LoadEntryAsync(string? entryName)
    {
        if (!IsContainer || string.IsNullOrWhiteSpace(FilePath)) return;
        IsBusy = true;
        try
        {
            var dexEntries = ContainerEntries.Where(e => e.Kind == JarEntryKind.Dex).Select(e => e.Name).ToArray();
            var target = dexEntries.FirstOrDefault(n => n == entryName) ?? dexEntries.FirstOrDefault();
            if (target is null)
            {
                CompileError = global::UotanStudio.Services.LanguageService.Instance.GetString("UserError_DexMissing");
                return;
            }

            SelectedEntry = target;
            _loadingEntry = target;
            try
            {
                var bytes = await Task.Run(() => JarDexContainer.LoadEntry(FilePath, target), default).ConfigureAwait(true);
                RawBytes = bytes;

                DexLoadResult result;
                try
                {
                    result = await Task.Run(() => LoadModel(bytes), default).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    CompileError = global::UotanStudio.Services.UserError.Describe(ex, "UserError_ParseFailed", target);
                    Source = string.Empty;
                    return;
                }

                ApplyLoadResult(result);
            }
            finally
            {
                _loadingEntry = null;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Builds merged-view data on a background thread: parses and disassembles per entry,
    /// aggregates statistics, and sections the text by entry.
    /// <para>在后台线程构建合并视图数据：逐条目解析、反汇编并聚合统计，文本按条目分节。</para>
    /// </summary>
    private static DexLoadResult LoadMergedModel(string containerPath)
    {
        var classCount = 0;
        var methodCount = 0;
        var fieldCount = 0;
        var stringCount = 0;
        var typeCount = 0;
        var source = DexContainerSearchText.BuildMergedSource(containerPath, dex =>
        {
            classCount += dex.Classes.Count;
            stringCount += dex.Strings.Count;
            typeCount += dex.Types.Count;
            foreach (var cls in dex.Classes)
            {
                methodCount += cls.DirectMethods.Count + cls.VirtualMethods.Count;
                fieldCount += cls.StaticFields.Count + cls.InstanceFields.Count;
            }
        });

        return new DexLoadResult(null, source, classCount, methodCount, fieldCount, stringCount, typeCount, string.Empty);
    }

    /// <summary>
    /// Saves: rebuilds the DEX bytes from the current model and writes them back (container
    /// sources write back the entry; saving is disabled in the merged view).
    /// <para>保存：从当前模型重建 DEX 字节并写回（容器来源经条目写回；合并视图禁止保存）。</para>
    /// </summary>
    public async Task SaveAsync()
    {
        if (IsContainer && IsMergeView)
        {
            CompileError = global::UotanStudio.Services.LanguageService.Instance.GetString("UserError_DexMergedReadOnly");
            return;
        }
        if (_dex is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        try
        {
            DexFile model;
            if (IsDirty)
            {
                model = await Task.Run(() => SmaliAssembler.Assemble(Source)).ConfigureAwait(true);
            }
            else
            {
                model = _dex;
            }
            var outBytes = model.ToArray();
            if (IsContainer)
            {
                if (string.IsNullOrEmpty(SelectedEntry))
                {
                    CompileError = global::UotanStudio.Services.LanguageService.Instance.GetString("UserError_DexNotSelected");
                    return;
                }
                await Task.Run(() => JarDexContainer.WriteBack(FilePath, SelectedEntry, outBytes), default).ConfigureAwait(true);
                LogService.Instance.Write(LogLevel.Info, "DexEditor", $"Rebuilt {SelectedEntry} written back to {FilePath}");
            }
            else
            {
                await File.WriteAllBytesAsync(FilePath, outBytes).ConfigureAwait(true);
                RawBytes = outBytes;
                LogService.Instance.Write(LogLevel.Info, "DexEditor", $"Rebuilt DEX written to {FilePath}");
            }
            IsDirty = false;
            RawBytes = outBytes;
            _dex = model;
            ApplyStatistics(model);
            CompileError = string.Empty;
        }
        catch (Exception ex)
        {
            CompileError = global::UotanStudio.Services.UserError.Describe(ex, "UserError_RebuildFailed", "DEX");
        }
    }

    /// <summary>
    /// Refreshes the JAR-purpose signing keys from the key library (the signing panel source).
    /// <para>刷新密钥库中的 JAR 用途签名密钥（签名面板下拉数据源）。</para>
    /// </summary>
    public void RefreshJarKeys()
    {
        JarKeys.Clear();
        try
        {
            foreach (var key in new KeyLibraryService().List().Where(k => k.Kind == ManagedKeyKind.Jar))
            {
                JarKeys.Add(key);
            }
            SelectedJarKey ??= JarKeys.FirstOrDefault();
        }
        catch (Exception ex)
        {
            SignStatus = global::UotanStudio.Services.UserError.Describe(ex);
        }
    }

    /// <summary>
    /// Signs the source container with the selected JAR key, outputting to
    /// <c>&lt;name&gt;-signed.jar</c> in the same directory.
    /// <para>以选中的 JAR 密钥对来源容器签名，输出到同目录 <c>&lt;name&gt;-signed.jar</c>。</para>
    /// </summary>
    public async Task SignJarAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || SelectedJarKey is not { } key)
        {
            SignStatus = "No JAR signing key selected.";
            return;
        }

        var output = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(FilePath))!,
            Path.GetFileNameWithoutExtension(FilePath) + "-signed.jar");
        try
        {
            var library = new KeyLibraryService();
            var keyPath = library.PrivatePath(key);
            var certPath = library.EnsureCertificate(key);
            var result = await Task.Run(
                () => DexService.Instance.SignJarAsync(FilePath, output, keyPath, certPath),
                default).ConfigureAwait(true);
            SignStatus = result.Success ? output : global::UotanStudio.Services.UserError.WithDetails(result.Summary, "UserError_SigningFailed");
            if (result.Success)
            {
                LogService.Instance.Write(LogLevel.Info, "DexEditor", $"Signed JAR written to {output}");
            }
        }
        catch (Exception ex)
        {
            SignStatus = global::UotanStudio.Services.UserError.Describe(ex);
        }
    }

    /// <summary>
    /// Exports the selected resource entry to the target file (false when cancelled or nothing
    /// is selected).
    /// <para>把选中的资源条目导出到目标文件（返回 false 表示取消或未选择）。</para>
    /// </summary>
    /// <param name="outputPath">输出文件路径。<para>The output file path.</para></param>
    public async Task<bool> ExportResourceAsync(string outputPath)
    {
        if (!IsContainer || string.IsNullOrWhiteSpace(FilePath) || SelectedResource is not { } entry)
        {
            return false;
        }

        await Task.Run(() => JarDexContainer.ExtractResource(FilePath, entry.Name, outputPath), default).ConfigureAwait(true);
        LogService.Instance.Write(LogLevel.Info, "DexEditor", $"Resource {entry.Name} exported to {outputPath}");
        return true;
    }

    /// <summary>
    /// Replaces the selected resource entry with a local file and marks the document dirty.
    /// <para>用本地文件替换选中的资源条目并标记文档为已修改。</para>
    /// </summary>
    /// <param name="sourceFilePath">替换内容来源文件。<para>The local file providing the content.</para></param>
    public async Task ReplaceResourceAsync(string sourceFilePath)
    {
        if (!IsContainer || string.IsNullOrWhiteSpace(FilePath) || SelectedResource is not { } entry)
        {
            return;
        }

        await Task.Run(() => JarDexContainer.ReplaceResource(FilePath, entry.Name, sourceFilePath), default).ConfigureAwait(true);
        IsDirty = true;
        LogService.Instance.Write(LogLevel.Info, "DexEditor", $"Resource {entry.Name} replaced from {sourceFilePath}");
    }

    /// <summary>
    /// The export command entry: the view drives the picker and calls <see cref="ExportResourceAsync"/>.
    /// <para>资源导出命令入口：由视图经文件选择器回调 <see cref="ExportResourceAsync"/>。</para>
    /// </summary>
    private void ExportSelectedResource() => ResourceOperationRequested?.Invoke(this, false);

    /// <summary>
    /// The replace command entry: the view drives the picker and calls <see cref="ReplaceResourceAsync"/>.
    /// <para>资源替换命令入口：由视图经文件选择器回调 <see cref="ReplaceResourceAsync"/>。</para>
    /// </summary>
    private void ReplaceSelectedResource() => ResourceOperationRequested?.Invoke(this, true);

    /// <summary>
    /// The resource-operation event: true replaces and false exports; the view shows the picker
    /// and then calls the matching method.
    /// <para>资源操作请求事件：参数 true 为替换、false 为导出，由视图弹出文件选择器后调用对应方法。</para>
    /// </summary>
    public event EventHandler<bool>? ResourceOperationRequested;

    /// <summary>
    /// Applies a background load result (UI thread): updates text, statistics and dirty state.
    /// <para>应用后台加载结果（UI 线程）：更新文本、统计与脏状态。</para>
    /// </summary>
    private void ApplyLoadResult(DexLoadResult result)
    {
        _dex = result.Dex;
        _suppressSourceDirty = true;
        try { Source = result.Source; }
        finally { _suppressSourceDirty = false; }
        CompileError = string.Empty;
        IsDirty = false;
        ClassCount = result.ClassCount;
        MethodCount = result.MethodCount;
        FieldCount = result.FieldCount;
        StringCount = result.StringCount;
        TypeCount = result.TypeCount;
        Version = result.Version;
    }

    private void ApplyStatistics(DexFile dex)
    {
        ClassCount = dex.Classes.Count;
        StringCount = dex.Strings.Count;
        TypeCount = dex.Types.Count;
        MethodCount = dex.Classes.Sum(c => c.DirectMethods.Count + c.VirtualMethods.Count);
        FieldCount = dex.Classes.Sum(c => c.StaticFields.Count + c.InstanceFields.Count);
        Version = dex.Header.Version;
    }

    /// <summary>
    /// Parses the DEX, disassembles it in full and computes statistics on a background thread,
    /// returning results to be applied on the UI thread.
    /// <para>在后台线程执行 DEX 解析、全量反汇编与统计，返回供 UI 线程应用的结果。</para>
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

        var source = Disassembler.DisassembleAll(dex);
        return new DexLoadResult(dex, source, dex.Classes.Count, methodCount, fieldCount,
            dex.Strings.Count, dex.Types.Count, dex.Header.Version);
    }

    /// <summary>
    /// Releases the model and view data held by this document when it is closed.
    /// <para>文档关闭时释放其持有的模型与视图数据。</para>
    /// </summary>
    public void Cleanup()
    {
        Source = string.Empty;
        CompileError = string.Empty;
        RawBytes = null;
        _dex = null;
        ClassCount = 0;
        MethodCount = 0;
        FieldCount = 0;
        StringCount = 0;
        TypeCount = 0;
        Version = string.Empty;
        IsContainer = false;
        IsMergeView = false;
        SelectedEntry = null;
        SelectedResource = null;
        Editor = null;
        ContainerEntries.Clear();
        ResourceEntries.Clear();
        DexEntryNames.Clear();
        SignStatus = string.Empty;
    }

    /// <summary>
    /// One-shot result data of a background DEX load; a null <c>Dex</c> marks the merged view
    /// (no single model).
    /// <para>后台加载 DEX 的一次性结果数据；<c>Dex</c> 为空表示合并视图（无单一模型）。</para>
    /// </summary>
    /// <param name="Dex">解析后的 DEX 模型；合并视图为 null。<para>The parsed DEX model; null for the merged view.</para></param>
    /// <param name="Source">反汇编文本。<para>The disassembly text.</para></param>
    /// <param name="ClassCount">类总数。<para>Total class count.</para></param>
    /// <param name="MethodCount">方法总数。<para>Total method count.</para></param>
    /// <param name="FieldCount">字段总数。<para>Total field count.</para></param>
    /// <param name="StringCount">字符串表条目数。<para>String table entry count.</para></param>
    /// <param name="TypeCount">类型表条目数。<para>Type table entry count.</para></param>
    /// <param name="Version">DEX 版本号；合并视图为空。<para>The DEX version; empty for the merged view.</para></param>
    private sealed record DexLoadResult(
        DexFile? Dex,
        string Source,
        int ClassCount,
        int MethodCount,
        int FieldCount,
        int StringCount,
        int TypeCount,
        string Version);
}