using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Services;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>新建表单的语义模式：顶部菜单入口创建新解决方案；已打开解决方案的入口创建子项目。</para>
/// Semantic mode of the new-project form: the File-menu entry creates a new solution; the entry on an
/// open solution creates a sub-project.
/// </summary>
public enum NewProjectWindowMode
{
    /// <summary><para>新建解决方案（首个子项目与解决方案同名）。</para>New solution (first sub-project shares its name).</summary>
    Solution = 0,

    /// <summary><para>向当前解决方案添加子项目。</para>Add a sub-project to the current solution.</summary>
    SubProject
}

/// <summary>
/// <para>新建项目表单的提交结果；仅在点击创建且校验通过后非空。
/// <see cref="ContainerKind"/> 为 <see cref="ContainerKind.None"/> 时 <see cref="SelectedImagePaths"/>
/// 承载普通多选镜像；为容器时 <see cref="ContainerPath"/> 指向容器文件，
/// <see cref="SelectedZipMembers"/> / <see cref="SelectedPartitions"/> 承载勾选的内部条目。</para>
/// Submitted new-project form result; non-null only after Create is clicked with valid input.
/// When <see cref="ContainerKind"/> is <see cref="ContainerKind.None"/>, <see cref="SelectedImagePaths"/>
/// carries plain multi-selected images; for containers, <see cref="ContainerPath"/> points at the
/// container file and <see cref="SelectedZipMembers"/> / <see cref="SelectedPartitions"/> carry the
/// selected inner entries.
/// </summary>
/// <param name="ProjectName"><para>解决方案/子项目名称。</para>Solution or sub-project name.</param>
/// <param name="Location">
/// <para>组合后的完整目录（<c>基础目录/名称</c>），必须不存在或为空。</para>
/// Combined full directory (<c>base/name</c>); must be missing or empty.
/// </param>
/// <param name="ContainerKind"><para>单文件容器类型（无/ super / payload / zip）。</para>Single-file container kind (none / super / payload / zip).</param>
/// <param name="ContainerPath"><para>容器文件路径；非容器时为 null。</para>Container file path; null when not a container.</param>
/// <param name="SelectedImagePaths"><para>普通多选镜像的完整路径。</para>Full paths of plain multi-selected images.</param>
/// <param name="SelectedZipMembers"><para>ZIP 容器中勾选的条目完整路径。</para>Selected ZIP member full paths.</param>
/// <param name="SelectedPartitions">
/// <para>勾选的分区名（super 逻辑分区或 payload 分区，按 <see cref="ContainerKind"/> 区分）。</para>
/// Selected partition names (super logical partitions or payload partitions, per <see cref="ContainerKind"/>).
/// </param>
/// <param name="UnpackImages"><para>是否解压镜像。</para>Whether to unpack images.</param>
/// <param name="UnpackFully"><para>是否把解出的受支持 img 继续解压为同名文件夹。</para>Whether to unpack extracted supported imgs into same-name folders.</param>
public sealed record NewProjectFormResult(
    string ProjectName,
    string Location,
    ContainerKind ContainerKind,
    string? ContainerPath,
    string[] SelectedImagePaths,
    string[] SelectedZipMembers,
    string[] SelectedPartitions,
    bool UnpackImages,
    bool UnpackFully,
    bool CopyOriginal = true,
    string? RemoteUrl = null);

/// <summary>
/// <para>新建项目配置窗口；维护表单与文件选择，通过 <see cref="CreateAsync"/> 等待调用方完成创建和解包后关闭。
/// 操作期间保留模态窗口并显示进度。位置显示组合后的完整目录（<c>基础目录/名称</c>）并随名称联动；
/// 单个 super / payload.bin / zip 选中时在后台分析内部结构并把分区/镜像条目加入勾选列表。</para>
/// New project form; holds local selections, outputs <see cref="Result"/> on create so the caller
/// creates the project and runs unpacking. The location shows the combined full directory
/// (<c>base/name</c>) synced with the name; a single super / payload.bin / zip selection triggers a
/// background structure analysis whose partitions/images join the selectable list.
/// </summary>
public partial class NewProjectWindow : Window
{
    private const double ImageRowHeight = 28;
    private const double MinimumImageColumnWidth = 240;
    private const int MaximumImageColumns = 3;
    private NewProjectImageEntry[] _images = [];
    private int _imageColumnCount;
    private bool _updatingSelection;
    private bool _updatingLocation;
    private string _baseDirectory;
    private int _analysisGeneration;
    private ContainerKind _containerKind;
    private string? _containerPath;
    private bool _isAnalyzing;
    private bool _isCreating;
    private CancellationTokenSource? _creationCancellation;
    private IReadOnlyList<ContainerEntry> _containerEntries = [];
    private string? _remoteUrl;

