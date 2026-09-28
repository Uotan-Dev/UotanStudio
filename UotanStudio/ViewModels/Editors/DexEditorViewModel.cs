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
/// <para>DEX 编辑器视图模型：右栏显示全量反汇编（Smali）文本，支持解析、统计与重建写出。
/// 不提供左栏结构树导航——大 DEX 打开时数万树节点与 TreeView 控件是主要内存开销，
/// 删除树后仅保留反汇编代码，内存占用显著下降。</para>
/// <para>JAR/APK 容器：枚举 classes*.dex 条目并支持多 DEX 合并视图（跨条目聚合 Smali 与统计）、
/// 单条目重建写回、资源条目导出/替换，以及接入密钥库 JAR 用途密钥的 v1/v2/v3 签名。</para>
/// DEX editor view model: the right pane shows the full disassembly (Smali) text, with parse,
/// statistics and rebuild-write support. There is no left structure-tree navigation — tens of
/// thousands of tree nodes and TreeView controls were the main memory cost when opening a large
/// DEX; dropping the tree keeps only the disassembly text and cuts the memory footprint sharply.
/// JAR/APK containers: enumerate classes*.dex entries with a merged multi-DEX view (aggregated
/// Smali and statistics), per-entry rebuild write-back, resource export/replace, and v1/v2/v3
/// signing through JAR-purpose keys from the key library.
/// </summary>
public partial class DexEditorViewModel : Document, ISearchableDocument
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

    /// <summary>Active disassembly editor for workspace actions. 供工作区操作使用的反汇编编辑器。</summary>
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
    /// <para>来源是否为 JAR/APK 容器（决定条目/资源/签名面板的可见性）。</para>
    /// Whether the source is a JAR/APK container (drives entry/resource/signing panel visibility).
    /// </summary>
    [ObservableProperty] private bool _isContainer;

    /// <summary>
    /// <para>是否处于多 DEX 合并视图（容器内全部 DEX 条目的聚合浏览，此时禁止保存）。</para>
    /// Whether the merged multi-DEX view is active (aggregated browsing of all container DEX
    /// entries; saving is disabled in this mode).
    /// </summary>
    [ObservableProperty] private bool _isMergeView;

    /// <summary>
    /// <para>容器内全部条目（DEX/签名/资源分类）。</para>
    /// All container entries (classified as DEX/signature/resource).
    /// </summary>
    public ObservableCollection<JarEntryInfo> ContainerEntries { get; } = [];

    /// <summary>
    /// <para>容器内的资源条目（可导出/替换；签名条目只读不在此列出）。</para>
    /// Resource entries in the container (exportable/replaceable; read-only signature entries
    /// are excluded).
    /// </summary>
    public ObservableCollection<JarEntryInfo> ResourceEntries { get; } = [];

    /// <summary>
    /// <para>容器内的 DEX 条目名（工具栏条目下拉数据源）。</para>
    /// Container DEX entry names (the toolbar entry combo source).
    /// </summary>
    public ObservableCollection<string> DexEntryNames { get; } = [];

    /// <summary>
    /// <para>当前选中的 DEX 条目名；合并视图下为空。</para>
    /// The currently selected DEX entry name; empty in the merged view.
    /// </summary>
    [ObservableProperty] private string? _selectedEntry;

    /// <summary>
    /// <para>当前选中的资源条目（导出/替换操作目标）。</para>
    /// The currently selected resource entry (the export/replace target).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedResource))]
    private JarEntryInfo? _selectedResource;

    /// <summary>
    /// <para>是否已选中资源条目（驱动导出/替换按钮可用性）。</para>
    /// Whether a resource entry is selected (drives the export/replace buttons).
    /// </summary>
    public bool HasSelectedResource => SelectedResource is not null;

    /// <summary>
    /// <para>密钥库中的 JAR 用途签名密钥。</para>
    /// JAR-purpose signing keys from the key library.
    /// </summary>
    public ObservableCollection<ManagedKey> JarKeys { get; } = [];

    /// <summary>
    /// <para>当前选中的 JAR 签名密钥。</para>
    /// The currently selected JAR signing key.
    /// </summary>
    [ObservableProperty] private ManagedKey? _selectedJarKey;

    /// <summary>
    /// <para>签名结果文本（成功为输出路径，失败为错误信息）。</para>
    /// The signing result text (the output path on success, the error otherwise).
    /// </summary>
    [ObservableProperty] private string _signStatus = string.Empty;

    /// <summary>
    /// <para>解析后的 DEX 模型（用于统计与重建；合并视图下为空）。</para>
    /// The parsed DEX model (for statistics and rebuild; empty in the merged view).
    /// </summary>
    private DexFile? _dex;

    /// <summary>
    /// <para>解析后的 DEX 模型（用于统计与重建）。</para>
    /// The parsed DEX model (used for statistics and rebuild).
    /// </summary>
    public DexFile? Dex => _dex;

    /// <summary>
    /// <para>类总数；未加载时为 0。合并视图下为全部 DEX 条目的累加。</para>
    /// Total class count; 0 when not loaded. Aggregated across entries in the merged view.
    /// </summary>
    public int ClassCount { get; private set; }

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
    public int StringCount { get; private set; }

    /// <summary>
    /// <para>类型表条目数；未加载时为 0。</para>
    /// Type table entry count; 0 when not loaded.
    /// </summary>
    public int TypeCount { get; private set; }

    /// <summary>
    /// <para>DEX 版本号（如 035）；未加载或合并视图时为空。</para>
    /// The DEX version (e.g. 035); empty when not loaded or in the merged view.
    /// </summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>
    /// <para>保存命令：从模型重建 DEX 字节并写回原文件（容器来源写回选中条目）。</para>
    /// Save command: rebuilds the DEX bytes from the model and writes them back to the file
    /// (container sources write back the selected entry).
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// <para>资源导出命令。</para>
    /// The resource export command.
    /// </summary>
    public IRelayCommand ExportResourceCommand { get; }

    /// <summary>
    /// <para>资源替换命令。</para>
    /// The resource replace command.
    /// </summary>
    public IRelayCommand ReplaceResourceCommand { get; }

    /// <summary>
    /// <para>JAR 签名命令。</para>
    /// The JAR signing command.
    /// </summary>
    public IAsyncRelayCommand SignJarCommand { get; }

    /// <summary>
    /// <para>创建 DEX 编辑器视图模型并初始化命令。</para>
    /// Creates the DEX editor view model and initializes commands.
    /// </summary>
    public DexEditorViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ExportResourceCommand = new RelayCommand(ExportSelectedResource);
        ReplaceResourceCommand = new RelayCommand(ReplaceSelectedResource);
        SignJarCommand = new AsyncRelayCommand(SignJarAsync);
    }

    /// <summary>
    /// <para>异步加载 DEX 或 JAR/APK 容器：容器时枚举条目并默认进入合并视图；普通 DEX 直接解析。</para>
    /// Asynchronously loads a DEX or JAR/APK container: containers enumerate entries and default
    /// to the merged view; plain DEX files parse directly.
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

            // 将 CPU 密集的解析与反汇编放到线程池执行，避免阻塞 UI 线程。
            // Offload the CPU-bound parse and disassembly to the thread pool to keep the UI responsive.
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
    /// <para>加载 JAR/APK 容器：填充条目与资源列表、刷新 JAR 签名密钥，并进入合并视图。</para>
    /// Loads a JAR/APK container: fills the entry/resource lists, refreshes JAR signing keys and
    /// enters the merged view.
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

        // 合并视图为容器默认入口：聚合全部 DEX 条目的反汇编与统计。
        // The merged view is the container default: aggregates disassembly and statistics
        // across all DEX entries.
        await SetMergeViewAsync(true).ConfigureAwait(true);
    }

    /// <summary>
    /// <para>条目下拉选择变化：非合并视图且非加载中时重新加载该条目。</para>
    /// Entry combo selection changed: reloads the entry when not in the merged view or loading.
    /// </summary>
    partial void OnSelectedEntryChanged(string? value)
    {
        if (IsContainer && !IsMergeView && value is not null && value != _loadingEntry)
        {
            _ = LoadEntryAsync(value);
        }
    }

    /// <summary>
    /// <para>正在程序化加载的条目名（防止选择回调重入）。</para>
    /// The entry being loaded programmatically (prevents reentrant selection callbacks).
    /// </summary>
    private string? _loadingEntry;

    /// <summary>
    /// <para>切换合并视图：开启时聚合全部 DEX 条目，关闭时加载第一个（或上次选中的）DEX 条目。</para>
    /// Toggles the merged view: on aggregates all DEX entries; off loads the first (or last
    /// selected) DEX entry.
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
    /// <para>加载容器内指定 DEX 条目（为空时取第一个），生成反汇编文本与统计。</para>
    /// Loads the given container DEX entry (the first when empty), generating disassembly text
    /// and statistics.
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
    /// <para>在后台线程构建合并视图数据：逐条目解析、反汇编并聚合统计，文本按条目分节。</para>
    /// Builds merged-view data on a background thread: parses and disassembles per entry,
    /// aggregates statistics, and sections the text by entry.
    /// </summary>
    private static DexLoadResult LoadMergedModel(string containerPath)
    {
        var classCount = 0;
        var methodCount = 0;
        var fieldCount = 0;
        var stringCount = 0;
        var typeCount = 0;
        // 合并文本与项目范围格式适配共用同一生成器，保证两侧文本逐字节一致。
        // The merged text shares its generator with the project-scope format adapter so both
        // sides produce byte-identical text.
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
    /// <para>保存：从当前模型重建 DEX 字节并写回（容器来源经条目写回；合并视图禁止保存）。</para>
    /// Saves: rebuilds the DEX bytes from the current model and writes them back (container
    /// sources write back the entry; saving is disabled in the merged view).
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
    /// <para>刷新密钥库中的 JAR 用途签名密钥（签名面板下拉数据源）。</para>
    /// Refreshes the JAR-purpose signing keys from the key library (the signing panel source).
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
    /// <para>以选中的 JAR 密钥对来源容器签名，输出到同目录 <c>&lt;name&gt;-signed.jar</c>。</para>
    /// Signs the source container with the selected JAR key, outputting to
    /// <c>&lt;name&gt;-signed.jar</c> in the same directory.
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
    /// <para>把选中的资源条目导出到目标文件（返回 false 表示取消或未选择）。</para>
    /// Exports the selected resource entry to the target file (false when cancelled or nothing
    /// is selected).
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
    /// <para>用本地文件替换选中的资源条目并标记文档为已修改。</para>
    /// Replaces the selected resource entry with a local file and marks the document dirty.
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
    /// <para>资源导出命令入口：由视图经文件选择器回调 <see cref="ExportResourceAsync"/>。</para>
    /// The export command entry: the view drives the picker and calls <see cref="ExportResourceAsync"/>.
    /// </summary>
    private void ExportSelectedResource() => ResourceOperationRequested?.Invoke(this, false);

    /// <summary>
    /// <para>资源替换命令入口：由视图经文件选择器回调 <see cref="ReplaceResourceAsync"/>。</para>
    /// The replace command entry: the view drives the picker and calls <see cref="ReplaceResourceAsync"/>.
    /// </summary>
    private void ReplaceSelectedResource() => ResourceOperationRequested?.Invoke(this, true);

    /// <summary>
    /// <para>资源操作请求事件：参数 true 为替换、false 为导出，由视图弹出文件选择器后调用对应方法。</para>
    /// The resource-operation event: true replaces and false exports; the view shows the picker
    /// and then calls the matching method.
    /// </summary>
    public event EventHandler<bool>? ResourceOperationRequested;

    /// <summary>
    /// <para>应用后台加载结果（UI 线程）：更新文本、统计与脏状态。</para>
    /// Applies a background load result (UI thread): updates text, statistics and dirty state.
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
        return new DexLoadResult(dex, source, dex.Classes.Count, methodCount, fieldCount,
            dex.Strings.Count, dex.Types.Count, dex.Header.Version);
    }

    /// <summary>
    /// <para>释放文档持有的模型与视图数据（文档关闭时调用）：清空反汇编文本、原始字节、解析模型与容器状态。
    /// 调用后 VM 只保留轻量字段，大对象交由 GC 回收。</para>
    /// Releases the model and view data held by this document (called when the document is closed):
    /// clears the disassembly text, raw bytes, parsed model and container state, so the VM retains only
    /// lightweight fields afterwards and the GC can reclaim the memory.
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
    /// <para>后台加载 DEX 的一次性结果数据；<c>Dex</c> 为空表示合并视图（无单一模型）。</para>
    /// One-shot result data of a background DEX load; a null <c>Dex</c> marks the merged view
    /// (no single model).
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