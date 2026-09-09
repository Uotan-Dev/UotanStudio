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
    bool UnpackFully);

/// <summary>
/// <para>新建项目配置窗口；维护表单与文件选择，点击创建时输出 <see cref="Result"/> 并关闭，
/// 由调用方创建工程与执行解包。位置显示组合后的完整目录（<c>基础目录/名称</c>）并随名称联动；
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
        Closed += (_, _) => DetachImageHandlers();

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
                        Patterns = ["*.img", "*.zip", "*.bin", "*.raw", "*.ext4", "*.erofs", "*.f2fs", "*.dat", "*.br", "*.ozip", "*.ofp", "*.ops", "*.pac", "*.super"]
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
    private void SetImages(NewProjectImageEntry[] images)
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

        var generation = ++_analysisGeneration;
        var single = images.Length == 1 && images[0].FilePath is not null ? images[0].FilePath! : null;
        if (single is null)
        {
            return;
        }

        // 容器结构分析在后台执行；完成后若仍是当前一代结果则重建为分组列表。
        // Structure analysis runs in the background; the list is rebuilt only when the result
        // still belongs to the latest generation.
        Task.Run(() => (single, ContainerStructureAnalyzer.Analyze(single)))
            .ContinueWith(t =>
            {
                if (t.IsFaulted || generation != _analysisGeneration) return;
                var (path, structure) = t.Result;
                if (structure.Kind == ContainerKind.None || structure.Entries.Count == 0) return;
                Dispatcher.UIThread.Post(() => ApplyContainerStructure(path, structure));
            });
    }

    /// <summary>
    /// <para>以容器结构重建列表：分组头 + 内部条目（ZIP 成员与 payload/super 分区），单列展示。</para>
    /// Rebuilds the list from a container structure: group headers + inner entries (ZIP members and
    /// payload/super partitions), displayed in a single column.
    /// </summary>
    private void ApplyContainerStructure(string containerPath, ContainerStructure structure)
    {
        var entries = new List<NewProjectImageEntry> { NewProjectImageEntry.GroupHeader(GroupLabelFor(structure.Kind, containerPath)) };
        entries.AddRange(structure.Entries.Select(entry => new NewProjectImageEntry(
            entry.Name, entry.Format, containerPath, entry.FromPayload)));
        SetImages([.. entries]);
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
        // 容器分组列表固定单列；普通多选按可用高度/宽度自适应一至三列。
        // Container group lists are single-column; plain multi-select adapts one to three columns.
        var isGrouped = _images.Any(image => image.IsGroupHeader || image.FilePath is null);
        if (isGrouped)
        {
            if (_imageColumnCount == 1) return;
            _imageColumnCount = 1;
            ImageHeaders.ItemsSource = new[] { 0 };
            ImageList.ItemsSource = _images.Select(image => new NewProjectImageRow([image], 1)).ToArray();
            return;
        }

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

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        _validationRequested = true;
        UpdateValidation();
        if (ProjectNameError.IsVisible) ProjectNameBox.Focus();
        else if (ProjectLocationError.IsVisible) ProjectLocationBox.Focus();
        else
        {
            // Output the form result and close; the caller creates the project and runs unpacking.
            // 输出表单结果并关闭；由调用方创建工程并执行解包。
            var selected = _images.Where(image => image.IsSelectable && image.IsSelected).ToArray();
            var containerPath = _images.Length > 0 && _images[0].FilePath is null ? _images[0].ContainerPath : null;
            var kind = containerPath is null
                ? ContainerKind.None
                : ContainerStructureAnalyzer.Analyze(containerPath).Kind;
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
                UnpackFullyBox.IsChecked == true);
            Close();
        }
    }

    private void UpdateValidation()
    {
        if (!_validationRequested) return;
        SetRequiredError(ProjectNameBox, ProjectNameError, "NewProject_NameRequired");
        SetRequiredError(ProjectLocationBox, ProjectLocationError, "NewProject_LocationRequired");
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
    public NewProjectImageEntry(string filePath)
    {
        FilePath = filePath;
        Name = Path.GetFileName(filePath);
        ContainerPath = filePath;
        Format = "—";
    }

    /// <summary><para>容器内部条目（分区名或 ZIP 内路径）。</para>Container inner entry (partition name or ZIP path).</summary>
    public NewProjectImageEntry(string innerName, string format, string containerPath, bool fromPayload)
    {
        FilePath = null;
        Name = innerName;
        ContainerPath = containerPath;
        Format = format;
        FromPayload = fromPayload;
    }

    private NewProjectImageEntry(string label, bool isGroupHeader)
    {
        FilePath = null;
        Name = label;
        ContainerPath = string.Empty;
        Format = string.Empty;
        IsGroupHeader = isGroupHeader;
    }

    /// <summary><para>普通镜像的完整路径；内部条目与分组头为 null。</para>Full path of a plain image; null for inner entries and group headers.</summary>
    public string? FilePath { get; }

    /// <summary><para>显示名称：文件名、分区名、ZIP 内路径或分组标题。</para>Display name: file name, partition name, ZIP path, or group title.</summary>
    public string Name { get; }

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
    public string Tip => FilePath ?? ContainerPath;

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