    /// <summary>Queries available bytes; replaceable by window validation without filling a real disk.</summary>
    internal Func<string, long> AvailableSpace { get; set; } = DiskSpaceEstimator.GetFreeBytes;

    /// <summary>Runs creation while the modal form displays progress.</summary>
    public Func<NewProjectFormResult, Task>? CreateAsync { get; set; }
    public Func<NewProjectFormResult, CancellationToken, Task>? CreateWithCancellationAsync { get; set; }

    /// <summary><para>滑动窗口计量器：统计「已处理/总量 · 速度 · 剩余时间」。</para>
    /// Sliding-window meter for the "processed/total · speed · eta" stats line.</summary>
    private readonly ProgressMeter _meter = new();
    private int _progressGeneration;

    /// <summary>Updates operation status on the UI thread.</summary>
    public void ReportProgress(string status, double? percent = null, (long Processed, long Total)? bytes = null, string? statistics = null)
    {
        ProgressPanel.IsVisible = true;
        if (bytes is { Total: > 0 } measured) percent = measured.Processed * 100.0 / measured.Total;
        percent = percent is { } value && double.IsFinite(value) && value >= 0 ? Math.Clamp(value, 0, 100) : null;
        MainBusyContainer.LoadingMessage = status;
        MainBusyContainer.IsLoading = true;
        OperationProgress.IsIndeterminate = percent is null;
        OperationProgress.Value = percent ?? 0;
        OperationStats.Text = statistics ?? string.Empty;
        if (bytes is { } sample)
        {
            _meter.Report(sample.Processed, sample.Total);

            OperationStats.Text = _meter.FormatStats(_meter.EstimatedRemaining is null
                ? LanguageService.Instance.GetString("Progress_StatsNoEta")
                : LanguageService.Instance.GetString("Progress_Stats"));
        }
    }

    /// <summary><para>阶段切换时清空样本与统计文本。</para>Clears samples and the stats text on stage transitions.</summary>
    public void ResetProgressStats()
    {
        _progressGeneration++;
        _meter.Reset();
        OperationStats.Text = string.Empty;
    }

    /// <summary>Begins one byte-counted stage; late callbacks from earlier stages are discarded.</summary>
    public IProgress<(long Processed, long Total)> BeginByteProgress(string status)
    {
        ResetProgressStats();
        ReportProgress(status);
        var generation = _progressGeneration;
        return new StageProgress(sample =>
        {
            if (generation == _progressGeneration) ReportProgress(status, bytes: sample);
        });
    }

    private sealed class StageProgress(Action<(long Processed, long Total)> report) : IProgress<(long Processed, long Total)>
    {
        private readonly object _gate = new();
        private (long Processed, long Total) _latest;
        private bool _queued;

        public void Report((long Processed, long Total) value)
        {
            lock (_gate)
            {
                _latest = value;
                if (_queued) return;
                _queued = true;
            }
            Dispatcher.UIThread.Post(() =>
            {
                (long Processed, long Total) sample;
                lock (_gate) { sample = _latest; _queued = false; }
                report(sample);
            });
        }
    }

    /// <summary><para>表单提交结果；取消或关闭时保持为空。</para>Form result; stays null on cancel/close.</summary>
    public NewProjectFormResult? Result { get; private set; }

