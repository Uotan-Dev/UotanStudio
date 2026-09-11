using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;

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
    bool CopyOriginal = true);

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
    private bool _validationRequested;
    private bool _updatingLocation;
    private bool _locationEdited;
    private string _baseDirectory;
    private int _analysisGeneration;
    private ContainerKind _containerKind;
    private string? _containerPath;
    private bool _isAnalyzing;
    private bool _isCreating;

    /// <summary>Runs creation while the modal form displays progress.</summary>
    public Func<NewProjectFormResult, Task>? CreateAsync { get; set; }

    /// <summary>Updates operation status on the UI thread.</summary>
    public void ReportProgress(string status, double? percent = null)
    {
        ProgressPanel.IsVisible = true;
        OperationStatus.Text = status;
        OperationProgress.IsIndeterminate = percent is null;
        OperationProgress.Value = percent ?? 0;
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
        ProjectNameBox.TextChanged += OnProjectNameChanged;
        ProjectLocationBox.TextChanged += OnLocationTextChanged;
        Opened += (_, _) => ProjectNameBox.Focus();
        Closing += (_, e) => e.Cancel = _isCreating;
        Closed += (_, _) => { _analysisGeneration++; DetachImageHandlers(); };

        ProjectNameBox.Text = ContainerStructureAnalyzer.GetNextDefaultName(_baseDirectory, ReservedNames);
        UpdateLocationText();
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
                // “选择目录”选定的是基础目录，位置仍然拼接当前名称显示。
                // The picker chooses the base directory; the location still shows base + current name.
                _baseDirectory = path;
                _locationEdited = false;
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
            exception.Message, Ursa.Controls.MessageBoxIcon.Error)
        : Task.CompletedTask;

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
        var generation = ++_analysisGeneration;
        var single = images.Length == 1 ? images[0].FilePath : null;
        _isAnalyzing = single is not null;
        CreateButton.IsEnabled = !_isAnalyzing;
        ProgressPanel.IsVisible = _isAnalyzing;
        if (single is null) return;
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
                CreateButton.IsEnabled = true;
                ProgressPanel.IsVisible = false;
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
        var duplicateZipNames = structure.Kind == ContainerKind.Zip
            ? structure.Entries.Where(entry => !entry.FromPayload)
                .GroupBy(entry => Path.GetFileName(entry.Name.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var entries = new List<NewProjectImageEntry> { new(containerPath, structure.Kind.ToString().ToLowerInvariant()) };
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
        // Use the fewest columns that fit the available height, while keeping each column readable.
        // 根据列表可用高度选择最少列数，并限制每列的最小宽度；窗口增高时自动合并列。
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

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// <para>名称变化时联动位置后缀；用户手动编辑位置后不再联动。</para>
    /// Name changes re-sync the location suffix; after the user edits the location manually the
    /// syncing stops.
    /// </summary>
    private void OnProjectNameChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateValidation();
        if (_locationEdited) return;
        UpdateLocationText();
    }

    private void OnLocationTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingLocation) return;
        _locationEdited = true;
        UpdateValidation();
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
        if (_isAnalyzing || _isCreating) return;
        _validationRequested = true;
        UpdateValidation();
        if (ProjectNameError.IsVisible) ProjectNameBox.Focus();
        else if (ProjectLocationError.IsVisible) ProjectLocationBox.Focus();
        else
        {
            // Preserve analyzed identity; never repeat metadata I/O on the UI thread at submission.
            // 提交时保留分析结果，避免在 UI 线程重新读取原包元数据。
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
                selected.Any(image => image.FilePath == containerPath));
            if (CreateAsync is null) { Close(); return; }
            _isCreating = true;
            FormContent.IsEnabled = false;
            CreateButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            ReportProgress(LanguageService.Instance.GetString("NewProject_Creating"));
            try
            {
                await CreateAsync(Result);
                _isCreating = false;
                Close();
            }
            catch (Exception ex)
            {
                Result = null;
                _isCreating = false;
                CancelButton.IsEnabled = true;
                ReportProgress(ex.Message, 0);
                await DialogService.ShowAsync(this, LanguageService.Instance.GetString("NewProject_CreateFailed"),
                    ex.Message, Ursa.Controls.MessageBoxIcon.Error);
            }
        }
    }

    private void UpdateValidation()
    {
        if (!_validationRequested) return;
        SetRequiredError(ProjectNameBox, ProjectNameError, "NewProject_NameRequired");
        SetRequiredError(ProjectLocationBox, ProjectLocationError, "NewProject_LocationRequired");
        var name = ProjectNameBox.Text?.Trim() ?? "";
        if (!ProjectNameError.IsVisible && (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.IndexOfAny(['/', '\\']) >= 0 || ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            ProjectNameBox.SetValue(DataValidationErrors.HasErrorsProperty, true);
            ProjectNameError.Text = LanguageService.Instance.GetString("NewProject_NameInvalid");
            ProjectNameError.IsVisible = true;
        }
        if (!ProjectLocationError.IsVisible && ProjectLocationBox.Text is { } location
            && !ContainerStructureAnalyzer.IsDirectoryEmptyOrMissing(location))
        {
            // 目录已存在且非空：新建要求全新目录。
            // The directory exists and is not empty: a fresh directory is required.
            ProjectLocationBox.SetValue(DataValidationErrors.HasErrorsProperty, true);
            ProjectLocationError.Text = LanguageService.Instance.GetString("NewProject_LocationNotEmpty");
            ProjectLocationError.IsVisible = true;
        }
    }

    private void SetRequiredError(TextBox input, TextBlock error, string requiredKey)
    {
        var invalid = string.IsNullOrWhiteSpace(input.Text);
        input.SetValue(DataValidationErrors.HasErrorsProperty, invalid);
        error.Text = LanguageService.Instance.GetString(requiredKey);
        error.IsVisible = invalid;
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
    public NewProjectImageEntry(string filePath, string format = "—")
    {
        FilePath = filePath;
        Name = Path.GetFileName(filePath);
        DisplayName = Name;
        ContainerPath = filePath;
        Format = format;
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
        // 分组头不可勾选：忽略外部批量置位。
        // Group headers are not selectable: batch toggles are ignored.
        if (IsGroupHeader && value)
        {
            _isSelected = false;
        }
    }
}

internal sealed record NewProjectImageRow(NewProjectImageEntry[] Images, int ColumnCount);
