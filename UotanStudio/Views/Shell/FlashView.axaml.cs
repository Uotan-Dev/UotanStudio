using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>烧录页面：刷机包选择、分区列表、操作面板与日志输出。</para>
/// Flash page: package selection, partition list, action panel and log output.
/// </summary>
/// <remarks>
/// <para>本页只提供界面与布局，尚未接刷写后端；按钮不执行任何刷写动作。</para>
/// Coordinates package inspection, managed image preparation and selected-device Fastboot operations.
/// </remarks>
public partial class FlashView : UserControl
{
    /// <summary>
    /// <para>页面左右留白，与 <c>FlashLayout</c> 的 Margin 保持一致。</para>
    /// Page inset on both sides; kept in sync with the <c>FlashLayout</c> margin.
    /// </summary>
    private const double PageMargin = 12;
    /// <summary><para>栏位之间的间距。</para>The gap between panes.</summary>
    private const double PaneGap = 12;
    /// <summary>
    /// <para>操作栏固定宽度：按「最宽的两个复选框同行」取值（复选框网格因此保持两列对齐），
    /// 窗口变窄时不再压缩它，只压缩日志和列表。</para>
    /// Fixed action-pane width, sized so the widest check-box pair still fits on one line with the two-column grid;
    /// resizing narrows the log and the list instead of this pane.
    /// </summary>
    private const double ActionPaneWidth = 292;
    private const double MinimumLogWidth = 340;
    private const double MinimumListWidth = 360;
    /// <summary>
    /// <para>宽屏时日志与分区列表的宽度比；中间列表分得更多，日志与列表按此比例先被压缩。</para>
    /// Width ratio between the log and the partition list on wide screens; the middle list gets the larger share and
    /// both shrink proportionally before the fixed action pane does.
    /// </summary>
    private const double WideLogShare = 1;
    private const double WideListShare = 2.5;
    /// <summary>
    /// <para>使用「日志在左」三栏布局所需的最小页面宽度；低于该宽度时日志移到分区列表下方。
    /// 阈值由日志的最小宽度反推，日志最小宽度一变，切换点随之变化，不会出现三栏比窄屏更挤的情况。</para>
    /// Minimum page width for the three-column layout with the log on the left; below it the log moves under the
    /// list. The threshold is derived from the log's minimum width so both stay in step.
    /// </summary>
    private const double WideMinimumWidth =
        ((MinimumLogWidth * (WideLogShare + WideListShare)) / WideLogShare) +
        (PaneGap * 2) + ActionPaneWidth + (PageMargin * 2);
    /// <summary>
    /// <para>窄屏时列表区域与日志区域的高度比例：优先保证列表能显示的行数，日志只占下方较小一段。</para>
    /// Height ratio between the list area and the log area in the stacked layout: the list's row capacity comes first.
    /// </summary>
    private const double StackedListRatio = 2.5;
    private const double StackedLogRatio = 1;
    private const double StackedListMinimumHeight = 600;
    private const double StackedLogMinimumHeight = 400;
    /// <summary>
    /// <para>刷机包一行放不下时的换行阈值：低于该宽度时按钮移到路径框下方，避免路径框被压成窄条。</para>
    /// Wrap threshold for the package bar: below this width the buttons move under the path box so the box keeps
    /// a usable width.
    /// </summary>
    private const double MinimumPackageRowWidth = 430;

    private readonly FlashViewModel _model = new();
    private bool _busy;
    private bool _confirming;
    private string _loadedPath = string.Empty;
    private string? _requiredCodeName;
    private bool _requiresFastbootd;
    private string? _payloadPath;
    private string? _remotePayloadUrl;
    private static readonly HttpClient RemoteHttpClient = new();
    private string? _superPath;
    private string? _vendorPath;
    private readonly Dictionary<string, string> _vendorEntries = new(StringComparer.OrdinalIgnoreCase);
    private string? _oppoPath;
    private readonly Dictionary<string, (string Entry, string OutputName)> _oppoEntries = new(StringComparer.OrdinalIgnoreCase);
    private string? _rootPackagePath;
    private readonly Dictionary<FlashPartitionEntry, RawProgramEntry> _edlEntries = [];
    private string? _edlProgrammer;
    private bool _flatBinPackage;
    private CancellationTokenSource? _flashCancellation;
    private bool? _wide;
    private bool? _packageButtonsStacked;
    private DeviceViewModel? _deviceViewModel;
    private DetectedDevice? SelectedDevice => _deviceViewModel?.SelectedDevice?.Device;