    /// <summary>
    /// <para>无参构造（供 XAML 运行时加载器识别）：等价于 Solution 模式 + 应用所在目录默认值。</para>
    /// Parameterless constructor (recognized by the XAML runtime loader): equivalent to Solution
    /// mode with the app-directory defaults.
    /// </summary>
    public NewProjectWindow() : this(NewProjectWindowMode.Solution, null, null)
    {
    }

    /// <summary>
    /// <para>初始化新建表单：位置默认 <c>&lt;baseDirectory&gt;/&lt;默认名&gt;</c>，
    /// 默认名为基础目录下第一个空闲的 NewProjectN（<paramref name="reservedNames"/> 亦视为占用）。</para>
    /// Initializes the form: the location defaults to <c>&lt;baseDirectory&gt;/&lt;default name&gt;</c>,
    /// where the default name is the first free NewProjectN under the base directory (names in
    /// <paramref name="reservedNames"/> count as taken too).
    /// </summary>
    /// <param name="mode"><para>表单语义模式。</para>Semantic mode of the form.</param>
    /// <param name="baseDirectory">
    /// <para>默认基础目录；null 时使用应用所在目录（工具箱同级）。</para>
    /// Default base directory; null uses the app directory (next to the toolbox).
    /// </param>
    /// <param name="reservedNames">
    /// <para>额外视为占用的名称（如现有子项目名）。</para>Additional taken names (e.g. existing sub-projects).</param>
    public NewProjectWindow(
        NewProjectWindowMode mode,
        string? baseDirectory,
        IEnumerable<string>? reservedNames)
    {
        InitializeComponent();

        Title = LanguageService.Instance.GetString(mode == NewProjectWindowMode.Solution
            ? "Menu_NewSolution"
            : "Explorer_NewEmptySubProject");

        Closed += (_, _) => ResetProgressStats();
        Mode = mode;
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory!;
        ReservedNames = reservedNames?.ToArray() ?? [];

        ImageList.SizeChanged += (_, _) => UpdateImageColumns();
        SelectAllBox.IsCheckedChanged += OnSelectAllChanged;
        UnpackFullyBox.IsCheckedChanged += (_, _) =>
        {
            if (UnpackFullyBox.IsChecked == true) UnpackImagesBox.IsChecked = true;
        };
        UnpackImagesBox.IsCheckedChanged += (_, _) =>
        {
            if (UnpackImagesBox.IsChecked != true) UnpackFullyBox.IsChecked = false;
        };

        ProjectNameBox.TextChanging += OnProjectNameChanged;
        ProjectLocationBox.TextChanging += OnLocationTextChanged;
        Opened += (_, _) => ProjectNameBox.Focus();
        Closing += (_, e) => e.Cancel = _isCreating;
        Closed += (_, _) => { _analysisGeneration++; DetachImageHandlers(); };

        ProjectNameBox.Text = NewProjectDirectoryHelper.GetNextDefaultName(_baseDirectory, ReservedNames);
        UpdateLocationText();
        UpdateCreateEnabled();
    }

    /// <summary><para>表单语义模式。</para>Semantic mode of the form.</summary>
    public NewProjectWindowMode Mode { get; }

    /// <summary><para>默认名探测时额外视为占用的名称。</para>Names treated as taken when probing the default name.</summary>
    private string[] ReservedNames { get; }

    private async void OnBrowseLocationClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("NewProject_BrowseLocation"),
                AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {

                _baseDirectory = path;
                UpdateLocationText();
            }
        }
        catch (Exception ex)
        {
            await ShowPickerErrorAsync(ex);
        }
    }

    private async void OnBrowseImagesClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen) return;
        ImagePicker.IsEnabled = false;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("NewProject_BrowseImages"),
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType(LanguageService.Instance.GetString("NewProject_ImageFiles"))
                    {
                        Patterns = ["*.img", "*.zip", "*.bin", "*.raw", "*.ext4", "*.erofs", "*.f2fs", "*.dat", "*.br", "*.ozip", "*.ofp", "*.ops", "*.pac", "*.super", "*.app", "*.simg", "*.sparse"]
                    },
                    FilePickerFileTypes.All
                ]
            });
            if (files.Count == 0) return;
            var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>()
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
            if (IsVisible) SetImages([.. paths.Select(path => new NewProjectImageEntry(path))]);
        }
        catch (Exception ex)
        {
            await ShowPickerErrorAsync(ex);
        }
        finally
        {
            ImagePicker.IsEnabled = true;
        }
    }

    private Task ShowPickerErrorAsync(Exception exception) => IsVisible
        ? DialogService.ShowAsync(this, LanguageService.Instance.GetString("NewProject_PickerFailed"),
            global::UotanStudio.Services.UserError.Describe(exception), Ursa.Controls.MessageBoxIcon.Error)
        : Task.CompletedTask;

    private async void OnGetRomClick(object? sender, RoutedEventArgs e)
    {
        var address = ImagePathsBox.Text?.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await DialogService.ShowAsync(this, LanguageService.Instance.GetString("NewProject_GetRom"),
                LanguageService.Instance.GetString("NewProject_RemoteUrlRequired"), Ursa.Controls.MessageBoxIcon.Warning);
            return;
        }

        ImagePicker.IsEnabled = false;
        _isAnalyzing = true;
        UpdateCreateEnabled();
        MainBusyContainer.IsLoading = true;
        ProgressPanel.IsVisible = true;
        ResetProgressStats();
        ReportProgress(LanguageService.Instance.GetString("NewProject_Analyzing"));
        var generation = ++_analysisGeneration;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var analysis = await new RemoteRomService(client).AnalyzeAsync(uri.ToString());
            if (generation != _analysisGeneration || !IsVisible) return;
            _remoteUrl = analysis.Url.ToString();
            _containerKind = analysis.Kind;
            _containerPath = null;
            _containerEntries = analysis.Entries;
            DetachImageHandlers();
            List<NewProjectImageEntry> entries = [new(uri.ToString(), "remote", isRemoteSource: true)];
            entries.AddRange(analysis.Entries.Select(entry => new NewProjectImageEntry(entry.Name, entry.Format,
                analysis.Url.ToString(), entry.FromPayload, entry.Name, entry.PayloadMemberPath)));
            _images = [.. entries];
            foreach (var image in _images) image.PropertyChanged += OnImageSelectionChanged;
            _imageColumnCount = 0;
            UpdateImageColumns();
            ImageListPanel.IsVisible = true;
            ClearImagesButton.IsVisible = true;
            UpdateSelectAll();
            ImagePathsBox.Text = analysis.Url.ToString();
            ToolTip.SetTip(ImagePathsBox, analysis.Url.ToString());
        }
        catch (Exception ex)
        {
            _remoteUrl = null;
            _containerKind = ContainerKind.None;
            _containerEntries = [];
            await ShowPickerErrorAsync(ex);
        }
        finally
        {
            if (generation == _analysisGeneration)
            {
                _isAnalyzing = false;
                UpdateCreateEnabled();
                ProgressPanel.IsVisible = false;
                MainBusyContainer.IsLoading = false;
            }
            ImagePicker.IsEnabled = true;
        }
    }

    /// <summary>
    /// <para>设置列表条目并刷新列布局；恰好一个普通文件时在后台分析容器结构，
    /// 分析成功后以分组条目重建列表（取消前一次未完成的分析）。</para>
    /// Sets list entries and refreshes columns; when exactly one plain file is present, its container
    /// structure is analyzed in the background and, on success, the list is rebuilt with grouped
    /// entries (a previous in-flight analysis is cancelled by generation).
    /// </summary>
    private async void SetImages(NewProjectImageEntry[] images)
    {
        DetachImageHandlers();
        _images = images;
        foreach (var image in images) image.PropertyChanged += OnImageSelectionChanged;
        _imageColumnCount = 0;
        UpdateImageColumns();
        ImagePathsBox.Text = string.Join("; ", images.Select(image => image.FilePath ?? image.Name));
        ToolTip.SetTip(ImagePathsBox, ImagePathsBox.Text);
        ImageListPanel.IsVisible = images.Length > 0;
        ClearImagesButton.IsVisible = images.Length > 0;
        UpdateSelectAll();

        _containerKind = ContainerKind.None;
        _containerPath = null;
        _containerEntries = [];
        _remoteUrl = null;
        var generation = ++_analysisGeneration;
        var single = images.Length == 1 ? images[0].FilePath : null;
        _isAnalyzing = single is not null;
        UpdateCreateEnabled();
        ProgressPanel.IsVisible = _isAnalyzing;
        MainBusyContainer.IsLoading = _isAnalyzing;
        if (single is null) return;
        ResetProgressStats();
        ReportProgress(LanguageService.Instance.GetString("NewProject_Analyzing"));
        try
        {
            var structure = await Task.Run(() => ContainerStructureAnalyzer.Analyze(single));
            if (generation != _analysisGeneration || !IsVisible) return;
            if (structure.Kind != ContainerKind.None) ApplyContainerStructure(single, structure);
        }
        catch (Exception ex)
        {
            if (generation == _analysisGeneration && IsVisible) await ShowPickerErrorAsync(ex);
        }
        finally
        {
            if (generation == _analysisGeneration)
            {
                _isAnalyzing = false;
                UpdateCreateEnabled();
                ProgressPanel.IsVisible = false;
                MainBusyContainer.IsLoading = false;
            }
        }
    }

    /// <summary>
    /// <para>以原包复制选项和内部条目重建列表，复用自适应多列布局。</para>
    /// Rebuilds the adaptive columns with an original-file copy option and selectable inner entries.
    /// </summary>
    private void ApplyContainerStructure(string containerPath, ContainerStructure structure)
    {
        DetachImageHandlers();
        _containerKind = structure.Kind;
        _containerPath = containerPath;
        _containerEntries = structure.Entries;
        var duplicateZipNames = structure.Kind == ContainerKind.Zip
            ? structure.Entries.Where(entry => !entry.FromPayload)
                .GroupBy(entry => Path.GetFileName(entry.Name.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        List<NewProjectImageEntry> entries = [new(containerPath, structure.Kind.ToString().ToLowerInvariant())];
        entries.AddRange(structure.Entries.Select(entry =>
        {
            var fileName = Path.GetFileName(entry.Name.Replace('\\', '/'));
            var displayName = structure.Kind == ContainerKind.Zip && !entry.FromPayload && !duplicateZipNames.Contains(fileName)
                ? fileName : entry.Name;
            return new NewProjectImageEntry(entry.Name, entry.Format, containerPath, entry.FromPayload, displayName, entry.PayloadMemberPath);
        }));
        _images = [.. entries];
        foreach (var image in _images) image.PropertyChanged += OnImageSelectionChanged;
        _imageColumnCount = 0;
        UpdateImageColumns();
        UpdateSelectAll();
    }

    /// <summary><para>分组头的本地化标题。</para>Localized group-header label.</summary>
    private static string GroupLabelFor(ContainerKind kind, string containerPath)
    {
        var name = Path.GetFileName(containerPath);
        return kind switch
        {
            ContainerKind.Super => string.Format(LanguageService.Instance.GetString("NewProject_GroupSuperPartitions"), name),
            ContainerKind.Payload => string.Format(LanguageService.Instance.GetString("NewProject_GroupPayloadPartitions"), name),
            ContainerKind.Zip => string.Format(LanguageService.Instance.GetString("NewProject_GroupZipImages"), name),
            _ => name
        };
    }

    private void UpdateImageColumns()
    {

        var rowsPerColumn = Math.Max(1, (int)Math.Floor(ImageList.Bounds.Height / ImageRowHeight));
        var widthLimit = Math.Clamp((int)(ImageList.Bounds.Width / MinimumImageColumnWidth), 1, MaximumImageColumns);
        var requiredColumns = (int)Math.Ceiling((double)_images.Length / rowsPerColumn);
        var columns = Math.Clamp(requiredColumns, 1, widthLimit);
        if (columns == _imageColumnCount) return;
        _imageColumnCount = columns;
        ImageHeaders.ItemsSource = Enumerable.Range(0, columns).ToArray();
        ImageList.ItemsSource = _images.Chunk(columns).Select(entries => new NewProjectImageRow(entries, columns)).ToArray();
    }

    private void DetachImageHandlers()
    {
        foreach (var image in _images) image.PropertyChanged -= OnImageSelectionChanged;
    }

    private void OnSelectAllChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        foreach (var image in _images) image.IsSelected = SelectAllBox.IsChecked == true;
        _updatingSelection = false;
    }

    private void OnImageSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingSelection && e.PropertyName == nameof(NewProjectImageEntry.IsSelected)) UpdateSelectAll();
    }

    private void UpdateSelectAll()
    {
        _updatingSelection = true;
        SelectAllBox.IsChecked = _images.Where(image => image.IsSelectable).All(image => image.IsSelected);
        _updatingSelection = false;
    }

    private void OnClearImagesClick(object? sender, RoutedEventArgs e)
    {
        _analysisGeneration++;
        SetImages([]);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_isCreating)
        {
            _creationCancellation?.Cancel();
            CancelButton.Content = "正在取消…";
            CancelButton.IsEnabled = false;
            return;
        }
        Close();
    }

    /// <summary>
    /// <para>名称变化时始终联动位置后缀（基础目录 + 当前名称）。</para>
    /// Name changes always re-sync the location as base directory + current name.
    /// </summary>
    private void OnProjectNameChanged(object? sender, TextChangingEventArgs e)
    {
        UpdateCreateEnabled();
        UpdateLocationText();
    }

    private void OnLocationTextChanged(object? sender, TextChangingEventArgs e)
    {
        if (_updatingLocation) return;

        var name = ProjectNameBox.Text?.Trim();
        if (ProjectLocationBox.Text is { Length: > 0 } text)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var currentName = name ?? string.Empty;
            var strip = currentName.Length > 0
                && (text.EndsWith(Path.DirectorySeparatorChar + currentName, comparison)
                    || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                        && text.EndsWith(Path.AltDirectorySeparatorChar + currentName, comparison)));
            _baseDirectory = strip ? text[..^currentName.Length] : text;
        }
        UpdateCreateEnabled();
    }

    /// <summary><para>按基础目录 + 当前名称刷新位置显示。</para>Refreshes the location display from base directory + current name.</summary>
    private void UpdateLocationText()
    {
        var name = ProjectNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _updatingLocation = true;
        try
        {
            ProjectLocationBox.Text = Path.Combine(_baseDirectory, name);
        }
        finally
        {
            _updatingLocation = false;
        }
    }

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {

        if (_isAnalyzing || _isCreating || !IsFormValid()) return;

        var selected = _images.Where(image => image.IsSelectable && image.IsSelected).ToArray();
        var containerPath = _containerPath;
        var kind = _containerKind;
        Result = new NewProjectFormResult(
            ProjectNameBox.Text?.Trim() ?? string.Empty,
            ProjectLocationBox.Text?.Trim() ?? string.Empty,
            kind,
            containerPath,
            selected.Where(image => image.FilePath is not null).Select(image => image.FilePath!).ToArray(),
            selected.Where(image => image.FilePath is null && !image.FromPayload).Select(image => image.Name).ToArray(),
            selected.Where(image => image.FilePath is null && (kind != ContainerKind.Zip || image.FromPayload))
                .Select(image => image.Name).ToArray(),
            UnpackImagesBox.IsChecked == true,
            UnpackFullyBox.IsChecked == true,
            selected.Any(image => image.FilePath == containerPath || image.IsRemoteSource),
            _remoteUrl);
        _isCreating = true;
        UpdateCreateEnabled();
        _creationCancellation = new CancellationTokenSource();
        FormContent.IsEnabled = false;
        CancelButton.IsEnabled = true;
        MainBusyContainer.IsLoading = true;
        ReportProgress(LanguageService.Instance.GetString("NewProject_CheckingSpace"));
        ResetProgressStats();
        try
        {
            if (!await ConfirmCreationSpaceAsync(Result))
            {
                Result = null;
                ProgressPanel.IsVisible = false;
                MainBusyContainer.IsLoading = false;
                return;
            }
            ReportProgress(LanguageService.Instance.GetString("NewProject_Creating"));
            if (CreateWithCancellationAsync is not null) await CreateWithCancellationAsync(Result, _creationCancellation.Token);
            else if (CreateAsync is not null) await CreateAsync(Result);
            ResetProgressStats();
            _isCreating = false;
            Close();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException || _creationCancellation?.IsCancellationRequested == true)
            {
                Result = null;
                ResetProgressStats();
                Close();
                return;
            }
            Result = null;
            _isCreating = false;
            CancelButton.IsEnabled = true;
            CancelButton.Content = "取消";
            ResetProgressStats();
            ReportProgress(global::UotanStudio.Services.UserError.Describe(ex), 0);
            await DialogService.ShowAsync(this, LanguageService.Instance.GetString("NewProject_CreateFailed"),
                global::UotanStudio.Services.UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
        finally
        {
            _isCreating = false;
            FormContent.IsEnabled = true;
            UpdateCreateEnabled();
            CancelButton.IsEnabled = true;
            CancelButton.Content = "取消";
            _creationCancellation?.Dispose();
            _creationCancellation = null;
            MainBusyContainer.IsLoading = false;
        }
    }

    private async Task<bool> ConfirmCreationSpaceAsync(NewProjectFormResult form)
    {
        var plainImages = form.ContainerKind == ContainerKind.None;
        var copyPaths = plainImages ? form.SelectedImagePaths
            : form.CopyOriginal && form.ContainerPath is not null ? new[] { form.ContainerPath } : [];
        var selected = form.UnpackImages ? _containerEntries.Where(entry =>
            (entry.FromPayload || form.ContainerKind != ContainerKind.Zip
                ? form.SelectedPartitions : form.SelectedZipMembers).Contains(entry.Name, StringComparer.Ordinal)).ToArray() : [];
        var payloadZip = form.UnpackImages && form.ContainerKind == ContainerKind.Zip
            && form.SelectedPartitions.Length > 0 ? form.ContainerPath : null;
        var shortages = await Task.Run(() =>
        {
            var estimate = ProjectCreationSpaceEstimator.Estimate(copyPaths, selected,
                plainImages && form.UnpackImages, form.UnpackImages && form.UnpackFully, payloadZip);
            return ProjectCreationSpaceEstimator.FindShortages(form.Location, estimate, AvailableSpace);
        });
        if (shortages.Count == 0) return true;
        var language = LanguageService.Instance;
        var details = string.Join(Environment.NewLine + Environment.NewLine, shortages.Select(shortage =>
            string.Format(language.GetString("NewProject_SpaceDetails"), shortage.Directory,
                ProgressMeter.FormatSize(shortage.AvailableBytes), ProgressMeter.FormatSize(shortage.RequiredBytes))));
        return await DialogService.ConfirmAsync(this, language.GetString("NewProject_SpaceTitle"),
            string.Format(language.GetString("NewProject_SpaceWarning"), details),
            language.GetString("NewProject_SpaceContinue"), language.GetString("NewProject_SpaceCancel"));
    }

    /// <summary>
    /// <para>刷新「创建」按钮的可用性：只有名称与位置都合法时才允许提交，未填写完整时直接禁用按钮，
    /// 不再在输入框下方用红字提示。</para>
    /// Refreshes the Create button state: submitting is allowed only when both the name and the location are
    /// valid, so an incomplete form disables the button instead of showing inline error text.
    /// </summary>
    private void UpdateCreateEnabled() => CreateButton.IsEnabled = IsFormValid();

    /// <summary>
    /// <para>表单是否可提交：名称非空且合法、未被占用，位置非空且目录不存在或为空。</para>
    /// Whether the form can be submitted: the name is non-empty, valid and not taken; the location is
    /// non-empty and points at a missing or empty directory.
    /// </summary>
    private bool IsFormValid()
    {
        if (_isAnalyzing || _isCreating) return false;
        var name = ProjectNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\']) >= 0) return false;
        if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return ProjectLocationBox.Text is { } location
            && !string.IsNullOrWhiteSpace(location)
            && NewProjectDirectoryHelper.IsDirectoryEmptyOrMissing(location);
    }
}