    /// <summary><para>构造页面并应用当前宽度的布局。</para>Creates the page and applies the layout for the current width.</summary>
    public FlashView()
    {
        InitializeComponent();
        DataContext = _model;
        AttachedToVisualTree += (_, _) =>
        {
            _deviceViewModel = DeviceViewModel.Instance;
            if (_deviceViewModel is not null)
                _deviceViewModel.PropertyChanged += OnGlobalDeviceChanged;
            UpdateEdlButton();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_deviceViewModel is not null)
                _deviceViewModel.PropertyChanged -= OnGlobalDeviceChanged;
            _deviceViewModel = null;
        };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();
    }

    /// <summary>
    /// <para>按可用宽度在三栏与上下两种排布之间切换；比例由 <see cref="GridLength"/> 星号分配，不写死像素宽度。</para>
    /// Switches between the three-column and the stacked arrangement based on the available width; the proportions
    /// come from star-sized grid lengths instead of fixed pixel widths.
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var wide = Bounds.Width >= WideMinimumWidth;
        if (_wide == wide)
        {
            return;
        }

        _wide = wide;
        if (wide)
        {
            // 宽屏三栏：日志在左、分区列表居中、操作栏在右；刷机包只占据列表所在的那一栏，
            // 日志与操作栏跨满内容高度，窗口变窄时先按比例压缩日志和列表。
            // 日志在左时关闭整页滚动：三栏内容始终压缩进视口，不出现滚动条。
            // Wide three-column layout: log left, partitions centred, actions right. The package bar only covers
            // the list column; the log and the action pane span the full content height. Page scrolling is off here
            // so the three columns always fit the viewport without a scroll bar.
            PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            FlashLayout.ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(WideLogShare, GridUnitType.Star)) { MinWidth = MinimumLogWidth },
                new ColumnDefinition(new GridLength(PaneGap)),
                new ColumnDefinition(new GridLength(WideListShare, GridUnitType.Star)) { MinWidth = MinimumListWidth },
                new ColumnDefinition(new GridLength(PaneGap)),
                new ColumnDefinition(new GridLength(ActionPaneWidth))
            ];
            FlashLayout.RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                GapRow(),
                new RowDefinition(GridLength.Star),
                new RowDefinition(new GridLength(0)),
                new RowDefinition(new GridLength(0))
            ];
            Place(PackageBar, row: 0, column: 2);
            Place(LogPane, row: 0, column: 0, rowSpan: 3);
            Place(PartitionPane, row: 2, column: 2);
            Place(ActionPane, row: 0, column: 4, rowSpan: 3);
        }
        else
        {
            // 窄屏：日志移到分区列表下方，列表优先占据上方高度（列表:日志 = 2.5:1），
            // 操作栏跨满刷机包与列表两行，顶部对齐刷机包，窗口高度不足时整页滚动。
            // Stacked layout: the log moves under the list and the list keeps 2.5:1 of the height. The action pane
            // spans the package row and the list row so it reaches the top; short windows scroll the page.
            PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            FlashLayout.ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(PaneGap)),
                new ColumnDefinition(new GridLength(ActionPaneWidth)),
                new ColumnDefinition(new GridLength(0)),
                new ColumnDefinition(new GridLength(0))
            ];
            FlashLayout.RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                GapRow(),
                new RowDefinition(new GridLength(StackedListRatio, GridUnitType.Star))
                {
                    MinHeight = StackedListMinimumHeight
                },
                GapRow(),
                new RowDefinition(new GridLength(StackedLogRatio, GridUnitType.Star))
                {
                    MinHeight = StackedLogMinimumHeight
                }
            ];
            Place(PackageBar, row: 0, column: 0);
            Place(PartitionPane, row: 2, column: 0);
            Place(ActionPane, row: 0, column: 2, rowSpan: 3);
            Place(LogPane, row: 4, column: 0, columnSpan: 3);
        }
    }

    /// <summary>
    /// <para>创建固定厚度的间隔行；限制最大高度，避免跨行面板把间隔行撑高。</para>
    /// Creates a fixed-height gap row; the maximum height stops a row-spanned pane from inflating it.
    /// </summary>
    /// <returns><para>间隔行定义。</para>The gap row definition.</returns>
    private static RowDefinition GapRow() => new(new GridLength(PaneGap)) { MaxHeight = PaneGap };

    /// <summary>
    /// <para>把栏位放到网格的指定行列；每次显式重设跨度，避免上一种布局的合并单元格残留。</para>
    /// Places a pane at the given grid position; spans are always rewritten so the previous layout's merges never stick.
    /// </summary>
    /// <param name="control"><para>要摆放的控件。</para>The control to place.</param>
    /// <param name="row"><para>目标行。</para>The target row.</param>
    /// <param name="column"><para>目标列。</para>The target column.</param>
    /// <param name="rowSpan"><para>行跨度。</para>The row span.</param>
    /// <param name="columnSpan"><para>列跨度。</para>The column span.</param>
    private static void Place(Control control, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetRowSpan(control, rowSpan);
        Grid.SetColumnSpan(control, columnSpan);
    }

    /// <summary>
    /// <para>刷机包一行的自适应：宽度不足时把两个按钮换到路径框下方。</para>
    /// Package-bar adaptation: when the row is too narrow the two buttons wrap under the path box.
    /// </summary>
    /// <param name="sender"><para>事件源。</para>The event source.</param>
    /// <param name="e"><para>尺寸变更参数。</para>The size-change arguments.</param>
    private void OnPackageBarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (PackageBarGrid.Bounds.Width <= 0)
        {
            return;
        }

        var stacked = PackageBarGrid.Bounds.Width < MinimumPackageRowWidth;
        if (_packageButtonsStacked == stacked)
        {
            return;
        }

        _packageButtonsStacked = stacked;
        // 在布局回调里直接改行列，会让本行的应得高度停留在旧值；放到下一轮布局再改。
        // Mutating rows inside a layout callback leaves the row's desired height stale, so defer it to the next pass.
        Dispatcher.UIThread.Post(() => ApplyPackageBarLayout(stacked), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// <para>按需要的排布重建刷机包一行；每次显式重设行列与跨度，避免残留上一次的合并单元格。</para>
    /// Rebuilds the package row for the requested arrangement; rows, columns and spans are always rewritten.
    /// </summary>
    /// <param name="stacked"><para>是否使用按钮换行的两行排布。</para>Whether the buttons wrap onto a second row.</param>
    private void ApplyPackageBarLayout(bool stacked)
    {
        if (stacked)
        {
            PackageBarGrid.RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(8)),
                new RowDefinition(GridLength.Auto)
            ];
            PackageBarGrid.ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            ];
            Place(PackageButtonRow, row: 2, column: 0, columnSpan: 2);
            PackageButtonRow.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        }
        else
        {
            PackageBarGrid.RowDefinitions = [new RowDefinition(GridLength.Auto)];
            PackageBarGrid.ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ];
            Place(PackageButtonRow, row: 0, column: 2);
            PackageButtonRow.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        }
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e) => _model.LogText = string.Empty;

    private async void OnChooseFileClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_ChooseFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (!_busy && !_confirming && files.Count == 1 && files[0].TryGetLocalPath() is { } path)
        {
            _model.PackagePath = path;
            await LoadPackageAsync(path);
        }
    }

    private async void OnChooseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_ChooseFolder"),
            AllowMultiple = false
        });
        if (!_busy && !_confirming && folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
        {
            _model.PackagePath = path;
            await LoadPackageAsync(path);
        }
    }

    private async Task LoadPackageAsync(string path)
    {
        if (_busy || _confirming || string.Equals(path, _loadedPath, StringComparison.Ordinal))
        {
            return;
        }

        _busy = true;
        _loadedPath = path;
        _requiredCodeName = null;
        _requiresFastbootd = false;
        _edlEntries.Clear();
        _edlProgrammer = null;
        _flatBinPackage = false;
        FlashEdlBundleButton.IsVisible = false;
        _payloadPath = null;
        _remotePayloadUrl = null;
        _superPath = null;
        _vendorPath = null;
        _vendorEntries.Clear();
        _oppoPath = null;
        _oppoEntries.Clear();
        ExtractSelectedButton.IsEnabled = false;
        BuildWiredPackageButton.IsEnabled = false;
        _model.Partitions.Clear();
        var remotePayload = Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https";
        try
        {
            if (!remotePayload && !Directory.Exists(path) && !File.Exists(path))
            {
                AppendLog(LanguageService.Instance.GetString("Flash_FolderOnly"));
                _loadedPath = string.Empty;
                return;
            }
            if (remotePayload)
            {
                var partitions = await new FlashRemotePayloadService(RemoteHttpClient)
                    .ListAsync(path, CancellationToken.None);
                if (path != _loadedPath) return;
                _remotePayloadUrl = path;
                foreach (var (name, size) in partitions)
                {
                    _model.Partitions.Add(new FlashPartitionEntry
                    {
                        Name = name,
                        Size = $"{size / 1024d / 1024d:F1} MiB",
                        CanSelect = true
                    });
                }
                ExtractSelectedButton.IsEnabled = partitions.Count > 0;
                AppendLog($"{partitions.Count} {LanguageService.Instance.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path) && (path.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
                                      path.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase)))
            {
                if (Path.GetFileName(path).Equals("super_empty.img", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals("super_empty.img.zst", StringComparison.OrdinalIgnoreCase))
                {
                    if (!path.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Compressed super_empty is not supported.");
                    _ = FlashSuperEmptyService.Prepare(path);
                    _model.Partitions.Add(new FlashPartitionEntry
                    {
                        Step = new FlashStep("wipe_super", "super_empty", path, new FileInfo(path).Length),
                        Name = "super_empty",
                        Command = "wipe_super",
                        FilePath = path,
                        FileName = Path.GetFileName(path)
                    });
                    return;
                }
                if (path.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
                    await Task.Run(() => new UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.LpFormatHandler().CanHandle(path)))
                {
                    var partitions = await FlashSuperService.ListAsync(path, CancellationToken.None);
                    _superPath = path;
                    foreach (var (partitionName, size) in partitions)
                    {
                        _model.Partitions.Add(new FlashPartitionEntry
                        {
                            Name = partitionName,
                            Size = $"{size / 1024d / 1024d:F1} MiB",
                            CanSelect = true
                        });
                    }
                    ExtractSelectedButton.IsEnabled = partitions.Count > 0;
                    AppendLog($"{partitions.Count} {LanguageService.Instance.GetString("Flash_ImagesLoaded")}");
                    return;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                if (path.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase))
                {
                    name = Path.GetFileNameWithoutExtension(name);
                }
                var length = new FileInfo(path).Length;
                if (length == 0 || name.Length is < 1 or > 128 ||
                    !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    throw new InvalidDataException("Image name is not a valid partition name.");
                }

                _model.Partitions.Add(new FlashPartitionEntry
                {
                    Name = name,
                    Command = "flash",
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    Size = $"{length / 1024d / 1024d:F1} MiB"
                });
                return;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (File.Exists(path) && extension is ".ofp" or ".ops" or ".ozip")
            {
                var entries = await FlashOppoContainerService.ListAsync(path, CancellationToken.None);
                _oppoPath = path;
                foreach (var (name, archiveEntry, outputName, size) in entries)
                {
                    _oppoEntries.Add(name, (archiveEntry, outputName));
                    _model.Partitions.Add(new FlashPartitionEntry
                    {
                        Name = name,
                        Size = $"{size / 1024d / 1024d:F1} MiB",
                        CanSelect = true
                    });
                }
                ExtractSelectedButton.IsEnabled = entries.Count > 0;
                AppendLog($"{entries.Count} {LanguageService.Instance.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path) && extension is ".nb0" or ".ntpi")
            {
                var entries = await FlashVendorContainerService.ListAsync(path, CancellationToken.None);
                _vendorPath = path;
                foreach (var (name, archiveEntry, size) in entries)
                {
                    _vendorEntries.Add(name, archiveEntry);
                    _model.Partitions.Add(new FlashPartitionEntry
                    {
                        Name = name,
                        Size = $"{size / 1024d / 1024d:F1} MiB",
                        CanSelect = true
                    });
                }
                ExtractSelectedButton.IsEnabled = entries.Count > 0;
                AppendLog($"{entries.Count} {LanguageService.Instance.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path) && extension is not (".txt" or ".bat" or ".sh"))
            {
                var partitions = await FlashPayloadService.ListAsync(path, CancellationToken.None);
                if (path != _loadedPath) return;
                _payloadPath = path;
                foreach (var (name, size) in partitions)
                {
                    _model.Partitions.Add(new FlashPartitionEntry
                    {
                        Name = name,
                        Size = $"{size / 1024d / 1024d:F1} MiB",
                        CanSelect = true
                    });
                }
                ExtractSelectedButton.IsEnabled = partitions.Count > 0;
                BuildWiredPackageButton.IsEnabled = partitions.Count > 0;
                AppendLog($"{partitions.Count} {LanguageService.Instance.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (Directory.Exists(path) &&
                Directory.EnumerateFiles(Directory.Exists(Path.Combine(path, "images"))
                    ? Path.Combine(path, "images") : path, "*.bin", SearchOption.TopDirectoryOnly).Any())
            {
                var bin = FlashBinPackage.Read(path);
                if (bin.Images.Count > 0)
                {
                    _flatBinPackage = true;
                    _edlProgrammer = bin.Programmer;
                    var edlPrograms = bin.Programs.Where(entry =>
                        !string.IsNullOrEmpty(entry.Filename) && File.Exists(entry.SourceFile) &&
                        !entry.Filename.StartsWith("gpt_", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    foreach (var (name, image, length) in bin.Images)
                    {
                        var matches = edlPrograms.Where(entry =>
                            Path.GetFullPath(entry.SourceFile).Equals(Path.GetFullPath(image),
                                StringComparison.OrdinalIgnoreCase)).ToArray();
                        foreach (var entry in matches.Length == 0 ? [null] : matches.Cast<RawProgramEntry?>())
                        {
                            var row = new FlashPartitionEntry
                            {
                                Name = entry?.Label is { Length: > 0 } label ? label : name,
                                Command = "flash",
                                FilePath = image,
                                FileName = Path.GetFileName(image),
                                Size = $"{length / 1024d / 1024d:F1} MiB"
                            };
                            _model.Partitions.Add(row);
                            if (entry is not null) _edlEntries.Add(row, entry);
                        }
                    }
                    UpdateEdlButton();
                    FlashEdlBundleButton.IsVisible = bin.Programs.Count > 0 && bin.Programmer is not null;
                    UpdateEdlButton();
                    AppendLog($"{_model.Partitions.Count} {LanguageService.Instance.GetString("Flash_ImagesLoaded")}");
                    return;
                }
            }
            var manifest = File.Exists(path)
                ? extension switch
                {
                    ".txt" => FlashManifestReader.Read(path),
                    ".bat" or ".sh" => FlashScriptReader.Read(path),
                    _ => null
                }
                : null;
            _requiredCodeName = manifest?.CodeName;
            _requiresFastbootd = manifest?.RequiresFastbootd ?? false;
            var steps = manifest?.Steps ?? FlashPackageReader.Read(path)
                .Select(image => new FlashStep(image.Partition.Equals("super_empty", StringComparison.OrdinalIgnoreCase)
                    ? "wipe_super" : "flash",
                    image.Partition, image.Path, image.Length)).ToArray();
            foreach (var step in steps)
            {
                _model.Partitions.Add(new FlashPartitionEntry
                {
                    Step = step,
                    Name = step.Partition.Length > 0 ? step.Partition : step.Command,
                    Command = step.Command,
                    Size = step.ImageLength > 0 ? $"{step.ImageLength / 1024d / 1024d:F1} MiB" : string.Empty,
                    FileName = Path.GetFileName(step.ImagePath),
                    FilePath = step.ImagePath
                });
            }

            AppendLog($"{_model.Partitions.Count} {LanguageService.Instance.GetString("Flash_ImagesLoaded")}");
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            _loadedPath = string.Empty;
            _flatBinPackage = false;
            _edlEntries.Clear();
            _edlProgrammer = null;
            FlashEdlBundleButton.IsVisible = false;
            _model.Partitions.Clear();
            UpdateEdlButton();
        }
        finally
        {
            _busy = false;
            UpdateEdlButton();
        }
    }

    private async void OnPackagePathLostFocus(object? sender, RoutedEventArgs e)
        => await LoadPackageAsync(_model.PackagePath);

    private async void OnExtractSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _payloadPath is null && _remotePayloadUrl is null &&
            _superPath is null && _vendorPath is null && _oppoPath is null ||
            TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var selected = _model.Partitions.Where(entry => entry.IsSelected).ToArray();
        if (selected.Length == 0) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_ExtractSelected"),
            AllowMultiple = false
        });
        if (_busy || _confirming || !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal) ||
            selected.Any(entry => !entry.IsSelected || !_model.Partitions.Contains(entry)) ||
            folders.Count != 1 || folders[0].TryGetLocalPath() is not { } parent) return;
        _busy = true;
        ExtractSelectedButton.IsEnabled = false;
        try
        {
            var names = selected.Select(entry => entry.Name).ToArray();
            var output = _payloadPath is { } payload
                ? await FlashPayloadService.ExtractAsync(payload, names, parent, CancellationToken.None)
                : _remotePayloadUrl is { } remote
                    ? await new FlashRemotePayloadService(RemoteHttpClient)
                        .ExtractAsync(remote, names, parent, CancellationToken.None)
                : _superPath is { } super
                    ? await FlashSuperService.ExtractAsync(super, names, parent, CancellationToken.None)
                    : _vendorPath is { } vendor
                        ? await FlashVendorContainerService.ExtractAsync(vendor,
                            names.Select(name => _vendorEntries[name]).ToArray(), parent, CancellationToken.None)
                        : await FlashOppoContainerService.ExtractAsync(_oppoPath!,
                            names.Select(name => _oppoEntries[name].Entry).ToArray(), parent, CancellationToken.None);
            foreach (var entry in selected)
            {
                var filename = _vendorPath is not null ? _vendorEntries[entry.Name]
                    : _oppoPath is not null ? _oppoEntries[entry.Name].OutputName
                    : entry.Name + ".img";
                var path = Path.Combine(output, filename);
                entry.FilePath = path;
                entry.FileName = Path.GetFileName(path);
                entry.Size = $"{new FileInfo(path).Length / 1024d / 1024d:F1} MiB";
            }
            AppendLog(output);
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _busy = false;
            ExtractSelectedButton.IsEnabled = true;
        }
    }

    private async void OnBuildWiredPackageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _payloadPath is not { } payload || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_BuildWiredPackage"),
            AllowMultiple = false
        });
        if (_busy || _confirming || !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal) ||
            _payloadPath != payload || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } parent) return;
        _busy = true;
        BuildWiredPackageButton.IsEnabled = false;
        try
        {
            var output = await FlashPayloadService.BuildPackageAsync(payload,
                _model.Partitions.Select(entry => entry.Name).ToArray(), parent, CancellationToken.None);
            AppendLog(output);
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _busy = false;
            BuildWiredPackageButton.IsEnabled = _payloadPath is not null;
        }
    }

    private async void OnChooseRootPackageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_ChooseRootPackage"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        _busy = true;
        AddRootBox.IsChecked = false;
        AddRootBox.IsEnabled = false;
        _rootPackagePath = null;
        try
        {
            var detection = await new UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.BootPatchService()
                .DetectAsync(path);
            if (!FlashRootImage.IsSupportedPackage(detection))
            {
                throw new InvalidDataException("The selected file is not a supported Magisk or KernelSU package.");
            }
            _rootPackagePath = path;
            AddRootBox.IsEnabled = true;
            ToolTip.SetTip(ChooseRootPackageButton, path);
            AppendLog(Path.GetFileName(path));
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally { _busy = false; }
    }

    private async void OnChoosePartitionImageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || sender is not Button { DataContext: FlashPartitionEntry entry } ||
            entry.Step?.Command is not (null or "flash" or "wipe_super") ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_ChooseFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || !_model.Partitions.Contains(entry) ||
            files.Count != 1 || files[0].TryGetLocalPath() is not { } path || !File.Exists(path))
        {
            return;
        }

        entry.FilePath = path;
        entry.FileName = Path.GetFileName(path);
        entry.Size = $"{new FileInfo(path).Length / 1024d / 1024d:F1} MiB";
        entry.CanSelect = true;
    }

    private void OnGlobalDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModel.SelectedDevice))
            UpdateEdlButton();
    }

    private void UpdateEdlButton() => FlashEdlBundleButton.IsEnabled =
        !_busy && FlashEdlBundleButton.IsVisible &&
        SelectedDevice is { Protocol: DeviceProtocolKind.Edl };

    private void AppendLog(string message) => _model.LogText += message + Environment.NewLine;

    private async void OnReadInfoClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Fastboot } device || _busy || _confirming) return;
        _busy = true;
        try
        {
            var partitions = await FastbootPartitionReader.ReadAsync(device,
                ShowAllPartitionsBox.IsChecked == true, CancellationToken.None);
            _model.Partitions.Clear();
            _loadedPath = string.Empty;
            _requiredCodeName = null;
            _requiresFastbootd = false;
            _payloadPath = null;
            _remotePayloadUrl = null;
            _superPath = null;
            _vendorPath = null;
            _vendorEntries.Clear();
            _oppoPath = null;
            _oppoEntries.Clear();
            ExtractSelectedButton.IsEnabled = false;
            BuildWiredPackageButton.IsEnabled = false;
            _model.PackagePath = string.Empty;
            foreach (var (name, size) in partitions)
            {
                _model.Partitions.Add(new FlashPartitionEntry
                {
                    Name = name,
                    Size = $"{size / 1024d / 1024d:F1} MiB",
                    CanSelect = true
                });
            }
            AppendLog($"{partitions.Count} {LanguageService.Instance.GetString("Flash_PartitionsLoaded")}");
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally { _busy = false; }
    }

    private async void OnFlashSelectedClick(object? sender, RoutedEventArgs e)
        => await RunSelectedAsync(erase: false);

    private async void OnFlashEdlBundleClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _edlProgrammer is null || !_flatBinPackage ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not Window owner ||
            !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal)) return;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner,
                LanguageService.Instance.GetString("Flash_FlashEdlBundle"), device.DisplayName);
        }
        finally { _confirming = false; }
        if (!confirmed || SelectedDevice is not DetectedDevice current ||
            current.Key != device.Key || _model.PackagePath != _loadedPath ||
            App.DeviceMonitor.Scan().All(found =>
                found.Key != device.Key || found.Protocol != DeviceProtocolKind.Edl)) return;
        _busy = true;
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        FlashSelectedButton.IsEnabled = false;
        FlashEdlBundleButton.IsEnabled = false;
        PackagePathBox.IsEnabled = false;
        try
        {
            await App.DeviceMonitor.FlashEdlBundleAsync(device, _loadedPath, _edlProgrammer, cancellation.Token);
            AppendLog(LanguageService.Instance.GetString("Flash_FlashEdlBundle") + ": OK");
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            _busy = false;
            CancelFlashButton.IsEnabled = false;
            FlashSelectedButton.IsEnabled = true;
            UpdateEdlButton();
            PackagePathBox.IsEnabled = true;
        }
    }

    private async void OnEraseSelectedClick(object? sender, RoutedEventArgs e)
        => await RunSelectedAsync(erase: true);

    private async void OnSwitchSlotClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Fastboot } device ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        _confirming = true;
        bool confirmed;
        try { confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_SwitchSlot"), device.DisplayName); }
        finally { _confirming = false; }
        if (!confirmed) return;
        if (SelectedDevice is not DetectedDevice current || current.Key != device.Key ||
            App.DeviceMonitor.Scan().All(found => found.Key != device.Key ||
                found.Protocol != DeviceProtocolKind.Fastboot))
        {
            AppendLog(language.GetString("Flash_DeviceChanged"));
            return;
        }
        _busy = true;
        SwitchSlotButton.IsEnabled = false;
        try
        {
            var slot = await FastbootSlotService.SetOtherAsync(device, CancellationToken.None);
            AppendLog($"{language.GetString("Flash_ActiveSlot")}: {slot}");
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _busy = false;
            SwitchSlotButton.IsEnabled = true;
        }
    }

    private void OnCancelFlashClick(object? sender, RoutedEventArgs e) => _flashCancellation?.Cancel();

    private async Task RunSelectedAsync(bool erase)
    {
        if (_busy || _confirming || SelectedDevice is not DetectedDevice device) return;
        if (!string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_PackageChanged"));
            return;
        }
        var selected = _model.Partitions.Where(entry => entry.IsSelected && entry.CanSelect &&
            (!erase || entry.Name != "reboot" && entry.Step?.Command is not ("set_active" or "reboot" or "wipe_super"))).ToArray();
        if (selected.Length == 0) return;
        if (!FlashModeAllowed(device.Protocol, _flatBinPackage, _edlEntries.Count > 0, _edlProgrammer is not null) ||
            device.Protocol == DeviceProtocolKind.Adb && !await FlashSuService.HasSuAsync(device, CancellationToken.None))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_ModeMismatch"));
            return;
        }
        if (device.Protocol != DeviceProtocolKind.Fastboot &&
            (erase || DeleteCowBox.IsChecked == true || EraseDataBox.IsChecked == true ||
             RebootAfterBox.IsChecked == true || DisableVbmetaBox.IsChecked == true || AddRootBox.IsChecked == true ||
             device.Protocol == DeviceProtocolKind.Edl && selected.Any(row => !_edlEntries.ContainsKey(row))))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_ModeMismatch"));
            return;
        }
        var canExtract = _payloadPath is not null || _remotePayloadUrl is not null ||
            _superPath is not null || _vendorPath is not null || _oppoPath is not null;
        if (!erase && selected.Any(entry => (entry.Step?.Command ?? "flash") is "flash" or "wipe_super" &&
            !File.Exists(entry.FilePath) && (!canExtract || entry.FilePath.Length != 0)))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_FileMissing"));
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        var deleteCow = !erase && DeleteCowBox.IsChecked == true;
        var eraseData = !erase && EraseDataBox.IsChecked == true;
        var rebootAfter = !erase && RebootAfterBox.IsChecked == true;
        var disableVbmeta = !erase && DisableVbmetaBox.IsChecked == true;
        var addRoot = !erase && AddRootBox.IsChecked == true;
        if (disableVbmeta && !selected.Any(entry => (entry.Step?.Command ?? "flash") == "flash" &&
            entry.Name.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog(language.GetString("Flash_VbmetaMissing"));
            return;
        }
        var bootEntries = selected.Where(entry => (entry.Step?.Command ?? "flash") == "flash" &&
            entry.Name is "boot" or "boot_a" or "boot_b" or "init_boot" or "init_boot_a" or "init_boot_b").ToArray();
        if (addRoot && (_rootPackagePath is not { } || bootEntries.Length != 1))
        {
            AppendLog(language.GetString("Flash_RootBootMissing"));
            return;
        }
        var rootPackage = _rootPackagePath;
        if (selected.Any(entry => entry.Step?.Command == "reboot") && (eraseData || rebootAfter))
        {
            AppendLog(language.GetString("Flash_RebootConflict"));
            return;
        }
        var description = $"{device.DisplayName}: {string.Join(", ", selected.Select(entry => entry.Name))}";
        if (deleteCow) description += Environment.NewLine + language.GetString("Flash_DeleteCow");
        if (eraseData) description += Environment.NewLine + language.GetString("Flash_EraseData");
        if (rebootAfter) description += Environment.NewLine + language.GetString("Flash_RebootAfter");
        if (disableVbmeta) description += Environment.NewLine + language.GetString("Flash_DisableVbmeta");
        if (addRoot) description += Environment.NewLine + language.GetString("Flash_AddRoot") +
            ": " + Path.GetFileName(rootPackage);
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner,
                language.GetString(erase ? "Flash_EraseSelected" : "Flash_FlashSelected"), description);
        }
        finally { _confirming = false; }
        if (!confirmed || selected.Any(entry => !entry.IsSelected || !_model.Partitions.Contains(entry))) return;
        if (!string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal))
        {
            AppendLog(language.GetString("Flash_PackageChanged"));
            return;
        }
        if (SelectedDevice is not DetectedDevice current || current.Key != device.Key ||
            App.DeviceMonitor.Scan().All(found => found.Key != device.Key || found.Protocol != device.Protocol))
        {
            AppendLog(language.GetString("Flash_DeviceChanged"));
            return;
        }

        _busy = true;
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        FlashSelectedButton.IsEnabled = false;
        EraseSelectedButton.IsEnabled = false;
        SwitchSlotButton.IsEnabled = false;
        CancelFlashButton.IsEnabled = true;
        PackagePathBox.IsEnabled = false;
        ChooseFileButton.IsEnabled = false;
        ChooseFolderButton.IsEnabled = false;
        PartitionGrid.IsEnabled = false;
        var staged = new List<string>();
        string? extractionRoot = null;
        try
        {
            if (device.Protocol is DeviceProtocolKind.Edl or DeviceProtocolKind.Adb)
            {
                if (device.Protocol == DeviceProtocolKind.Adb &&
                    !await FlashSuService.HasSuAsync(device, cancellation.Token))
                {
                    AppendLog(language.GetString("Flash_ModeMismatch"));
                    return;
                }
                if (device.Protocol == DeviceProtocolKind.Edl)
                {
                    await App.DeviceMonitor.RunEdlAsync(device, async edl =>
                    {
                        foreach (var row in selected)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            if (!File.Exists(row.FilePath) ||
                                !Path.GetFullPath(_edlEntries[row].SourceFile).Equals(
                                    Path.GetFullPath(row.FilePath), StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("EDL image no longer matches its rawprogram entry.");
                            var result = await FlashService.Default.FlashEdlPartitionAsync(
                                edl, _edlEntries[row], Path.GetDirectoryName(row.FilePath)!,
                                _edlProgrammer, null, cancellation.Token);
                            AppendLog(result.Summary);
                            if (!result.Success) break;
                        }
                        return true;
                    }, cancellation.Token);
                }
                else
                {
                    foreach (var row in selected)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        var result = await FlashSuService.FlashAsync(device, row.Name, row.FilePath, cancellation.Token);
                        AppendLog(result);
                    }
                }
                return;
            }
            var target = new DeviceInfo(device);
            var progress = new Progress<FlashProgress>(update =>
            {
                if (update.Percent is 0 or 100)
                {
                    AppendLog($"{update.Partition}: {update.Status}");
                }
            });
            if (_requiredCodeName is { } required)
            {
                var product = await FlashService.Default.GetDevicePropertyAsync(target, "product", cancellation.Token);
                if (!product.Success ||
                    !string.Equals(product.Summary, $"product={required}", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(language.GetString("Flash_ProductMismatch"));
                    return;
                }
            }
            if (_requiresFastbootd)
            {
                var userspace = await FlashService.Default.GetDevicePropertyAsync(target, "is-userspace", cancellation.Token);
                if (!userspace.Success || !userspace.Summary.Equals("is-userspace=yes", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(language.GetString("Flash_RequiresFastbootd"));
                    return;
                }
            }
            var prepared = new Dictionary<FlashPartitionEntry, string>();
            var preparedSuper = new Dictionary<FlashPartitionEntry, PreparedSuperEmpty>();
            if (!erase)
            {
                foreach (var entry in selected.Where(entry => entry.Step?.Command == "wipe_super"))
                    preparedSuper[entry] = FlashSuperEmptyService.Prepare(entry.FilePath);
                var missingImages = selected.Where(entry => (entry.Step?.Command ?? "flash") == "flash" &&
                    entry.FilePath.Length == 0).ToArray();
                if (missingImages.Length > 0)
                {
                    extractionRoot = Path.Combine(Path.GetTempPath(), "uotan-flash-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(extractionRoot);
                    var names = missingImages.Select(entry => entry.Name).ToArray();
                    var output = _payloadPath is { } payload
                        ? await FlashPayloadService.ExtractAsync(payload, names, extractionRoot, cancellation.Token)
                        : _remotePayloadUrl is { } remote
                            ? await new FlashRemotePayloadService(RemoteHttpClient)
                                .ExtractAsync(remote, names, extractionRoot, cancellation.Token)
                        : _superPath is { } super
                            ? await FlashSuperService.ExtractAsync(super, names, extractionRoot, cancellation.Token)
                        : _vendorPath is { } vendor
                            ? await FlashVendorContainerService.ExtractAsync(vendor,
                                names.Select(name => _vendorEntries[name]).ToArray(), extractionRoot, cancellation.Token)
                        : await FlashOppoContainerService.ExtractAsync(_oppoPath!,
                            names.Select(name => _oppoEntries[name].Entry).ToArray(), extractionRoot, cancellation.Token);
                    foreach (var entry in missingImages)
                    {
                        var filename = _vendorPath is not null ? _vendorEntries[entry.Name]
                            : _oppoPath is not null ? _oppoEntries[entry.Name].OutputName
                            : entry.Name + ".img";
                        var image = Path.Combine(output, filename);
                        if (!File.Exists(image)) throw new InvalidDataException($"Missing extracted image: {filename}");
                        prepared[entry] = image;
                    }
                }
                foreach (var entry in selected.Where(entry => (entry.Step?.Command ?? "flash") == "flash" &&
                    prepared.GetValueOrDefault(entry, entry.FilePath).EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    AppendLog($"{entry.FileName}: {language.GetString("Flash_Decompressing")}");
                    var path = await FlashCompressedImage.PrepareAsync(
                        prepared.GetValueOrDefault(entry, entry.FilePath), cancellation.Token);
                    staged.Add(path);
                    prepared[entry] = path;
                }
                if (disableVbmeta)
                {
                    foreach (var entry in selected.Where(entry => (entry.Step?.Command ?? "flash") == "flash" &&
                        entry.Name.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase)))
                    {
                        var source = prepared.GetValueOrDefault(entry, entry.FilePath);
                        var output = await FlashVbmetaImage.PrepareAsync(source, cancellation.Token);
                        staged.Add(output);
                        prepared[entry] = output;
                    }
                }
                if (addRoot)
                {
                    var entry = bootEntries[0];
                    var output = await FlashRootImage.PrepareAsync(rootPackage!,
                        prepared.GetValueOrDefault(entry, entry.FilePath), cancellation.Token);
                    staged.Add(output);
                    prepared[entry] = output;
                }
            }
            if (deleteCow)
            {
                var count = await FastbootStepService.DeleteCowAsync(device, cancellation.Token);
                AppendLog($"{language.GetString("Flash_DeleteCow")}: {count}");
            }
            var succeeded = true;
            foreach (var entry in selected)
            {
                if (cancellation.IsCancellationRequested)
                {
                    AppendLog(language.GetString("Flash_Canceled"));
                    succeeded = false;
                    break;
                }
                var command = erase ? "erase" : entry.Step?.Command ?? "flash";
                if (command is "flash" or "wipe_super" &&
                    !File.Exists(prepared.GetValueOrDefault(entry, entry.FilePath)))
                {
                    AppendLog($"{entry.FileName}: {language.GetString("Flash_FileMissing")}");
                    succeeded = false;
                    break;
                }

                AppendLog($"{command} {entry.Name}: {entry.FileName}");
                if (command == "wipe_super")
                {
                    await FlashSuperEmptyService.WipeAsync(device, preparedSuper[entry], cancellation.Token);
                    AppendLog($"{command}: OK");
                }
                else if (command is "flash" or "erase")
                {
                    var result = command == "erase"
                        ? await FlashService.Default.ErasePartitionAsync(target, entry.Name, progress, cancellation.Token)
                        : await FlashService.Default.FlashImageAsync(target, entry.Name,
                            prepared.GetValueOrDefault(entry, entry.FilePath), progress, cancellation.Token);
                    AppendLog(result.Summary);
                    if (!result.Success)
                    {
                        succeeded = false;
                        break;
                    }
                }
                else
                {
                    await FastbootStepService.ExecuteAsync(device, entry.Step!, cancellation.Token);
                    AppendLog($"{command}: OK");
                }
            }
            if (succeeded && !cancellation.IsCancellationRequested && eraseData)
            {
                foreach (var partition in new[] { "metadata", "userdata" })
                {
                    var result = await FlashService.Default.ErasePartitionAsync(target, partition, progress, cancellation.Token);
                    AppendLog(result.Summary);
                    if (!result.Success)
                    {
                        succeeded = false;
                        break;
                    }
                }
            }
            if (succeeded && !cancellation.IsCancellationRequested && rebootAfter)
            {
                await FastbootStepService.ExecuteAsync(device, new FlashStep("reboot", ""), cancellation.Token);
                AppendLog($"{language.GetString("Flash_RebootAfter")}: OK");
            }
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            foreach (var path in staged)
            {
                try { File.Delete(path); } catch (IOException) { }
            }
            if (extractionRoot is not null)
            {
                try { Directory.Delete(extractionRoot, recursive: true); } catch (IOException) { }
            }
            _busy = false;
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            FlashSelectedButton.IsEnabled = true;
            EraseSelectedButton.IsEnabled = true;
            SwitchSlotButton.IsEnabled = true;
            PackagePathBox.IsEnabled = true;
            ChooseFileButton.IsEnabled = true;
            ChooseFolderButton.IsEnabled = true;
            PartitionGrid.IsEnabled = true;
        }
    }

    internal static bool FlashModeAllowed(DeviceProtocolKind mode, bool flatBin, bool hasEdlEntries, bool hasProgrammer)
        => mode == DeviceProtocolKind.Fastboot ||
            flatBin && (mode == DeviceProtocolKind.Adb ||
                        mode == DeviceProtocolKind.Edl && hasEdlEntries && hasProgrammer);
}