/// <summary>
/// <para>新建项目列表条目：普通镜像文件、容器内部条目或分组头。</para>
/// New-project list entry: a plain image file, a container inner entry, or a group header.
/// </summary>
internal sealed partial class NewProjectImageEntry : ObservableObject
{
    /// <summary><para>分组头行（不可勾选）。</para>Group-header row (not selectable).</summary>
    public static NewProjectImageEntry GroupHeader(string label) => new(label, isGroupHeader: true);

    /// <summary><para>普通镜像文件条目。</para>Plain image-file entry.</summary>
    public NewProjectImageEntry(string filePath, string format = "—", bool isRemoteSource = false)
    {
        FilePath = filePath;
        Name = Path.GetFileName(filePath);
        DisplayName = Name;
        ContainerPath = filePath;
        Format = format;
        IsRemoteSource = isRemoteSource;
    }

    /// <summary><para>容器内部条目（分区名或 ZIP 内路径）。</para>Container inner entry (partition name or ZIP path).</summary>
    public NewProjectImageEntry(string innerName, string format, string containerPath, bool fromPayload, string? displayName = null, string? payloadMemberPath = null)
    {
        FilePath = null;
        Name = innerName;
        DisplayName = displayName ?? innerName;
        ContainerPath = containerPath;
        Format = format;
        FromPayload = fromPayload;
        PayloadMemberPath = payloadMemberPath;
    }

    private NewProjectImageEntry(string label, bool isGroupHeader)
    {
        FilePath = null;
        Name = label;
        DisplayName = label;
        ContainerPath = string.Empty;
        Format = string.Empty;
        IsGroupHeader = isGroupHeader;
    }

    /// <summary><para>普通镜像的完整路径；内部条目与分组头为 null。</para>Full path of a plain image; null for inner entries and group headers.</summary>
    public string? FilePath { get; }

    /// <summary><para>条目标识：ZIP 条目始终保留完整内部路径用于提取。</para>Entry identity; ZIP members retain their full archive paths for extraction.</summary>
    public string Name { get; }

    /// <summary><para>ZIP 条目默认显示文件名，重名时显示完整内部路径。</para>ZIP filename, expanded to the full member path when filenames collide.</summary>
    public string DisplayName { get; }

    /// <summary><para>所属容器路径（悬停提示用）。</para>Owning container path (used for tooltips).</summary>
    public string ContainerPath { get; }

    /// <summary><para>格式列显示文本。</para>Format column text.</summary>
    public string Format { get; }

    /// <summary><para>是否来自 payload 的分区（ZIP 模式下区分 ZIP 成员与 payload 分区）。</para>Whether the entry is a payload partition (distinguishes ZIP members in zip mode).</summary>
    public bool FromPayload { get; }

    /// <summary>Whether this row represents the complete remote source package.</summary>
    public bool IsRemoteSource { get; }

    /// <summary><para>是否分组头行。</para>Whether this is a group-header row.</summary>
    public bool IsGroupHeader { get; }

    /// <summary><para>是否可勾选（分组头不可）。</para>Whether the entry is selectable (group headers are not).</summary>
    public bool IsSelectable => !IsGroupHeader;

    /// <summary><para>悬停提示：普通文件显示完整路径，内部条目显示容器路径。</para>Tooltip: full path for plain files, container path for inner entries.</summary>
    public string Tip => FilePath ?? ContainerPath + "!/" + (PayloadMemberPath is null ? "" : PayloadMemberPath + "!/") + Name;

    /// <summary>Containing Payload member path for the full nested location tooltip.</summary>
    public string? PayloadMemberPath { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    /// <inheritdoc />
    partial void OnIsSelectedChanged(bool value)
    {

        if (IsGroupHeader && value)
        {
            _isSelected = false;
        }
    }
}

internal sealed record NewProjectImageRow(NewProjectImageEntry[] Images, int ColumnCount);