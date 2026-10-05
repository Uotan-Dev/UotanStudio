using System.ComponentModel;
using System.IO.Ports;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Auth;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
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
/// <para>本页协调镜像归属与所选设备的刷写事务：EDL/Fastboot/RKDL 走 <c>FlashService</c>，
/// ADB 走 <c>FlashSuService</c>，MTK  scatter 走 <c>MtkFlashService</c>，擦除与属性读取同样由这些服务持有。</para>
/// Coordinates package inspection, managed image preparation and selected-device Fastboot, EDL, RKDL,
/// MTK and ADB-su flashing transactions held by <c>FlashService</c>, <c>FlashSuService</c> and
/// <c>MtkFlashService</c>.
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
    /// <para>写入日志的分区表最大行数；超出部分只给出余量提示，避免一张几百分区的表淹没日志。</para>
    /// Maximum partition-table lines written to the log; anything beyond it is summarised so a
    /// several-hundred-partition table never floods the log.
    /// </summary>
    private const int MaxPartitionTableLines = 64;
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
    private byte[]? _edlXiaomiAuthData;


    private const int EdlAuthKindNone = 0;
    private const int EdlAuthKindDemacia = 1;
    private const int EdlAuthKindOnePlusProject = 2;
    private const int EdlAuthKindNothingProject = 3;
    private const int EdlAuthKindXiaomi = 4;
    private readonly List<string> _edlOnePlusModelIds = [];
    private IReadOnlyList<string> _edlProgrammerCandidates = [];
    private string? _edlProgrammerOverride;
    private UfsProvisionProfile? _edlUfsProfile;
    private bool _flatBinPackage;
    private bool _edlBundleReady;
    private string? _edlCapabilityDevice;
    private CancellationTokenSource? _flashCancellation;
    private IReadOnlyList<FlashPartitionEntry> _overallRows = [];
    private MtkScatterPackageResolution? _mtkResolution;
    private readonly Dictionary<FlashPartitionEntry, int> _mtkRows = [];
    /// <summary>
    /// <para>scatter 行到其清单条目的映射，既含可刷写行也含仅可擦除行；擦除按钮据此直接构造 GPT 命名步骤。</para>
    /// Maps a scatter row to its manifest entry, covering both flashable rows and erase-only rows;
    /// the erase button builds GPT-named steps straight from it.
    /// </summary>
    private readonly Dictionary<FlashPartitionEntry, MtkScatterPartition> _mtkPartitions = [];
    private string? _mtkLoaderPath;
    private IReadOnlyList<MtkLoaderEntryInfo> _mtkLoaderEntries = [];
    private MtkEmiParameters? _mtkEmi;
    private string? _mtkKeyDirectory;
    private string? _mtkAuthFile;
    private bool? _wide;
    private bool? _packageButtonsStacked;
    private DeviceViewModel? _deviceViewModel;
    private DetectedDevice? SelectedDevice => _deviceViewModel?.SelectedDevice?.Device;

    /// <summary>
    /// <para>取界面选择的 EDL 物理区域（LUN）；未选择时使用主区域 0。</para>
    /// The EDL physical region (LUN) selected on the page; the primary region 0 when none is picked.
    /// </summary>
    private int SelectedEdlLun => EdlLunBox.SelectedItem is int lun ? lun : 0;

    /// <summary><para>构造页面并应用当前宽度的布局。</para>Creates the page and applies the layout for the current width.</summary>
    public FlashView()
    {
        InitializeComponent();
        DataContext = _model;
        MtkStorageKindBox.ItemsSource = new[] { "eMMC", "UFS", "NOR", "NAND", "Auto" };
        MtkStorageKindBox.SelectedIndex = 0;
        MtkCrashModeBox.ItemsSource = Enum.GetNames<MtkDaCrashMode>();
        MtkCrashModeBox.SelectedIndex = 0;
        MtkExploitFamilyBox.SelectionChanged += (_, _) => UpdateEdlButton();
        MtkBaudBox.ItemsSource = new[] { 115200, 921600, 460800, 230400 };
        MtkBaudBox.SelectedIndex = 0;
        EdlSerialBaudBox.ItemsSource = new[] { 115200, 921600, 460800, 230400 };
        EdlSerialBaudBox.SelectedIndex = 0;
        EdlLunBox.ItemsSource = Enumerable.Range(0, 8).ToArray();
        EdlLunBox.SelectedIndex = 0;
        var initializers = LanguageService.Instance;
        MtkExploitFamilyBox.ItemsSource = new[]
        {
            initializers.GetString("Flash_MtkExploitAutoFamily"),
            initializers.GetString("Flash_MtkFamilyKamakiri"),
            initializers.GetString("Flash_MtkFamilyKamakiri2"),
            initializers.GetString("Flash_MtkFamilyKamakiriPreloader"),
            initializers.GetString("Flash_MtkFamilyAmonet"),
            initializers.GetString("Flash_MtkFamilyHashimoto"),
        };
        var payloadNames = Enum.GetNames<MtkExploitPayloadSource>();
        MtkExploitPayloadBox.ItemsSource = new[]
        {
            initializers.GetString("Flash_MtkPayloadStageOnePatcher"),
            initializers.GetString("Flash_MtkPayloadStageOneGeneric"),
            initializers.GetString("Flash_MtkPayloadGenericLoader"),
            initializers.GetString("Flash_MtkPayloadBromDump"),
            initializers.GetString("Flash_MtkPayloadSramDump"),
            initializers.GetString("Flash_MtkPayloadUartDump"),
            initializers.GetString("Flash_MtkPayloadPreloaderDump"),
            initializers.GetString("Flash_MtkPayloadReboot"),
            initializers.GetString("Flash_MtkPayloadChipProfile"),
        };
        MtkExploitPayloadBox.SelectedIndex = Array.IndexOf(payloadNames,
            nameof(MtkExploitPayloadSource.ChipProfilePayload));
        MtkRpmbModeBox.ItemsSource = new[]
        {
            initializers.GetString("Flash_MtkRpmbModeFrames"),
            initializers.GetString("Flash_MtkRpmbModeByteRange"),
            initializers.GetString("Flash_MtkRpmbModeFullArea"),
        };
        MtkRpmbModeBox.SelectedIndex = 0;
        MtkRpmbOffsetBox.Text = "0";
        MtkRpmbLengthBox.Text = "8";
        MtkSecCfgLockBox.ItemsSource = new[]
        {
            initializers.GetString("Flash_MtkSecCfgIntentLock"),
            initializers.GetString("Flash_MtkSecCfgIntentUnlock"),
        };
        MtkOemKeyKindBox.ItemsSource = new[]
        {
            initializers.GetString("Flash_MtkOemKeySoftwareOtp"),
            initializers.GetString("Flash_MtkOemKeyRpmb"),
        };
        EdlAuthKindBox.ItemsSource = new[]
        {
            initializers.GetString("Flash_EdlAuthNone"),
            initializers.GetString("Flash_EdlDemaciaAuth"),
            initializers.GetString("Flash_EdlProjectAuth"),
            initializers.GetString("Flash_EdlNothingAuth"),
            initializers.GetString("Flash_EdlXiaomiAuth"),
        };
        EdlAuthKindBox.SelectedIndex = EdlAuthKindNone;
        _edlOnePlusModelIds.AddRange(EdlOnePlusProjectCatalog.ProjectIds);
        EdlOnePlusModelBox.ItemsSource = _edlOnePlusModelIds
            .Select(identifier => string.Format(
                initializers.GetString("Flash_EdlOnePlusModelFormat"),
                EdlOnePlusProjectCatalog.Describe(identifier),
                identifier))
            .ToArray();
        EdlGeneralizedXmlBox.IsEnabled = false;
        EdlGenerateXmlBox.IsCheckedChanged += (_, _) =>
            EdlGeneralizedXmlBox.IsEnabled = EdlGenerateXmlBox.IsChecked == true;
        _model.PropertyChanged += OnModelPropertyChanged;
        RefreshMtkPorts();
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

        SetBusy(true);
        _loadedPath = path;
        _requiredCodeName = null;
        _requiresFastbootd = false;
        _edlEntries.Clear();
        ResetEdlProgrammer();
        _edlProgrammerOverride = null;
        _flatBinPackage = false;
        _edlBundleReady = false;
        _mtkResolution = null;
        _mtkRows.Clear();
        _mtkPartitions.Clear();
        MtkPane.IsVisible = false;
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
                        throw new InvalidDataException(
                            LanguageService.Instance.GetString("Flash_CompressedSuperEmptyUnsupported"));
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
                    await Task.Run(() =>
                    {
                        UotanStudio.Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
                        return UotanStudio.Firmware.Core.FormatDetector.Detect(path) is UotanStudio.Firmware.BlockImage.Lp.LpFormatHandler;
                    }))
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
                    throw new InvalidDataException(
                        LanguageService.Instance.GetString("Flash_ImageNameInvalid"));
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

            if (File.Exists(path) && extension == ".xml" && LooksLikeScatter(path))
            {
                var resolution = MtkScatterPackage.Resolve(path, MtkScatterPackage.Read(path));
                _mtkResolution = resolution;
                var knownNames = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < resolution.Rows.Count; index++)
                {
                    var (partition, imagePath, length) = resolution.Rows[index];
                    var row = new FlashPartitionEntry
                    {
                        Name = partition.Name,
                        Command = "flash",
                        FilePath = imagePath,
                        FileName = Path.GetFileName(imagePath),
                        Size = $"{length / 1024d / 1024d:F1} MiB"
                    };
                    _model.Partitions.Add(row);
                    _mtkRows[row] = index;
                    _mtkPartitions[row] = partition;
                    knownNames.Add(partition.Name);
                }

                var eraseOnlyRows = 0;
                foreach (var partition in MtkScatterPackage.ListEraseTargets(resolution.Storage))
                {
                    if (!knownNames.Add(partition.Name))
                    {
                        continue;
                    }

                    _model.Partitions.Add(new FlashPartitionEntry
                    {
                        Name = partition.Name,
                        Command = "erase",
                        CanSelect = true,
                        Size = $"{partition.Size / 1024d / 1024d:F1} MiB"
                    });
                    eraseOnlyRows++;
                    _mtkPartitions[_model.Partitions[^1]] = partition;
                }

                MtkPane.IsVisible = true;
                AppendLog($"{resolution.Rows.Count + eraseOnlyRows} {LanguageService.Instance.GetString("Flash_ImagesLoaded")} · {resolution.StorageLabel}");
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
                    _edlProgrammerCandidates = bin.Programmers;
                    EdlProgrammerPickBox.ItemsSource = bin.Programmers
                        .Select(Path.GetFileName)
                        .ToArray();
                    EdlProgrammerPickBox.SelectedIndex = -1;
                    EdlProgrammerPickBox.IsVisible = EdlProgrammerChoiceLabel.IsVisible = bin.Programmers.Count > 1;
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
                    _edlBundleReady = bin.Programs.Count > 0 && bin.Programmers.Count > 0;
                    if (bin.Programmers.Count > 1)
                    {
                        AppendLog($"{LanguageService.Instance.GetString("Flash_EdlProgrammerChoice")}: {bin.Programmers.Count}");
                    }
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
            _edlBundleReady = false;
            _edlEntries.Clear();
            ResetEdlProgrammer();
            _edlProgrammerOverride = null;
            _mtkResolution = null;
            _mtkRows.Clear();
            _mtkPartitions.Clear();
            MtkPane.IsVisible = false;
            _model.Partitions.Clear();
            UpdateEdlButton();
        }
        finally
        {
            SetBusy(false);
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
        SetBusy(true);
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
            SetBusy(false);
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
        SetBusy(true);
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
            SetBusy(false);
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
        SetBusy(true);
        AddRootBox.IsChecked = false;
        AddRootBox.IsEnabled = false;
        _rootPackagePath = null;
        try
        {
            var detection = await new UotanStudio.Firmware.BootSecurity.Patcher.BootPatchService()
                .DetectAsync(path);
            if (!FlashRootImage.IsSupportedPackage(detection))
            {
                throw new InvalidDataException(
                    LanguageService.Instance.GetString("Flash_RootPackageUnsupported"));
            }
            _rootPackagePath = path;
            AddRootBox.IsEnabled = true;
            ToolTip.SetTip(ChooseRootPackageButton, path);
            AppendLog(Path.GetFileName(path));
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally { SetBusy(false); }
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

    private void UpdateEdlButton()
    {
        var busy = _busy;
        var protocol = SelectedDevice?.Protocol;
        UpdateModeVisibility();
        FlashEdlBundleButton.IsEnabled =
            !busy && FlashEdlBundleButton.IsVisible && protocol is DeviceProtocolKind.Edl &&
            (_edlProgrammer is not null || _edlProgrammerOverride is not null);
        EdlSerialFlashButton.IsEnabled = !busy && _edlBundleReady;
        MtkFlashButton.IsEnabled = !busy && _mtkResolution is not null;
        VerifyAfterFlashBox.IsEnabled = !busy &&
            (protocol is DeviceProtocolKind.Edl or DeviceProtocolKind.Rkdl || _mtkResolution is not null);
        _model.SelectedDeviceText = SelectedDevice is { } device
            ? device.Protocol is DeviceProtocolKind.Mtk && device.State == "logging"
                ? $"{device.ProtocolLabel} · {device.DisplayName} — {LanguageService.Instance.GetString("Flash_MtkLoggingOnlyHint")}"
                : $"{device.ProtocolLabel} · {device.DisplayName}"
            : LanguageService.Instance.GetString("Flash_NoDevice");
    }

    /// <summary>
    /// <para>清空包内 programmer 候选与选择控件；歧义状态不保留任何隐式默认值。</para>
    /// Clears the package programmer candidates and picker; an ambiguous state keeps no implicit default.
    /// </summary>
    private void ResetEdlProgrammer()
    {
        _edlProgrammer = null;
        _edlProgrammerCandidates = [];
        EdlProgrammerPickBox.ItemsSource = null;
        EdlProgrammerPickBox.SelectedIndex = -1;
        EdlProgrammerPickBox.IsVisible = false;
        EdlProgrammerChoiceLabel.IsVisible = false;
    }

    /// <summary>
    /// <para>按界面选择构造 EDL 启动要求：未勾选鉴权时输出 null；输入无效时记录本地化提示并返回 false。</para>
    /// Builds the EDL startup requirements from the page; outputs null when authentication is off, and reports a
    /// localized diagnostic and returns false when the inputs are incomplete.
    /// </summary>
    private bool TryBuildEdlInitialization(out EdlInitializationOptions? initialization)
    {
        initialization = null;
        var netType = EdlNetTypeBox.IsChecked == true;
        var qudRecovery = EdlQudRecoveryBox.IsChecked == true;
        var pendingCompletion = EdlPendingCompletionBox.IsChecked == true;
        var streamDload = EdlAllowStreamDloadBox.IsChecked == true;
        if (!TryReadEdlAuthTimeout(EdlAuthTimeoutBox.Text, out var authTimeoutMs))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_EdlAuthTimeoutInvalid"));
            return false;
        }

        if (EdlAuthKindBox.SelectedIndex == EdlAuthKindNone)
        {
            initialization = netType || qudRecovery || pendingCompletion || streamDload ||
                authTimeoutMs != EdlAuthTimeoutDefaultMs
                ? new EdlInitializationOptions
                {
                    SendOnePlusNetType = netType,
                    QudHelloRecoveryEnabled = qudRecovery,
                    AllowSingleFirehosePendingCompletion = pendingCompletion,
                    AuthenticationTimeoutMs = authTimeoutMs,
                    AllowStreamDload = streamDload
                }
                : null;
            return true;
        }

        var language = LanguageService.Instance;
        if (EdlAuthKindBox.SelectedIndex == EdlAuthKindXiaomi)
        {
            if (_edlXiaomiAuthData is not { } authData)
            {
                AppendLog(language.GetString("Flash_EdlXiaomiAuthInvalid"));
                return false;
            }

            initialization = WithEdlCompatibility(new EdlInitializationOptions
            {
                XiaomiAuthenticationProvider = new EdlXiaomiAuthFileProvider(authData),
            }, netType, qudRecovery, pendingCompletion, authTimeoutMs);
            return true;
        }

        if (!ulong.TryParse(EdlSerialBox.Text?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var serial) || serial == 0)
        {
            AppendLog(language.GetString("Flash_EdlSerialInvalid"));
            return false;
        }

        if (EdlAuthKindBox.SelectedIndex == EdlAuthKindDemacia)
        {
            var projectId = EdlProjectIdBox.Text?.Trim() ?? string.Empty;
            if (projectId.Length == 0)
            {
                AppendLog(language.GetString("Flash_EdlProjectIdMissing"));
                return false;
            }

            initialization = WithEdlCompatibility(new EdlInitializationOptions
            {
                QueryIdentityBeforeUpload = true,
                ProjectAuthentication = new EdlProjectAuthenticationOptions
                {
                    Kind = EdlProjectAuthenticationKind.OnePlusDemacia,
                    ProjectId = projectId,
                    SerialNumber = serial,
                    Provider = new EdlOnePlusDemaciaAuthProvider(),
                },
            }, netType, qudRecovery, pendingCompletion, authTimeoutMs);
            return true;
        }

        if (EdlAuthKindBox.SelectedIndex == EdlAuthKindNothingProject)
        {
            var nothingProjectId = EdlProjectIdBox.Text?.Trim() ?? string.Empty;
            if (nothingProjectId.Length == 0)
            {
                AppendLog(language.GetString("Flash_EdlProjectIdMissing"));
                return false;
            }

            if (netType)
            {
                AppendLog(language.GetString("Flash_EdlNetTypeConflict"));
                return false;
            }

            if (!EdlNothingProjectCatalog.TryResolve(nothingProjectId, out var nothingProfile) ||
                nothingProfile is null)
            {
                AppendLog($"{language.GetString("Flash_EdlNothingProjectUnknown")} {nothingProjectId}");
                return false;
            }

            initialization = WithEdlCompatibility(new EdlInitializationOptions
            {
                QueryIdentityBeforeUpload = true,
                ProjectAuthentication = new EdlProjectAuthenticationOptions
                {
                    Kind = EdlProjectAuthenticationKind.NothingProject,
                    ProjectId = nothingProjectId,
                    SerialNumber = serial,
                    Provider = new EdlNothingProjectAuthProvider(nothingProfile),
                },
            }, netType, qudRecovery, pendingCompletion, authTimeoutMs);
            return true;
        }

        if (!TrySelectedOnePlusProfile(out var profile))
        {
            return false;
        }

        if (netType && profile.Family != EdlOnePlusProjectFamily.SoftwareProjectModel)
        {
            AppendLog(language.GetString("Flash_EdlNetTypeConflict"));
            return false;
        }

        var provider = new EdlOnePlusProjectAuthProvider(profile);
        initialization = WithEdlCompatibility(new EdlInitializationOptions
        {
            QueryIdentityBeforeUpload = true,
            ProjectAuthentication = new EdlProjectAuthenticationOptions
            {
                Kind = profile.Family == EdlOnePlusProjectFamily.SoftwareProjectModel
                    ? EdlProjectAuthenticationKind.OnePlusSoftwareProject
                    : EdlProjectAuthenticationKind.OnePlusProject,
                ProjectId = profile.ProjectId,
                SerialNumber = serial,
                Provider = provider,
                CommandTokenProvider = provider,
            },
        }, netType, qudRecovery, pendingCompletion, authTimeoutMs);
        return true;
    }

    /// <summary>
    /// <para>取界面选中的 OnePlus 目录项并解析配置；未选择或目录解析失败时记录本地化提示。</para>
    /// Resolves the catalogue entry selected on the page; a missing or unresolvable selection reports a localized
    /// diagnostic and returns false.
    /// </summary>
    private bool TrySelectedOnePlusProfile(out EdlOnePlusProjectProfile profile)
    {
        var index = EdlOnePlusModelBox.SelectedIndex;
        if (index >= 0 && index < _edlOnePlusModelIds.Count &&
            EdlOnePlusProjectCatalog.TryResolve(_edlOnePlusModelIds[index], out var resolved) &&
            resolved is not null)
        {
            profile = resolved;
            return true;
        }

        profile = null!;
        AppendLog(LanguageService.Instance.GetString("Flash_EdlOnePlusModelMissing"));
        return false;
    }

    /// <summary>
    /// <para>切换鉴权命令家族：目录项目校验需要选择机型，demacia 与不鉴权则使用手输项目号。</para>
    /// Switches the authentication command family: catalogue project verification needs a model selection, while
    /// demacia and no authentication keep the manually typed project name.
    /// </summary>
    private void OnEdlAuthKindChanged(object? source, SelectionChangedEventArgs e)
    {
        var catalogue = EdlAuthKindBox.SelectedIndex == EdlAuthKindOnePlusProject;
        var xiaomi = EdlAuthKindBox.SelectedIndex == EdlAuthKindXiaomi;
        EdlOnePlusModelBox.IsVisible = catalogue;
        EdlProjectIdBox.IsEnabled = !catalogue && !xiaomi;
        ChooseEdlXiaomiAuthFileButton.IsVisible = xiaomi;
        EdlXiaomiAuthFileText.IsVisible = xiaomi;
        if (catalogue && EdlOnePlusModelBox.SelectedIndex < 0)
        {
            EdlProjectIdBox.Text = string.Empty;
        }
    }

    /// <summary>
    /// <para>选择小米固定鉴权数据文件；上游 xiaomi 模块的凭据块恰好为 256 字节，挑战请求原样忽略。</para>
    /// Picks the Xiaomi fixed auth-data file; the upstream xiaomi module's credential blob is exactly
    /// 256 bytes and the challenge request is ignored verbatim.
    /// </summary>
    private async void OnChooseEdlXiaomiAuthFileClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_EdlXiaomiAuthFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var data = await File.ReadAllBytesAsync(path);
            if (data.Length != 256)
            {
                throw new InvalidDataException(LanguageService.Instance.GetString("Flash_EdlXiaomiAuthInvalid"));
            }
            _edlXiaomiAuthData = data;
            EdlXiaomiAuthFileText.Text = Path.GetFileName(path);
            ToolTip.SetTip(EdlXiaomiAuthFileText, path);
            AppendLog(Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _edlXiaomiAuthData = null;
            EdlXiaomiAuthFileText.Text = string.Empty;
            AppendLog(ex.Message);
        }
    }

    /// <summary>
    /// <para>按目录项把令牌内的项目名回填到项目号输入框，避免用户输入与配置不一致的名称。</para>
    /// Writes the token project name from the catalogue entry into the project field so the typed value cannot
    /// disagree with the resolved profile.
    /// </summary>
    private void OnEdlOnePlusModelChanged(object? source, SelectionChangedEventArgs e)
    {
        if (TrySelectedOnePlusProfile(out var profile))
        {
            EdlProjectIdBox.Text = profile.ProjectId;
            EdlProjectIdBox.IsEnabled = false;
        }
    }

    /// <summary>
    /// <para>用当前所选 programmer 与启动要求把会话推进到 Firehose 配置态，供擦除、回读等单项操作复用；
    /// 已配置且无鉴权要求时直接复用现有配置。</para>
    /// Advances the session to the configured Firehose state with the selected programmer and startup
    /// requirements, reused by the single-operation erase and read-back paths; an already configured session
    /// without authentication requirements is reused as-is.
    /// </summary>
    private async Task<FirehoseProtocol> ConfigureEdlAsync(EdlSession session,
        EdlInitializationOptions? initialization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (initialization is null && session.IsConfigured && session.Firehose is { } existing)
        {
            return existing;
        }

        var programmerPath = _edlProgrammerOverride ?? _edlProgrammer;
        using var programmer = programmerPath is { } loader && File.Exists(loader)
            ? File.OpenRead(loader)
            : null;
        await session.EnsureInitializedAsync(programmer, initialization, cancellationToken).ConfigureAwait(true);
        return session.Firehose ?? throw new InvalidOperationException(
            "The EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
    }

    private void OnEdlProgrammerPickChanged(object? source, SelectionChangedEventArgs e)
    {
        var index = EdlProgrammerPickBox.SelectedIndex;
        _edlProgrammer = index >= 0 && index < _edlProgrammerCandidates.Count
            ? _edlProgrammerCandidates[index]
            : null;
        UpdateEdlButton();
    }

    /// <summary>
    /// <para>按所选设备的协议切换操作面板可见性：Fastboot 专属项（读取信息、全选显示、切换槽位、擦除）
    /// 只在 Fastboot 下显示；EDL 显示专用面板，完整包按钮同时要求包就绪与 EDL 协议；串口设备显示
    /// MTK 面板并隐藏通用“刷写所选”；其余协议隐藏 EDL 与 MTK 两块。</para>
    /// Switches the action-pane visibility by the selected device's protocol: Fastboot-only items
    /// (read info, show-all, switch slot, erase) appear only for Fastboot; EDL shows the dedicated
    /// pane and the bundle button additionally requires a ready bundle and the EDL protocol; serial
    /// devices show the MTK pane and hide the generic flash-selected button; every other protocol
    /// hides both panes.
    /// </summary>
    private void UpdateModeVisibility()
    {
        var protocol = SelectedDevice?.Protocol;
        var isEdl = protocol is DeviceProtocolKind.Edl;
        var isFastboot = protocol is DeviceProtocolKind.Fastboot;
        var isMtkSerial = SelectedDevice is { Transport: DeviceTransportKind.Serial };
        var isMtkUsb = protocol is DeviceProtocolKind.Mtk
            && SelectedDevice is { Transport: DeviceTransportKind.Usb };
        EdlHeader.IsVisible = EdlPane.IsVisible = isEdl;
        var bundleRowVisible = _edlBundleReady && (isEdl || SelectedDevice is null);
        if (bundleRowVisible && !BundleActionRow.IsVisible)
        {
            RefreshEdlSerialPorts();
        }

        BundleActionRow.IsVisible = bundleRowVisible;
        FlashEdlBundleButton.IsVisible = isEdl;
        EdlSerialFlashButton.IsVisible = SelectedDevice is null;
        MtkPane.IsVisible = isMtkSerial || _mtkResolution is not null || protocol is DeviceProtocolKind.Mtk;
        MtkScatterHintText.IsVisible = _mtkResolution is null;
        MtkPortLabel.IsVisible = !isMtkUsb;
        MtkPortBox.IsVisible = !isMtkUsb;
        RefreshMtkPortsButton.IsVisible = !isMtkUsb;
        MtkBaudLabel.IsVisible = !isMtkUsb;
        MtkBaudBox.IsVisible = !isMtkUsb;
        MtkAttachAgentBox.IsVisible = !isMtkUsb;
        SwitchSlotButton.IsVisible = isFastboot;
        ReadInfoButton.IsVisible = isFastboot || isMtkUsb;
        ShowAllPartitionsBox.IsVisible = isFastboot;
        EraseSelectedButton.IsVisible = isFastboot;
        FlashSelectedButton.IsVisible = protocol is not DeviceProtocolKind.Mtk;
        AddRootBox.IsVisible = !isEdl && !isMtkSerial;
        ChooseRootPackageButton.IsVisible = !isEdl && !isMtkSerial;
        DeleteCowBox.IsVisible = !isEdl && !isMtkSerial;
        EdlReadbackButton.IsEnabled = !_busy && isEdl && HasSelectedEdlEntries();
        EdlEraseButton.IsEnabled = !_busy && isEdl && HasSelectedEdlEntries();
        EdlReadStorageInfoButton.IsEnabled = !_busy && isEdl;
        EdlRepairGptButton.IsEnabled = !_busy && isEdl;
        EdlRamDumpButton.IsEnabled = !_busy && isEdl;
        MtkUsbOnlyRow.IsVisible = isMtkUsb;
        var isMtkPreloader = isMtkUsb && SelectedDevice?.State == "preloader";
        MtkPreloaderPane.IsVisible = isMtkPreloader;
        var preloaderEnabled = !_busy && isMtkPreloader;
        MtkPreloaderOffsetBox.IsEnabled = preloaderEnabled;
        MtkPreloaderLengthBox.IsEnabled = preloaderEnabled;
        MtkPreloaderStepBox.IsEnabled = preloaderEnabled;
        MtkPreloaderConfirmFlashMappingBox.IsEnabled = preloaderEnabled;
        MtkPreloaderDumpButton.IsEnabled = preloaderEnabled;
        MtkPreloaderPeekButton.IsEnabled = preloaderEnabled;
        MtkPreloaderWriteDataBox.IsEnabled = preloaderEnabled;
        MtkPreloaderWriteConfirmBox.IsEnabled = preloaderEnabled;
        MtkPreloaderWriteButton.IsEnabled = preloaderEnabled;
        MtkPreloaderReturnToBromButton.IsEnabled = preloaderEnabled;
        MtkPreloaderDisableWatchdogButton.IsEnabled = preloaderEnabled;
        MtkPreloaderPartitionNameBox.IsEnabled = preloaderEnabled;
        MtkPreloaderJumpButton.IsEnabled = preloaderEnabled;
        var exploitGranted = !_busy && isMtkUsb && MtkExploitBypassBox.IsChecked == true &&
            MtkExploitVolatileBox.IsChecked == true;
        MtkExploitFamilyBox.IsEnabled = exploitGranted;
        MtkExploitPayloadBox.IsEnabled = MtkExploitFamilyBox.IsEnabled;
        MtkBypassButton.IsEnabled = MtkExploitFamilyBox.IsEnabled;
        MtkBruteVar1Box.IsVisible = isMtkUsb &&
            ReadExploitFamily(MtkExploitFamilyBox.SelectedIndex) == MtkExploitFamily.Kamakiri;
        MtkBruteVar1Box.IsEnabled = exploitGranted;
        MtkRpmbModeBox.IsEnabled = !_busy && isMtkUsb;
        MtkRpmbOffsetBox.IsEnabled = MtkRpmbModeBox.IsEnabled;
        MtkRpmbLengthBox.IsEnabled = MtkRpmbModeBox.IsEnabled;
        MtkRpmbReadButton.IsEnabled = MtkRpmbModeBox.IsEnabled;
        var mtkProtocolReady = !_busy && protocol is DeviceProtocolKind.Mtk;
        MtkCrashModeBox.IsEnabled = mtkProtocolReady;
        MtkCrashButton.IsEnabled = mtkProtocolReady;
        MtkSetSpeedButton.IsEnabled = mtkProtocolReady;
        MtkRebootMetaButton.IsEnabled = mtkProtocolReady;
        MtkRebootAndroidTestButton.IsEnabled = mtkProtocolReady;
        MtkSecCfgPartitionBox.IsEnabled = mtkProtocolReady;
        MtkSecCfgReadButton.IsEnabled = mtkProtocolReady;
        MtkSecCfgLockBox.IsEnabled = mtkProtocolReady;
        MtkSecCfgApplyButton.IsEnabled = mtkProtocolReady;
        MtkEfusePublicKeyButton.IsEnabled = mtkProtocolReady;
        MtkEfuseStartBox.IsEnabled = mtkProtocolReady;
        MtkEfuseCountBox.IsEnabled = mtkProtocolReady;
        MtkEfuseRangeButton.IsEnabled = mtkProtocolReady;
        MtkFullDumpButton.IsEnabled = mtkProtocolReady;
        MtkMemoryAddressBox.IsEnabled = mtkProtocolReady;
        MtkMemoryLengthBox.IsEnabled = mtkProtocolReady;
        MtkMemoryRegisterBox.IsEnabled = mtkProtocolReady;
        MtkMemoryPeekButton.IsEnabled = mtkProtocolReady;
        MtkPokeDataBox.IsEnabled = mtkProtocolReady;
        MtkPokeConfirmBox.IsEnabled = mtkProtocolReady;
        MtkPokeButton.IsEnabled = mtkProtocolReady;
        MtkBromDumpButton.IsEnabled = mtkProtocolReady;
        MtkOemKeyKindBox.IsEnabled = mtkProtocolReady;
        MtkOemKeyHexBox.IsEnabled = mtkProtocolReady;
        MtkOemKeySejBaseBox.IsEnabled = mtkProtocolReady;
        MtkOemKeyProgramButton.IsEnabled = mtkProtocolReady;
        MtkOemKeyPreviewButton.IsEnabled = mtkProtocolReady;
        EdlChooseProgrammerButton.IsEnabled = !_busy && isEdl;
        EdlResetButton.IsEnabled = !_busy && isEdl;
        EdlPowerOffButton.IsEnabled = !_busy && isEdl;
        EdlResetToEdlButton.IsEnabled = !_busy && isEdl;
        EdlActiveSlotBox.IsEnabled = !_busy && isEdl;
        EdlSetActiveSlotButton.IsEnabled = !_busy && isEdl;
        EdlChooseUfsProfileButton.IsEnabled = !_busy && isEdl;
        EdlUfsPreviewButton.IsEnabled = !_busy && isEdl && _edlUfsProfile is not null;
        EdlUfsProvisionButton.IsEnabled = !_busy && isEdl && _edlUfsProfile is not null;
        EdlUfsResetButton.IsEnabled = !_busy && isEdl;
        if (!isEdl || !string.Equals(_edlCapabilityDevice, SelectedDevice?.Key, StringComparison.Ordinal))
        {
            EdlCapabilityText.IsVisible = false;
        }
    }

    /// <summary>
    /// <para>判断是否选中了可回读的 EDL 条目。</para>
    /// Returns whether any readable EDL entry is selected.
    /// </summary>
    private bool HasSelectedEdlEntries()
        => _model.Partitions.Any(row => row.IsSelected && row.CanSelect);

    private void SetBusy(bool value)
    {
        _busy = value;
        _model.IsBusy = value;
        EdlPane.IsEnabled = !value;
        MtkPane.IsEnabled = !value;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlashViewModel.SelectedCount))
        {
            UpdateEdlButton();
        }
    }

    private void RefreshMtkPorts()
    {
        var previous = MtkPortBox.SelectedItem as string;
        var ports = SerialTransport.GetAvailablePorts();
        MtkPortBox.ItemsSource = ports;
        MtkPortBox.SelectedItem = previous is { } name && ports.Contains(name)
            ? previous
            : ports.Count > 0 ? ports[0] : null;
    }

    private void RefreshEdlSerialPorts()
    {
        var previous = EdlSerialPortBox.SelectedItem as string;
        var ports = SerialTransport.GetAvailablePorts();
        EdlSerialPortBox.ItemsSource = ports;
        EdlSerialPortBox.SelectedItem = previous is { } name && ports.Contains(name)
            ? previous
            : ports.Count > 0 ? ports[0] : null;
    }

    private void SetRowsQueued(IReadOnlyList<FlashPartitionEntry> rows)
    {
        _overallRows = rows.ToArray();
        var language = LanguageService.Instance;
        foreach (var row in rows)
        {
            row.State = FlashRowState.Queued;
            row.Percent = 0;
            row.StatusText = language.GetString("Flash_Status_Queued");
        }

        UpdateOverall();
    }

    private void SetRowRunning(FlashPartitionEntry row, string textKey)
    {
        row.State = FlashRowState.Running;
        row.Percent = 0;
        row.StatusText = LanguageService.Instance.GetString(textKey);
    }

    private void SetRowFinished(FlashPartitionEntry row, bool success)
    {
        var language = LanguageService.Instance;
        row.State = success ? FlashRowState.Succeeded : FlashRowState.Failed;
        row.StatusText = language.GetString(success ? "Flash_Status_Succeeded" : "Flash_Status_Failed");
        if (success)
        {
            row.Percent = 100;
        }

        UpdateOverall();
    }

    private void MarkUnfinishedSkipped()
    {
        var language = LanguageService.Instance;
        foreach (var row in _overallRows.Where(row => row.State is FlashRowState.Queued or FlashRowState.Running))
        {
            row.State = FlashRowState.Skipped;
            row.StatusText = language.GetString("Flash_Status_Skipped");
        }
    }

    private void UpdateOverall()
    {
        var rows = _overallRows;
        if (rows.Count == 0)
        {
            _model.OverallPercent = 0;
            _model.OverallText = string.Empty;
            return;
        }

        var finished = 0;
        double share = 0;
        foreach (var row in rows)
        {
            switch (row.State)
            {
                case FlashRowState.Succeeded:
                case FlashRowState.Failed:
                case FlashRowState.Skipped:
                    finished++;
                    share += 1;
                    break;
                case FlashRowState.Running:
                    share += Math.Clamp((double)row.Percent / 100d, 0d, 1d);
                    break;
            }
        }

        _model.OverallPercent = share / rows.Count * 100d;
        _model.OverallText = string.Format(LanguageService.Instance.GetString("Flash_OverallProgress"), finished, rows.Count);
    }

    private void AppendLog(string message) => _model.LogText += message + Environment.NewLine;

    /// <summary>
    /// <para>把 GPT 分区表格式化为若干行；超过 <paramref name="maxLines"/> 时只保留前若干行并追加一条
    /// 余量提示。格式由调用方从资源字典给出，因此结果随当前界面语言变化。</para>
    /// Formats a GPT partition table into lines, keeping at most <paramref name="maxLines"/> rows and
    /// appending a remainder note beyond that. The caller supplies the formats from the resource
    /// dictionary, so the result follows the current interface language.
    /// </summary>
    /// <param name="partitions"><para>已校验的 GPT 分区列表。</para>The validated GPT partition list.</param>
    /// <param name="maxLines"><para>最大行数，必须为正。</para>Maximum line count; must be positive.</param>
    /// <param name="rowFormat"><para>单行格式（名称、起始扇区、扇区数）。</para>Per-row format (name, first sector, sector count).</param>
    /// <param name="moreFormat"><para>余量提示格式（未列出的分区数）。</para>Remainder-note format (unlisted count).</param>
    /// <returns><para>按顺序排列的日志行。</para>The log lines in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="partitions"/> 为 null。
    /// <para><paramref name="partitions"/> is null.</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLines"/> 非正。
    /// <para><paramref name="maxLines"/> is not positive.</para></exception>
    internal static List<string> FormatPartitionTable(IReadOnlyList<GptPartition> partitions, int maxLines,
        string rowFormat, string moreFormat)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        ArgumentNullException.ThrowIfNull(rowFormat);
        ArgumentNullException.ThrowIfNull(moreFormat);
        var lines = new List<string>(Math.Min(partitions.Count, maxLines + 1));
        for (var index = 0; index < partitions.Count; index++)
        {
            if (index == maxLines)
            {
                lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, moreFormat,
                    partitions.Count - index));
                break;
            }

            var partition = partitions[index];
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, rowFormat,
                partition.Name, partition.FirstLba, partition.LastLba - partition.FirstLba + 1));
        }

        return lines;
    }

    private void AppendPartitionTable(IReadOnlyList<GptPartition> partitions)
    {
        var language = LanguageService.Instance;
        foreach (var line in FormatPartitionTable(partitions, MaxPartitionTableLines,
                     language.GetString("Flash_PartitionTableRow"),
                     language.GetString("Flash_PartitionTableMore")))
        {
            AppendLog(line);
        }
    }

    private async void OnReadInfoClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice is { Protocol: DeviceProtocolKind.Mtk, Transport: DeviceTransportKind.Usb })
        {
            await RunMtkDeviceInfoAsync();
            return;
        }

        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Fastboot } device || _busy || _confirming) return;
        SetBusy(true);
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
            _mtkResolution = null;
            _mtkRows.Clear();
            _mtkPartitions.Clear();
            MtkPane.IsVisible = false;
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
        finally { SetBusy(false); }
    }

    private async Task RunMtkDeviceInfoAsync()
    {
        var language = LanguageService.Instance;
        await RunWithReadyMtkOperationAsync(async (session, token) =>
        {
            if (SelectedMtkProtocol != MtkDaProtocol.XFlash)
            {
                AppendLog(language.GetString("Flash_MtkSpeedFamilyUnsupported"));
                return;
            }

            var label = language.GetString("Flash_MtkDevCtrlGroup");
            var ram = await session.GetXFlashRamInfoAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkRamInfo")}: {ram}");
            var randomId = await session.GetXFlashRandomIdAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkRandomId")}: {Convert.ToHexString(randomId)}");
            var life = await session.GetXFlashStorageLifeCycleCheckAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkStorageLife")}: 0x{life:X8}");
            var expire = await session.GetXFlashExpireDateAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkExpireDate")}: {Convert.ToHexString(expire)}");
            var category = await session.GetXFlashPartitionTableCategoryAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkPartitionCategory")}: {category}");
        });
    }

    private async void OnFlashSelectedClick(object? sender, RoutedEventArgs e)
        => await RunSelectedAsync(erase: false);

    private async void OnFlashEdlBundleClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _edlProgrammer is null || !_flatBinPackage ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not Window owner ||
            !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal)) return;
        await RunEdlBundleAsync(useSerialPort: false, device, serialPort: null, owner);
    }

    private async void OnEdlSerialFlashClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _edlProgrammer is null || !_flatBinPackage ||
            TopLevel.GetTopLevel(this) is not Window owner ||
            !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal)) return;
        if (EdlSerialPortBox.SelectedItem is not string serialPort)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_EdlSerialPortRequired"));
            return;
        }

        await RunEdlBundleAsync(useSerialPort: true, device: null, serialPort, owner);
    }

    /// <summary>
    /// <para>执行完整 EDL 包刷写：USB 路径复扫设备后再刷，串口路径复核端口仍在；两条路径共用
    /// 全部选项、调优参数、进度与取消。</para>
    /// Runs a full EDL bundle flash: the USB path re-scans the device first and the serial path
    /// re-checks that the port is still present; both paths share every option, tuning value,
    /// progress report and cancellation.
    /// </summary>
    /// <param name="useSerialPort">Whether the run targets an explicit serial port.
    /// <para>本次运行是否针对显式串口。</param>
    /// <param name="device">The confirmed USB EDL device, or null for a serial run.
    /// <para>已确认的 USB EDL 设备；串口运行为 null。</param>
    /// <param name="serialPort">The confirmed serial port, or null for a USB run.
    /// <para>已确认的串口；USB 运行为 null。</param>
    /// <param name="owner">The window owning the confirmation dialogs. <para>持有确认对话框的窗口。</param>
    private async Task RunEdlBundleAsync(bool useSerialPort, DetectedDevice? device, string? serialPort, Window owner)
    {
        var language = LanguageService.Instance;
        var programmerPath = _edlProgrammerOverride ?? _edlProgrammer;
        if (programmerPath is null || !_flatBinPackage) return;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_FlashEdlBundle"),
                useSerialPort ? serialPort! : device!.DisplayName);
        }
        finally { _confirming = false; }
        if (!confirmed || _model.PackagePath != _loadedPath) return;
        if (useSerialPort)
        {
            if (EdlSerialPortBox.SelectedItem is not string currentPort || currentPort != serialPort) return;
        }
        else if (SelectedDevice is not DetectedDevice current || current.Key != device!.Key ||
                 (await Task.Run(App.DeviceMonitor.Scan)).All(found =>
                     found.Key != device.Key || found.Protocol != DeviceProtocolKind.Edl))
        {
            return;
        }

        if (!TryReadEdlBundleTuning(out var bundleTuning)) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;
        if (useSerialPort && initialization is not null)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_EdlSerialAuthUnsupported"));
            return;
        }
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        FlashSelectedButton.IsEnabled = false;
        FlashEdlBundleButton.IsEnabled = false;
        EdlSerialFlashButton.IsEnabled = false;
        EdlSerialPortBox.IsEnabled = false;
        PackagePathBox.IsEnabled = false;
        FlashPartitionEntry? bundleActiveRow = null;
        try
        {
            var selectedEdlLabels = _model.Partitions
                .Where(row => row.IsSelected && row.CanSelect && _edlEntries.ContainsKey(row))
                .Select(row => _edlEntries[row].Label)
                .Where(label => label.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var bundleLabels = selectedEdlLabels.Length > 0 && selectedEdlLabels.Length < _edlEntries.Count
                ? selectedEdlLabels
                : null;
            var bundleRows = _model.Partitions
                .Where(row => row.CanSelect && _edlEntries.TryGetValue(row, out var entry) &&
                    (bundleLabels is null || (entry.Label.Length > 0 &&
                        bundleLabels.Contains(entry.Label, StringComparer.OrdinalIgnoreCase))))
                .ToArray();
            if (bundleRows.Length > 0)
            {
                SetRowsQueued(bundleRows);
            }

            var rowsByName = new Dictionary<string, FlashPartitionEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in bundleRows)
            {
                var entry = _edlEntries[row];
                var displayName = entry.Label.Length > 0 ? entry.Label : entry.Filename;
                if (displayName.Length > 0)
                {
                    rowsByName.TryAdd(displayName, row);
                }
            }

            var progress = new Progress<FlashBundleProgress>(update =>
            {
                if (update.Phase is FlashBundlePhase.Programming or FlashBundlePhase.Erasing or FlashBundlePhase.Patching)
                {
                    var matched = MatchBundleRow(rowsByName, update.Message);
                    if (matched is not null && !ReferenceEquals(matched, bundleActiveRow))
                    {
                        if (bundleActiveRow is { State: FlashRowState.Running })
                        {
                            SetRowFinished(bundleActiveRow, success: true);
                        }

                        bundleActiveRow = matched;
                        SetRowRunning(matched, "Flash_Status_Flashing");
                    }

                    if (bundleActiveRow is { State: FlashRowState.Running } running && update.Total > 0)
                    {
                        running.Percent = (int)Math.Clamp(update.Current * 100d / update.Total, 0d, 100d);
                        UpdateOverall();
                    }
                }

                if (update.Phase is not FlashBundlePhase.Parsing)
                {
                    AppendLog(update.Message);
                }
            });
            var warnings = useSerialPort
                ? await App.DeviceMonitor.FlashEdlSerialAsync(
                    new SerialConnectionOptions
                    {
                        PortName = serialPort!,
                        BaudRate = EdlSerialBaudBox.SelectedItem is int baud ? baud : 115200,
                        Handshake = Handshake.None
                    },
                    _loadedPath, programmerPath,
                    configuration: null, includeUfsProvision: EdlSkipUfsBox.IsChecked != true,
                    cancellationToken: cancellation.Token,
                    continueOnFailure: ContinueOnFailureBox.IsChecked == true,
                    skipAbsentSources: SkipAbsentSourcesBox.IsChecked == true,
                    labels: bundleLabels, progress: progress,
                    verify: VerifyAfterFlashBox.IsChecked == true, tuning: bundleTuning)
                : await App.DeviceMonitor.FlashEdlBundleAsync(device!, _loadedPath, programmerPath,
                    cancellation.Token, ContinueOnFailureBox.IsChecked == true,
                    SkipAbsentSourcesBox.IsChecked == true, bundleLabels, progress,
                    VerifyAfterFlashBox.IsChecked == true, EdlSkipUfsBox.IsChecked == true, bundleTuning,
                    initialization);
            if (bundleActiveRow is { State: FlashRowState.Running })
            {
                SetRowFinished(bundleActiveRow, success: true);
            }

            foreach (var row in _overallRows.Where(row =>
                         row.State is FlashRowState.Queued or FlashRowState.Running))
            {
                SetRowFinished(row, success: true);
            }

            AppendLog(LanguageService.Instance.GetString("Flash_FlashEdlBundle") + ": OK");
            foreach (var warning in warnings)
            {
                AppendLog($"{LanguageService.Instance.GetString("Flash_WarningPrefix")}: {warning}");
            }
        }
        catch (Exception ex)
        {
            if (bundleActiveRow is { State: FlashRowState.Running })
            {
                SetRowFinished(bundleActiveRow, success: false);
            }

            MarkUnfinishedSkipped();
            UpdateOverall();
            AppendLog(ex.Message);
        }
        finally
        {
            _flashCancellation = null;
            SetBusy(false);
            CancelFlashButton.IsEnabled = false;
            FlashSelectedButton.IsEnabled = true;
            UpdateEdlButton();
            PackagePathBox.IsEnabled = true;
            EdlSerialPortBox.IsEnabled = true;
        }
    }

    /// <summary>
    /// <para>读取整包三项调优控件并给出校验结论；前导清零输入必须是非负整数，失败原因写入日志。</para>
    /// Reads the three bundle tuning controls and reports the validation outcome; the leading-wipe
    /// input must be a non-negative integer and the failure reason is written to the log.
    /// </summary>
    /// <param name="tuning"><para>校验通过时输出的调优参数，否则输出未校验的默认值。</para>
    /// The tuning values on success, or unvalidated defaults on failure.</param>
    /// <returns><para>输入合法时为 true。</para>True when the input is valid.</returns>
    internal bool TryReadEdlBundleTuning(out EdlBundleTuning tuning)
    {
        tuning = new EdlBundleTuning();
        if (!long.TryParse(EdlWipeFirstBox.Text, out var wipeFirstSectors) || wipeFirstSectors < 0)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_EdlWipeFirstInvalid"));
            return false;
        }

        tuning = new EdlBundleTuning
        {
            OverrideSectorSize = EdlSectorSizeBox.SelectedIndex switch
            {
                1 => 512L,
                2 => 2048L,
                3 => 4096L,
                _ => null
            },
            Slot = EdlSlotBox.SelectedIndex > 0 ? EdlSlotBox.SelectedIndex : null,
            WipeFirstSectors = wipeFirstSectors
        };
        return true;
    }

    private static FlashPartitionEntry? MatchBundleRow(
        IReadOnlyDictionary<string, FlashPartitionEntry> rowsByName, string message)
    {
        FlashPartitionEntry? matched = null;
        var matchedLength = 0;
        foreach (var (name, row) in rowsByName)
        {
            if (name.Length > matchedLength && message.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                matched = row;
                matchedLength = name.Length;
            }
        }

        return matched;
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
            (await Task.Run(App.DeviceMonitor.Scan)).All(found => found.Key != device.Key ||
                found.Protocol != DeviceProtocolKind.Fastboot))
        {
            AppendLog(language.GetString("Flash_DeviceChanged"));
            return;
        }
        SetBusy(true);
        SwitchSlotButton.IsEnabled = false;
        try
        {
            var slot = await FastbootSlotService.SetOtherAsync(device, CancellationToken.None);
            AppendLog($"{language.GetString("Flash_ActiveSlot")}: {slot}");
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
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
        var rkImageDir = selected.FirstOrDefault(row => row.FilePath.Length > 0) is { } firstRow
            ? Path.GetDirectoryName(Path.GetFullPath(firstRow.FilePath)) ?? string.Empty
            : string.Empty;
        if (!FlashModeAllowed(device.Protocol, _flatBinPackage,
                _edlProgrammer is not null || _edlProgrammerOverride is not null,
                rkImageDir.Length > 0 && FlashService.HasRockchipParameter(rkImageDir)) ||
            device.Protocol == DeviceProtocolKind.Adb && !await FlashSuService.HasSuAsync(device, CancellationToken.None))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_ModeMismatch"));
            return;
        }
        var plainOptions = erase || DeleteCowBox.IsChecked == true || EraseDataBox.IsChecked == true ||
            RebootAfterBox.IsChecked == true || DisableVbmetaBox.IsChecked == true || AddRootBox.IsChecked == true;
        if (device.Protocol != DeviceProtocolKind.Fastboot && plainOptions)
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
        var verifyRequested = !erase && VerifyAfterFlashBox.IsChecked == true;
        var verify = verifyRequested && device.Protocol is DeviceProtocolKind.Edl or DeviceProtocolKind.Rkdl;
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
        if (!TryBuildEdlInitialization(out var initialization))
        {
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
            (await Task.Run(App.DeviceMonitor.Scan)).All(found => found.Key != device.Key || found.Protocol != device.Protocol))
        {
            AppendLog(language.GetString("Flash_DeviceChanged"));
            return;
        }

        SetBusy(true);
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
            if (verifyRequested && !verify)
            {
                AppendLog(language.GetString("Flash_VerifyUnsupported"));
            }

            var rowsByName = new Dictionary<string, FlashPartitionEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in selected)
            {
                rowsByName.TryAdd(row.Name, row);
            }

            var progress = new Progress<FlashProgress>(update =>
            {
                if (update.Partition is { } name && rowsByName.TryGetValue(name, out var row) &&
                    row.State == FlashRowState.Running)
                {
                    row.Percent = update.Percent;
                    UpdateOverall();
                }

                if (update.Percent is 0 or 100)
                {
                    AppendLog($"{update.Partition}: {update.Status}");
                }
            });
            SetRowsQueued(selected);
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
                        FirehoseGptSnapshot? deviceGpt = null;
                        foreach (var row in selected)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            var manifestEntry = _edlEntries.TryGetValue(row, out var mapped) ? mapped : null;
                            if (manifestEntry is not null && (!File.Exists(row.FilePath) ||
                                !Path.GetFullPath(manifestEntry.SourceFile).Equals(
                                    Path.GetFullPath(row.FilePath), StringComparison.OrdinalIgnoreCase)))
                            {
                                throw new InvalidDataException(string.Format(
                                    language.GetString("Flash_EdlImageMismatch"), row.Name));
                            }

                            SetRowRunning(row, "Flash_Status_Flashing");
                            FlashResult result;
                            if (manifestEntry is { } entry)
                            {
                                result = await FlashService.Default.FlashEdlPartitionAsync(
                                    edl, entry, Path.GetDirectoryName(row.FilePath)!,
                                    _edlProgrammerOverride ?? _edlProgrammer, progress, cancellation.Token, verify,
                                    initialization);
                            }
                            else
                            {
                                var firehose = await ConfigureEdlAsync(edl, initialization,
                                    cancellation.Token).ConfigureAwait(true);
                                var snapshot = deviceGpt ??= await firehose.ReadGptAsync(
                                    SelectedEdlLun, cancellation.Token).ConfigureAwait(true);
                                var written = await firehose.ProgramRawPartitionFromFileAsync(snapshot, row.Name,
                                    row.FilePath, verify: verify, cancellationToken: cancellation.Token).ConfigureAwait(true);
                                result = new FlashResult
                                {
                                    Success = true,
                                    Summary = string.Format(language.GetString("Flash_EdlGptProgrammed"),
                                        row.Name, written.AcknowledgedBytes, written.VerifiedBytes)
                                };
                            }

                            AppendLog(result.Summary);
                            SetRowFinished(row, result.Success);
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
                        SetRowRunning(row, "Flash_Status_Flashing");
                        var result = await FlashSuService.FlashAsync(device, row.Name, row.FilePath, cancellation.Token);
                        AppendLog(result);
                        SetRowFinished(row, success: true);
                    }
                }
                return;
            }
            var target = new DeviceInfo(device);
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
                        if (!File.Exists(image))
                        {
                            throw new InvalidDataException(string.Format(
                                language.GetString("Flash_MissingExtractedImage"), filename));
                        }
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
                    entry.State = FlashRowState.Failed;
                    entry.StatusText = language.GetString("Flash_Status_Failed");
                    succeeded = false;
                    break;
                }

                AppendLog($"{command} {entry.Name}: {entry.FileName}");
                SetRowRunning(entry, erase ? "Flash_Status_Erasing" : "Flash_Status_Flashing");
                if (command == "wipe_super")
                {
                    await FlashSuperEmptyService.WipeAsync(device, preparedSuper[entry], cancellation.Token);
                    AppendLog($"{command}: OK");
                    SetRowFinished(entry, success: true);
                }
                else if (command is "flash" or "erase")
                {
                    var result = command == "erase"
                        ? await FlashService.Default.ErasePartitionAsync(target, entry.Name, progress, cancellation.Token)
                        : device.Protocol == DeviceProtocolKind.Rkdl
                            ? await FlashService.FlashRkdlAsync(target, entry.Name,
                                prepared.GetValueOrDefault(entry, entry.FilePath), null, progress, cancellation.Token, verify)
                            : await FlashService.Default.FlashImageAsync(target, entry.Name,
                                prepared.GetValueOrDefault(entry, entry.FilePath), progress, cancellation.Token);
                    AppendLog(result.Summary);
                    SetRowFinished(entry, result.Success);
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
                    SetRowFinished(entry, success: true);
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
            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
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

    private static bool LooksLikeScatter(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > MtkScatterPackage.MaxScatterBytes)
        {
            return false;
        }

        var buffer = new byte[64 * 1024];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.ASCII.GetString(buffer, 0, read).Contains("MTK_PLATFORM", StringComparison.OrdinalIgnoreCase);
    }

    private void OnRefreshMtkPortsClick(object? sender, RoutedEventArgs e) => RefreshMtkPorts();

    private async void OnChooseMtkDaClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_MtkDaFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        SetBusy(true);
        MtkEntryIndexBox.ItemsSource = null;
        MtkEntryIndexBox.SelectedIndex = -1;
        _mtkLoaderEntries = [];
        _mtkLoaderPath = null;
        try
        {
            var entries = await MtkScatterPackage.ListUsableLoaderEntriesAsync(path, CancellationToken.None);
            if (entries.Count == 0)
            {
                throw new InvalidDataException(
                    LanguageService.Instance.GetString("Flash_MtkLoaderNoEntries"));
            }

            _mtkLoaderPath = path;
            _mtkLoaderEntries = entries;
            MtkEntryIndexBox.ItemsSource = entries.Select(entry =>
                string.Format(LanguageService.Instance.GetString("Flash_MtkLoaderEntry"),
                    entry.Index, entry.HardwareCode.ToString("X4"), entry.HardwareSubCode.ToString("X4"),
                    $"{entry.HardwareVersion}{(entry.SoftwareVersion is { } software ? $".{software}" : string.Empty)}")).ToArray();
            MtkEntryIndexBox.SelectedIndex = 0;
            MtkDaPathText.Text = Path.GetFileName(path);
            ToolTip.SetTip(MtkDaPathText, path);
            AppendLog(Path.GetFileName(path));
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    private async void OnChooseMtkEmiClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_MtkEmiFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        SetBusy(true);
        _mtkEmi = null;
        try
        {
            _mtkEmi = ReadEmi(path);
            MtkEmiPathText.Text = Path.GetFileName(path);
            ToolTip.SetTip(MtkEmiPathText, path);
            AppendLog(Path.GetFileName(path));
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// <para>选择 Boot ROM SLA 密钥目录；目录内按 sla_&lt;hwcode&gt;.pem 命名，未选择时 SLA 请求只会被拒绝。</para>
    /// Picks the Boot ROM SLA key directory holding sla_&lt;hwcode&gt;.pem files; without it an SLA request
    /// is refused rather than answered.
    /// </summary>
    private async void OnChooseMtkKeyDirClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_MtkKeyDir"),
            AllowMultiple = false
        });
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } path) return;
        _mtkKeyDirectory = Directory.Exists(path) ? path : null;
        MtkKeyDirText.Text = _mtkKeyDirectory is { } directory
            ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : string.Empty;
        ToolTip.SetTip(MtkKeyDirText, _mtkKeyDirectory ?? string.Empty);
        AppendLog(_mtkKeyDirectory is null
            ? LanguageService.Instance.GetString("Flash_MtkKeyDir")
            : $"{LanguageService.Instance.GetString("Flash_MtkKeyDir")}: {_mtkKeyDirectory}");
    }

    private async void OnChooseMtkAuthFileClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_MtkAuthFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        _mtkAuthFile = File.Exists(path) ? path : null;
        MtkAuthFileText.Text = _mtkAuthFile is { } file ? Path.GetFileName(file) : string.Empty;
        ToolTip.SetTip(MtkAuthFileText, _mtkAuthFile ?? string.Empty);
        AppendLog(_mtkAuthFile is null
            ? LanguageService.Instance.GetString("Flash_MtkAuthFile")
            : $"{LanguageService.Instance.GetString("Flash_MtkAuthFile")}: {_mtkAuthFile}");
    }

    private static MtkEmiParameters ReadEmi(string path)
    {
        using (var stream = File.OpenRead(path))
        {
            try
            {
                return MtkEmiReader.ReadPreloader(stream, 0, MtkEmiPayloadLayout.BootloaderInfo);
            }
            catch (InvalidDataException)
            {
            }
        }

        using var retry = File.OpenRead(path);
        return MtkEmiReader.ReadPreloader(retry, 0, MtkEmiPayloadLayout.BinaryParameters);
    }

    /// <summary>
    /// <para>读取所选 EDL 设备的存储几何与 GPT 摘要并展示；仅供显示，不改变刷写状态。</para>
    /// Reads the selected EDL device's storage geometry and GPT summary for display only.
    /// </summary>
    private async void OnEdlReadStorageInfoClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            !TryBuildEdlInitialization(out var initialization))
        {
            return;
        }

        SetBusy(true);
        var currentKey = device.Key;
        try
        {
            var info = await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, CancellationToken.None)
                    .ConfigureAwait(true);
                var geometry = await firehose.GetStorageGeometryAsync(SelectedEdlLun, CancellationToken.None).ConfigureAwait(true);
                var snapshot = await firehose.ReadGptAsync(SelectedEdlLun, CancellationToken.None).ConfigureAwait(true);
                return (geometry, snapshot, edl.Capabilities);
            }, CancellationToken.None);
            EdlStorageInfoText.Text = string.Format(
                LanguageService.Instance.GetString("Flash_EdlStorageInfoFormat"),
                info.geometry.SectorSize, info.geometry.SectorCount,
                info.geometry.CapacityBytes / 1024d / 1024d / 1024d,
                info.snapshot.Table.Partitions.Count);
            if (FirehoseProtocol.DetectActiveSlot(info.snapshot) is { } activeSlot)
            {
                EdlStorageInfoText.Text += Environment.NewLine + string.Format(
                    LanguageService.Instance.GetString("Flash_EdlActiveSlotDetected"), activeSlot);
            }
            AppendLog(EdlStorageInfoText.Text);
            if (info.Capabilities is { } capabilities)
            {
                EdlCapabilityText.Text = FormatEdlCapabilities(capabilities, info.geometry.SectorSize);
                EdlCapabilityText.IsVisible = true;
                _edlCapabilityDevice = currentKey;
                AppendLog(EdlCapabilityText.Text);
            }
            else
            {
                EdlCapabilityText.IsVisible = false;
            }

            AppendPartitionTable(info.snapshot.Table.Partitions);
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>把 Firehose 能力快照格式化为两行本地化文本：第一行为扇区、载荷与版本，第二行为
    /// 关键命令的三态支持情况。</para>
    /// Formats a Firehose capability snapshot into two localized lines: sector, payload and version
    /// first, then the tri-state support of the key commands.
    /// </summary>
    /// <param name="capabilities">The negotiated capability snapshot. <para>已协商的能力快照。</param>
    /// <param name="fallbackSectorSize">Sector size to use when the snapshot carries none.
    /// <para>快照未携带扇区大小时使用的回退值。</param>
    /// <returns>The localized capability text. <para>本地化的能力文本。</returns>
    private static string FormatEdlCapabilities(FirehoseCapabilitySnapshot capabilities, long fallbackSectorSize)
    {
        var language = LanguageService.Instance;
        return string.Format(language.GetString("Flash_EdlCapabilities"),
                capabilities.SectorSizeInBytes ?? fallbackSectorSize,
                (capabilities.MaxPayloadSizeToTargetInBytes ?? 0) / 1024d / 1024d,
                capabilities.FirehoseVersion ?? "?")
            + Environment.NewLine
            + string.Format(language.GetString("Flash_EdlCapabilityFeatures"),
                SupportToken(language, capabilities.Read),
                SupportToken(language, capabilities.Program),
                SupportToken(language, capabilities.Erase),
                SupportToken(language, capabilities.Patch),
                SupportToken(language, capabilities.Sparse),
                SupportToken(language, capabilities.UfsProvisioning));
    }

    private static string SupportToken(LanguageService language, FeatureSupport support) => support switch
    {
        FeatureSupport.Supported => language.GetString("Flash_SupportYes"),
        FeatureSupport.Unsupported => language.GetString("Flash_SupportNo"),
        _ => language.GetString("Flash_SupportUnknown")
    };

    /// <summary>
    /// <para>按清单声明的扇区范围擦除所选 EDL 分区；<c>NUM_DISK_SECTORS</c> 相对地址原样下发、由目标端
    /// 求值。擦除不可恢复，执行前需用户确认，且每条 NAK 都按失败上报而不是当作成功。</para>
    /// Erases the selected EDL partitions over the sector range declared by the manifest;
    /// <c>NUM_DISK_SECTORS</c> relative addresses are forwarded as-is for the target to evaluate.
    /// Erasure is irreversible, so the user must confirm first, and every NAK is reported as a
    /// failure rather than treated as success.
    /// </summary>
    private async void OnEdlEraseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var selected = _model.Partitions
            .Where(row => row.IsSelected && row.CanSelect)
            .ToArray();
        if (selected.Length == 0) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;
        var language = LanguageService.Instance;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_EdlErase"),
                $"{language.GetString("Flash_EdlEraseConfirm")}{Environment.NewLine}{device.DisplayName} · {string.Join(", ", selected.Select(row => row.Name))}");
        }
        finally { _confirming = false; }
        if (!confirmed || selected.Any(row => !row.IsSelected || !_model.Partitions.Contains(row))) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        EdlEraseButton.IsEnabled = false;
        SetRowsQueued(selected);
        try
        {
            await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token).ConfigureAwait(true);
                FirehoseGptSnapshot? deviceGpt = null;
                foreach (var row in selected)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var entry = _edlEntries.TryGetValue(row, out var manifestEntry)
                        ? manifestEntry
                        : EdlGptPartitionService.ResolveEntry(
                            deviceGpt ??= await firehose.ReadGptAsync(SelectedEdlLun,
                                cancellationToken: cancellation.Token).ConfigureAwait(true),
                            row.Name);
                    var displayName = entry.Label.Length > 0 ? entry.Label : entry.Filename;
                    if (entry.NumPartitionSectors <= 0)
                    {
                        throw new InvalidDataException(string.Format(
                            language.GetString("Flash_EdlEraseRangeUnknown"), displayName));
                    }

                    SetRowRunning(row, "Flash_Status_Erasing");
                    var response = await firehose.EraseAsync(entry.StartSector, entry.NumPartitionSectors,
                        entry.PhysicalPartitionNumber, cancellation.Token).ConfigureAwait(true);
                    if (!response.IsAck)
                    {
                        throw new InvalidOperationException(string.Format(
                            language.GetString("Flash_EdlEraseFailed"), displayName,
                            EdlFailureText.Describe(response)));
                    }

                    AppendLog(displayName);
                    SetRowFinished(row, success: true);
                }

                return true;
            }, cancellation.Token);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            foreach (var row in selected.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>用设备当前读回的分区表重写主、备 GPT 范围，并在写回后按元数据指纹复核；任何写入前都需要用户确认。</para>
    /// Repairs the partition table by writing back the extents built from the table currently read off the
    /// device, then revalidates the metadata fingerprint; the user must confirm before any write is issued.
    /// </summary>
    private async void OnEdlRepairGptClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;
        var language = LanguageService.Instance;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_EdlRepairGpt"),
                $"{language.GetString("Flash_EdlRepairGptConfirm")}{Environment.NewLine}{device.DisplayName}");
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        EdlRepairGptButton.IsEnabled = false;
        try
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            var result = await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token).ConfigureAwait(true);
                return await EdlGptPartitionService.RepairGptCoreAsync(firehose, progress,
                    SelectedEdlLun, cancellation.Token).ConfigureAwait(true);
            }, cancellation.Token);
            AppendLog(result.Summary);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>在选定目录采集目标处于 Sahara 内存调试态时上报的内存区域；不加载 programmer，也不配置存储，
    /// 每个区域单独经临时文件校验后发布。</para>
    /// Collects the memory regions a device reports while it sits in Sahara memory-debug mode into a
    /// chosen directory: no programmer is loaded, no storage is configured, and each region is staged,
    /// verified and published on its own.
    /// </summary>
    private async void OnEdlRamDumpClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = language.GetString("Flash_EdlRamDump"),
            AllowMultiple = false
        });
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } destination) return;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_EdlRamDump"),
                $"{language.GetString("Flash_EdlRamDumpConfirm")}{Environment.NewLine}{device.DisplayName}");
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        EdlRamDumpButton.IsEnabled = false;
        try
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            var result = await App.DeviceMonitor.RunEdlAsync(device, async edl =>
                await EdlMemoryDumpService.DumpCoreAsync(edl, destination, confirmed: true, progress,
                    cancellation.Token).ConfigureAwait(true), cancellation.Token);
            AppendLog(result.Summary);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>经 firehose 复位所选 EDL 设备；复位前会尽力设置可引导存储，执行前需用户确认。</para>
    /// Resets the selected EDL device over firehose after a best-effort bootable-storage setup; the
    /// user must confirm before the command is sent.
    /// </summary>
    private async void OnEdlResetClick(object? sender, RoutedEventArgs e)
        => await RunEdlPowerRequestAsync("Flash_EdlReset", "Flash_EdlResetConfirm", "Flash_EdlResetDone",
            (firehose, cancellationToken) => firehose.ResetAsync(cancellationToken));

    /// <summary>
    /// <para>断电重启之外的三种电源类请求（关机、回到 EDL、切换活动分区）与复位共用同一条路径：同一次确认、
    /// 同一套 programmer 与鉴权配置，并把未被确认的响应作为失败上报。</para>
    /// Power-off, the return to EDL and the active-slot switch share the reset path: the same single
    /// confirmation, the same programmer and authentication configuration, and a response the target did
    /// not acknowledge reported as a failure rather than as success.
    /// </summary>
    private void OnEdlPowerOffClick(object? sender, RoutedEventArgs e)
        => _ = RunEdlPowerRequestAsync("Flash_EdlPowerOff", "Flash_EdlPowerOffConfirm", "Flash_EdlPowerOffDone",
            (firehose, cancellationToken) => firehose.PowerOffAsync(cancellationToken));

    /// <summary>
    /// <para>让设备从 firehose 会话回到 EDL 下载模式。</para>
    /// Returns the device from a firehose session to EDL download mode.
    /// </summary>
    private void OnEdlResetToEdlClick(object? sender, RoutedEventArgs e)
        => _ = RunEdlPowerRequestAsync("Flash_EdlResetToEdl", "Flash_EdlResetToEdlConfirm",
            "Flash_EdlResetToEdlDone", (firehose, cancellationToken) => firehose.ResetToEdlAsync(cancellationToken));

    /// <summary>
    /// <para>把槽位文本归一化为可发送的小写标识：参考实现两种谱系分别以 <c>slot="a"</c> 与
    /// <c>slot="1"</c> 的形式下发，因此这里同时接受 a/b 与 0/1，并按用户所写的值原样发送，不做任何换算。</para>
    /// Normalises the slot text to a lowercase identifier that may be sent: the reference lineages emit
    /// either <c>slot="a"</c> or <c>slot="1"</c>, so both a/b and 0/1 are accepted here and the value the
    /// user wrote is forwarded unchanged, with no conversion.
    /// </summary>
    /// <param name="text">The raw box text. <para>输入框原文。</para></param>
    /// <returns>The normalised slot, or <see langword="null"/>. <para>归一化后的槽位，或
    /// <see langword="null"/>。</para></returns>
    internal static string? NormalizeEdlSlot(string? text) =>
        text?.Trim().ToLowerInvariant() is { Length: 1 } slot && "ab01".Contains(slot, StringComparison.Ordinal)
            ? slot
            : null;

    /// <summary>
    /// <para>按输入框里的槽位标识切换活动 A/B 分区；参考实现分别接受字母与数字两种写法，因此这里接受 a、b、0、1
    /// 并原样下发，其他文本在接触设备之前被拒绝。</para>
    /// Switches the active A/B slot from the box value; the reference implementations accept the letter and
    /// numeric spellings, so a, b, 0 and 1 are forwarded verbatim and any other text is refused before the
    /// device is touched.
    /// </summary>
    private async void OnEdlSetActiveSlotClick(object? sender, RoutedEventArgs e)
    {
        if (NormalizeEdlSlot(EdlActiveSlotBox.Text) is not { } slot)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_EdlSetActiveSlotInvalid"));
            return;
        }

        await RunEdlPowerRequestAsync("Flash_EdlSetActiveSlot", "Flash_EdlSetActiveSlotConfirm",
            "Flash_EdlSetActiveSlotDone",
            (firehose, cancellationToken) => firehose.SetActivePartitionAsync(slot, cancellationToken));
    }

    /// <summary>
    /// <para>解析界面输入的十六进制地址；不允许负数且必须可解析。</para>
    /// Parses a hexadecimal address from the page; negatives and malformed text are refused.
    /// </summary>
    private static bool TryReadEdlHexString(string? text, out long value)
    {
        value = 0;
        return long.TryParse(text?.Trim().Replace(" ", string.Empty), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    /// <summary>
    /// <para>向目标内存写入最多 8 字节：需要勾选不可逆确认，再经对话框二次确认。</para>
    /// Writes up to 8 bytes of target memory: the irreversible-write box must be ticked and the dialog
    /// confirmed a second time.
    /// </summary>
    private async void OnEdlPokeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        if (!TryReadEdlHexString(EdlPokeAddressBox.Text, out var address))
        {
            AppendLog(language.GetString("Flash_EdlPokeInvalid"));
            return;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromHexString(EdlPokeDataBox.Text?.Trim() ?? string.Empty);
        }
        catch (FormatException)
        {
            AppendLog(language.GetString("Flash_EdlPokeInvalid"));
            return;
        }

        if (payload.Length is < 1 or > 8)
        {
            AppendLog(language.GetString("Flash_EdlPokeInvalid"));
            return;
        }

        if (EdlPokeConfirmBox.IsChecked != true)
        {
            AppendLog(language.GetString("Flash_MtkPokeConfirmRequired"));
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_EdlPoke"),
                language.GetString("Flash_EdlPokeConfirm"));
        }
        finally { _confirming = false; }
        if (!confirmed) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        try
        {
            var response = await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token).ConfigureAwait(true);
                return await firehose.PokeAsync(address, payload, cancellation.Token).ConfigureAwait(true);
            }, cancellation.Token);
            AppendLog(response.IsAck
                ? $"0x{address:X} ← {Convert.ToHexString(payload)}"
                : $"{language.GetString("Flash_EdlPowerNotAcknowledged")}");
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>在目标内存内按 8 字节步长复制一段有界范围；写目标前需对话框确认。</para>
    /// Copies a bounded range inside target memory in 8-byte steps; the dialog must be confirmed before
    /// the destination is touched.
    /// </summary>
    private async void OnEdlMemcpyClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        if (!TryReadEdlHexString(EdlMemcpySourceBox.Text, out var source) ||
            !TryReadEdlHexString(EdlMemcpyDestBox.Text, out var destination) ||
            !long.TryParse(EdlMemcpySizeBox.Text?.Trim(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out var size) || size is < 1 or > 16 * 1024 * 1024)
        {
            AppendLog(language.GetString("Flash_EdlPokeInvalid"));
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_EdlMemcpy"),
                language.GetString("Flash_EdlPokeConfirm"));
        }
        finally { _confirming = false; }
        if (!confirmed) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        try
        {
            await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token).ConfigureAwait(true);
                var response = await firehose.MemcpyAsync(destination, source, size, cancellation.Token)
                    .ConfigureAwait(true);
                if (!response.IsAck)
                {
                    throw new InvalidOperationException("The target rejected the memcpy poke.");
                }

                return true;
            }, cancellation.Token);
            AppendLog($"0x{source:X} → 0x{destination:X} ({size} B)");
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>在确认之后于已配置的 firehose 会话上执行一次电源或槽位请求；未被 ACK 的响应带分类文本作为失败抛出，
    /// 会话 retirement 由协议层负责。</para>
    /// Runs one power or slot request on the configured firehose session after confirmation; a response the
    /// target did not acknowledge is thrown as a failure with its classification text, and session
    /// retirement stays the protocol layer's concern.
    /// </summary>
    private async Task RunEdlPowerRequestAsync(string titleKey, string confirmKey, string doneKey,
        Func<FirehoseProtocol, CancellationToken, Task<FirehoseResponse>> request)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                language.GetString(confirmKey));
        }
        finally { _confirming = false; }
        if (!confirmed || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } current) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        try
        {
            await App.DeviceMonitor.RunEdlAsync(current, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token)
                    .ConfigureAwait(true);
                var response = await request(firehose, cancellation.Token).ConfigureAwait(true);
                if (!response.IsAck)
                {
                    throw new InvalidOperationException(string.Format(
                        language.GetString("Flash_EdlPowerNotAcknowledged"), EdlFailureText.Describe(response)));
                }

                return true;
            }, cancellation.Token);
            AppendLog(language.GetString(doneKey));
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>选择并解析 UFS provisioning 描述符文件；解析失败会清空已选配置并把原因写进日志。</para>
    /// Picks and parses a UFS provisioning descriptor file; a parse failure clears the selection and
    /// reports the reason in the log.
    /// </summary>
    private async void OnEdlChooseUfsProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_EdlUfsProfileChoose"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            _edlUfsProfile = await UfsProvisionProfileLoader.LoadAsync(path, CancellationToken.None);
            EdlUfsProfileText.Text = Path.GetFileName(path);
            ToolTip.SetTip(EdlUfsProfileText, path);
        }
        catch (Exception ex)
        {
            _edlUfsProfile = null;
            EdlUfsProfileText.Text = string.Empty;
            AppendLog(ex.Message);
        }

        UpdateEdlButton();
    }

    /// <summary>
    /// <para>在已配置的 firehose 会话上执行一次 UFS provisioning 步骤：写入前需确认，非 UFS 存储由服务层拒绝，
    /// 复用面板选定的 programmer 与鉴权配置而不是另开一次初始化。</para>
    /// Runs one UFS provisioning step on the configured firehose session: the write needs confirmation,
    /// non-UFS storage is refused by the service, and the pane's programmer and authentication selection is
    /// reused instead of starting a second initialization.
    /// </summary>
    private async Task RunEdlUfsOperationAsync(string titleKey, string? confirmKey,
        Func<FirehoseProtocol, CancellationToken, Task<FlashResult>> operation)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        if (confirmKey is not null)
        {
            _confirming = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                    $"{language.GetString(confirmKey)}{Environment.NewLine}{device.DisplayName}");
            }
            finally { _confirming = false; }
            if (!confirmed) return;
        }

        if (!TryBuildEdlInitialization(out var initialization)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        EdlUfsPreviewButton.IsEnabled = false;
        EdlUfsProvisionButton.IsEnabled = false;
        EdlUfsResetButton.IsEnabled = false;
        try
        {
            var result = await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token)
                    .ConfigureAwait(true);
                return await operation(firehose, cancellation.Token).ConfigureAwait(true);
            }, cancellation.Token);
            AppendLog(result.Summary);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>预览描述符文件对该设备会产生的 provisioning 计划；不写出任何字节。</para>
    /// Previews the provisioning plan the descriptor file would produce for this device; no byte is
    /// written.
    /// </summary>
    private async void OnEdlUfsPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (_edlUfsProfile is not { } profile)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_EdlUfsProfileMissing"));
            return;
        }

        await RunEdlUfsOperationAsync("Flash_EdlUfsPreview", null, (firehose, token) =>
            EdlUfsProvisionService.PreviewCoreAsync(firehose, profile,
                new Progress<FlashProgress>(update => AppendLog(update.Status)), token));
    }

    /// <summary>
    /// <para>提交 provisioning 计划：描述符里声明的逻辑单元会真实建立，因此必须先经用户确认。</para>
    /// Submits the provisioning plan: the logical units the descriptor declares are really created, so the
    /// user must confirm first.
    /// </summary>
    private async void OnEdlUfsProvisionClick(object? sender, RoutedEventArgs e)
    {
        if (_edlUfsProfile is not { } profile)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_EdlUfsProfileMissing"));
            return;
        }

        await RunEdlUfsOperationAsync("Flash_EdlUfsProvision", "Flash_EdlUfsProvisionConfirm",
            (firehose, token) => EdlUfsProvisionService.ProvisionCoreAsync(firehose, profile, confirmed: true,
                new Progress<FlashProgress>(update => AppendLog(update.Status)), token));
    }

    /// <summary>
    /// <para>请求设备执行 UFS reset；该命令独立于描述符文件，因此不需要先选配置。</para>
    /// Requests a device UFS reset; the command is independent of the descriptor file, so no profile
    /// selection is required first.
    /// </summary>
    private async void OnEdlUfsResetClick(object? sender, RoutedEventArgs e)
        => await RunEdlUfsOperationAsync("Flash_EdlUfsReset", "Flash_EdlUfsResetConfirm",
            (firehose, token) => EdlUfsProvisionService.ResetCoreAsync(firehose, confirmed: true,
                new Progress<FlashProgress>(update => AppendLog(update.Status)), token));

    /// <summary>
    /// <para>选择用于覆盖包内 programmer 的引导文件；覆盖只影响后续 EDL 刷写与回读。</para>
    /// Picks a programmer file overriding the package-supplied one for later EDL work.
    /// </summary>
    private async void OnEdlChooseProgrammerClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_EdlChooseProgrammer"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        _edlProgrammerOverride = File.Exists(path) ? path : null;
        EdlProgrammerPathText.Text = _edlProgrammerOverride is { } programmer
            ? Path.GetFileName(programmer)
            : string.Empty;
        ToolTip.SetTip(EdlProgrammerPathText, _edlProgrammerOverride ?? string.Empty);
        if (_edlProgrammerOverride is { } selected)
        {
            AppendLog(Path.GetFileName(selected));
        }
    }

    /// <summary>
    /// <para>回读所选 EDL 分区到用户目录；勾选生成 XML 时按回读结果写出 rawprogram 清单，
    /// 通用化选项把与磁盘末尾相邻的条目改写为 <c>NUM_DISK_SECTORS-</c> 相对起始扇区。</para>
    /// Reads the selected EDL partitions back into a user directory; with XML generation enabled,
    /// a rawprogram manifest is written from the results and the generalized option rewrites
    /// entries abutting the disk end as relative <c>NUM_DISK_SECTORS-</c> start sectors.
    /// </summary>
    private async void OnEdlReadbackClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var selected = _model.Partitions
            .Where(row => row.IsSelected && row.CanSelect)
            .ToArray();
        if (selected.Length == 0) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_EdlReadbackSelected"),
            AllowMultiple = false
        });
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } destination) return;
        var generateXml = EdlGenerateXmlBox.IsChecked == true;
        var generalized = generateXml && EdlGeneralizedXmlBox.IsChecked == true;
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        EdlReadbackButton.IsEnabled = false;
        CancelFlashButton.IsEnabled = true;
        try
        {
            var manifest = new List<EdlReadbackManifestEntry>();
            var sectorSize = 0L;
            var numDiskSectors = 0L;
            SetRowsQueued(selected);
            FlashPartitionEntry? readbackRow = null;
            var readbackProgress = new Progress<BackupProgress>(update =>
            {
                if (readbackRow is null)
                {
                    return;
                }

                readbackRow.Percent = Math.Clamp(update.Percent, 0, 100);
                UpdateOverall();
            });
            await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token).ConfigureAwait(true);
                if (generalized)
                {
                    numDiskSectors = (await firehose.GetStorageGeometryAsync(
                        SelectedEdlLun, cancellation.Token).ConfigureAwait(true)).SectorCount;
                }

                FirehoseGptSnapshot? deviceGpt = null;
                foreach (var row in selected)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var entry = _edlEntries.TryGetValue(row, out var manifestEntry)
                        ? manifestEntry
                        : EdlGptPartitionService.ResolveEntry(
                            deviceGpt ??= await firehose.ReadGptAsync(SelectedEdlLun,
                                cancellationToken: cancellation.Token).ConfigureAwait(true),
                            row.Name);
                    SetRowRunning(row, "Flash_Status_Flashing");
                    readbackRow = row;
                    var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;
                    var outputPath = Path.Combine(destination,
                        entry.Filename.Length > 0 ? entry.Filename : $"{displayName}.img");
                    var result = await BackupService.BackupEdlPartitionAsync(
                        edl, entry, outputPath, readbackProgress, cancellation.Token).ConfigureAwait(true);
                    AppendLog(result.Summary);
                    SetRowFinished(row, result.Success);
                    if (!result.Success) return false;
                    if (!generateXml) continue;
                    var geometry = await firehose.GetStorageGeometryAsync(
                        entry.PhysicalPartitionNumber, cancellation.Token).ConfigureAwait(true);
                    sectorSize = entry.SectorSizeInBytes > 0 ? entry.SectorSizeInBytes : geometry.SectorSize;
                    manifest.Add(new EdlReadbackManifestEntry
                    {
                        PartitionName = displayName,
                        StartSector = FlashService.ParseStartSectorValue(entry.StartSector, geometry.SectorCount),
                        SectorCount = entry.NumPartitionSectors,
                        PhysicalPartitionNumber = entry.PhysicalPartitionNumber,
                        FileName = Path.GetFileName(outputPath)
                    });
                }

                return true;
            }, cancellation.Token);
            if (generateXml && manifest.Count > 0)
            {
                var manifestPath = Path.Combine(destination, "rawprogram_readback.xml");
                await EdlReadbackManifestWriter.WriteAsync(
                    manifestPath, manifest, sectorSize, numDiskSectors, generalized,
                    cancellation.Token).ConfigureAwait(true);
                AppendLog(Path.GetFileName(manifestPath));
            }
        }
        catch (OperationCanceledException) { MarkUnfinishedSkipped(); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            SetBusy(false);
            CancelFlashButton.IsEnabled = false;
            UpdateOverall();
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>对已准备好的 MTK 计划做二次确认并执行：确认框给出操作数与总量，进度按步骤回写到对应行，
    /// 结束后统一补完未标记的行并写入结果摘要。刷写与擦除共用该流程，仅标题资源键不同。</para>
    /// Confirms and executes a prepared MTK plan: the dialog reports the operation count and total
    /// size, progress is written back to each row by step, and the run finishes every unmarked row
    /// and writes one result summary. Flashing and erasing share this flow and differ only in the
    /// title resource key.
    /// </summary>
    /// <param name="preparation">The prepared one-shot plan. <para>已准备好的一次性计划。</param>
    /// <param name="rows">Rows whose progress tracks the plan's steps. <para>进度对应计划各步骤的行。</param>
    /// <param name="owner">Window owning the confirmation dialog. <para>持有确认对话框的窗口。</param>
    /// <param name="token">Cancellation for the execution. <para>执行阶段的取消令牌。</param>
    /// <param name="titleKey">Resource key for the confirmation title. <para>确认标题的资源键。</param>
    private async Task ExecuteMtkPlanAsync(IMtkFlashPlanPreview preparation, IReadOnlyList<FlashPartitionEntry> rows,
        Window owner, CancellationToken token, string titleKey)
    {
        var language = LanguageService.Instance;
        _confirming = true;
        bool go;
        try
        {
            go = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                string.Format(language.GetString("Flash_MtkPlanPreview"), preparation.OperationCount,
                    preparation.TotalBytes / 1024d / 1024d));
        }
        finally { _confirming = false; }
        if (!go)
        {
            AppendLog(language.GetString("Flash_Canceled"));
            return;
        }

        var progress = new Progress<MtkFlashPlanProgress>(update =>
        {
            if (update.StepIndex < 0 || update.StepIndex >= rows.Count)
            {
                return;
            }

            var row = rows[update.StepIndex];
            row.State = FlashRowState.Running;
            row.Percent = update.TotalBytes > 0
                ? (int)Math.Clamp(update.ProcessedBytes * 100 / update.TotalBytes, 0, 100)
                : 0;
            row.StatusText = update.Phase switch
            {
                MtkFlashPhase.Writing => language.GetString("Flash_Status_Flashing"),
                MtkFlashPhase.Erasing => language.GetString("Flash_Status_Erasing"),
                MtkFlashPhase.Verifying => language.GetString("Flash_Status_Verifying"),
                MtkFlashPhase.OperationCompleted => language.GetString("Flash_Status_Succeeded"),
                _ => row.StatusText
            };
            if (update.Phase == MtkFlashPhase.OperationCompleted)
            {
                row.State = FlashRowState.Succeeded;
                row.Percent = 100;
            }

            UpdateOverall();
        });
        var result = await preparation.ExecuteAsync(progress, 120000, token);
        foreach (var row in rows.Where(row => row.State != FlashRowState.Succeeded))
        {
            SetRowFinished(row, success: true);
        }

        AppendLog(string.Format(language.GetString("Flash_MtkResultSummary"),
                result.CompletedOperations, result.WrittenBytes / 1024d / 1024d) +
            (result.VerifiedBytes > 0
                ? string.Format(language.GetString("Flash_MtkResultVerified"),
                    result.VerifiedBytes / 1024d / 1024d)
                : string.Empty));
    }

    /// <summary>
    /// <para>擦除所选 scatter 分区：既覆盖有镜像的可刷写行，也覆盖无镜像但策略允许的行；范围取清单声明
    /// 的容量并由实机 GPT 复核。擦除不可恢复，执行前需用户确认，串口与 USB 共用同一套连接选择。</para>
    /// Erases the selected scatter partitions: it covers both flashable rows that carry an image and
    /// policy-eligible rows without one; the range is the manifest-declared capacity and the live GPT
    /// re-checks it. Erasure is irreversible, so the user must confirm first, and both transports share
    /// the same connection selection.
    /// </summary>
    private async void OnMtkEraseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _mtkResolution is not { } resolution ||
            TopLevel.GetTopLevel(this) is not Window owner ||
            !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal)) return;
        var selected = _model.Partitions
            .Where(row => row.IsSelected && row.CanSelect && _mtkPartitions.ContainsKey(row)).ToArray();
        if (selected.Length == 0) return;
        var language = LanguageService.Instance;
        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        if (selected.Length > 256)
        {
            AppendLog(language.GetString("Flash_MtkTooManySteps"));
            return;
        }

        var kind = SelectedMtkStorageKind;
        if (BuildMtkFamilyPlan(erase: true, selected, verify: false) is not { } plan)
        {
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkErase"),
                $"{language.GetString("Flash_MtkEraseConfirm")}{Environment.NewLine}" +
                $"{resolution.StorageLabel} · {string.Join(", ", selected.Select(row => row.Name))}");
        }
        finally { _confirming = false; }
        if (!confirmed ||
            selected.Any(row => !row.IsSelected || !_model.Partitions.Contains(row)) ||
            !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        MtkFlashButton.IsEnabled = false;
        FlashSelectedButton.IsEnabled = false;
        PartitionGrid.IsEnabled = false;
        PackagePathBox.IsEnabled = false;
        ChooseFileButton.IsEnabled = false;
        ChooseFolderButton.IsEnabled = false;
        SetRowsQueued(selected);
        IMtkFlashPlanPreview? preparation = null;
        try
        {
            var physicalPartition = plan.PhysicalPartition;
            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                preparation = plan.Family switch
                {
                    MtkDaProtocol.Legacy => await MtkFlashService.PrepareLegacyAsync(connect, plan.LegacyRegion,
                        plan.LegacySteps, cancellation.Token),
                    MtkDaProtocol.Xml => await MtkFlashService.PrepareXmlAsync(connect, plan.XmlRegion!,
                        plan.XmlSteps, cancellation.Token),
                    _ => await MtkFlashService.PrepareAsync(connect, physicalPartition, plan.XFlashEraseSteps,
                        cancellation.Token)
                };
                await ExecuteMtkPlanAsync(preparation, selected, owner, cancellation.Token, "Flash_MtkErase");
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    preparation = plan.Family switch
                    {
                        MtkDaProtocol.Legacy => await MtkFlashService.PrepareLegacyOnSessionAsync(session,
                            matched, plan.LegacyRegion, plan.LegacySteps, cancellation.Token),
                        MtkDaProtocol.Xml => await MtkFlashService.PrepareXmlOnSessionAsync(session,
                            matched, plan.XmlRegion!, plan.XmlSteps, cancellation.Token),
                        _ => await MtkFlashService.PrepareOnSessionAsync(session, plan.Family,
                            matched, physicalPartition, plan.XFlashEraseSteps, cancellation.Token)
                    };
                    await ExecuteMtkPlanAsync(preparation, selected, owner, cancellation.Token, "Flash_MtkErase");
                    return true;
                }, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            foreach (var row in selected.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            if (preparation is not null)
            {
                try { preparation.Dispose(); }
                catch (Exception ex) { AppendLog(ex.Message); }
            }

            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            FlashSelectedButton.IsEnabled = true;
            PartitionGrid.IsEnabled = true;
            PackagePathBox.IsEnabled = true;
            ChooseFileButton.IsEnabled = true;
            ChooseFolderButton.IsEnabled = true;
            UpdateEdlButton();
        }
    }

    private async void OnMtkFlashClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _mtkResolution is not { } resolution ||
            TopLevel.GetTopLevel(this) is not Window owner ||
            !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal)) return;
        var selected = _model.Partitions
            .Where(row => row.IsSelected && row.CanSelect && _mtkRows.ContainsKey(row)).ToArray();
        if (selected.Length == 0) return;
        var language = LanguageService.Instance;
        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        if (selected.Length > 256)
        {
            AppendLog(language.GetString("Flash_MtkTooManySteps"));
            return;
        }

        var kind = SelectedMtkStorageKind;
        var verify = VerifyAfterFlashBox.IsChecked == true;
        if (BuildMtkFamilyPlan(erase: false, selected, verify) is not { } plan)
        {
            return;
        }

        var target = useSerial ? port! : usbDevice!.DisplayName;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkFlash"),
                $"{resolution.StorageLabel} · {target} · {string.Join(", ", selected.Select(row => row.Name))}");
        }
        finally { _confirming = false; }
        if (!confirmed ||
            selected.Any(row => !row.IsSelected || !_model.Partitions.Contains(row)) ||
            !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        MtkFlashButton.IsEnabled = false;
        FlashSelectedButton.IsEnabled = false;
        PartitionGrid.IsEnabled = false;
        PackagePathBox.IsEnabled = false;
        ChooseFileButton.IsEnabled = false;
        ChooseFolderButton.IsEnabled = false;
        SetRowsQueued(selected);
        IMtkFlashPlanPreview? preparation = null;
        try
        {
            var physicalPartition = plan.PhysicalPartition;
            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                preparation = plan.Family switch
                {
                    MtkDaProtocol.Legacy => await MtkFlashService.PrepareLegacyAsync(connect, plan.LegacyRegion,
                        plan.LegacySteps, cancellation.Token),
                    MtkDaProtocol.Xml => await MtkFlashService.PrepareXmlAsync(connect, plan.XmlRegion!,
                        plan.XmlSteps, cancellation.Token),
                    _ => await MtkFlashService.PrepareScatterAsync(connect,
                        physicalPartition, resolution.Storage, plan.ScatterSteps, cancellation.Token)
                };
                await ExecuteMtkPlanAsync(preparation, selected, owner, cancellation.Token, "Flash_MtkFlash");
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    preparation = plan.Family switch
                    {
                        MtkDaProtocol.Legacy => await MtkFlashService.PrepareLegacyOnSessionAsync(session,
                            matched, plan.LegacyRegion, plan.LegacySteps, cancellation.Token),
                        MtkDaProtocol.Xml => await MtkFlashService.PrepareXmlOnSessionAsync(session,
                            matched, plan.XmlRegion!, plan.XmlSteps, cancellation.Token),
                        _ => await MtkFlashService.PrepareScatterOnSessionAsync(session,
                            plan.Family, matched, physicalPartition, resolution.Storage, plan.ScatterSteps,
                            cancellation.Token)
                    };
                    await ExecuteMtkPlanAsync(preparation, selected, owner, cancellation.Token, "Flash_MtkFlash");
                    return true;
                }, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            foreach (var row in selected.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            if (preparation is not null)
            {
                try { preparation.Dispose(); }
                catch (Exception ex) { AppendLog(ex.Message); }
            }

            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            FlashSelectedButton.IsEnabled = true;
            PartitionGrid.IsEnabled = true;
            PackagePathBox.IsEnabled = true;
            ChooseFileButton.IsEnabled = true;
            ChooseFolderButton.IsEnabled = true;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>读取 MTK 面板当前选择并按顺序校验；失败时把本地化原因写入日志并返回 false。
    /// 家族与存储类型的不可能组合排在最前，因为用户不必为一个所选代理永远无法服务的请求去找加载器、
    /// 串口和波特率；XML 家族不使用 EMI 参数，通过校验时只写一条本地化提示，不做静默丢弃。</para>
    /// Reads the current MTK pane selection and validates it in order; on failure the localized
    /// reason is written to the log and <see langword="false"/> is returned. An impossible family and
    /// storage combination is reported first, because the user should not have to hunt for a loader, a
    /// port and a baud rate for a request the chosen agent can never serve. The XML family uses no EMI
    /// parameters, so a passing selection only writes one localized note instead of dropping them quietly.
    /// </summary>
    /// <param name="useSerial">Whether the operation targets a serial port instead of a USB device.
    /// <para>操作目标是串口而非 USB 设备。</param>
    /// <param name="usbDevice">The selected USB MTK device, or null. <para>所选 USB MTK 设备，或为 null。</param>
    /// <param name="port">The selected serial port, or null. <para>所选串口，或为 null。</param>
    /// <param name="baudRate">The selected baud rate; 0 without a serial target. <para>所选波特率；非串口目标时为 0。</param>
    /// <param name="attach">Whether a running agent should be attached instead of booting one.
    /// <para>是否改为附加已运行的代理而非启动新代理。</param>
    /// <param name="autoMatch">Whether the loader entry is matched from the reported identity.
    /// <para>是否按上报身份匹配加载器条目。</param>
    /// <returns><see langword="true"/> when the selection is usable. <para>选择可用时为 true。</returns>
    internal bool TryReadMtkSelection(out bool useSerial, out DetectedDevice? usbDevice, out string? port,
        out int baudRate, out bool attach, out bool autoMatch)
    {
        var language = LanguageService.Instance;
        usbDevice = SelectedDevice is { Protocol: DeviceProtocolKind.Mtk, Transport: DeviceTransportKind.Usb }
            ? SelectedDevice
            : null;
        useSerial = usbDevice is null;
        port = null;
        baudRate = 0;
        attach = MtkAttachAgentBox.IsChecked == true;
        autoMatch = MtkAutoMatchBox.IsChecked == true;

        string? failure = null;
        if (SelectedMtkProtocol == MtkDaProtocol.Legacy && SelectedMtkStorageKind != MtkXFlashStorageKind.Emmc)
        {
            failure = "Flash_MtkLegacyStorageUnsupported";
        }

        if (failure is null && useSerial)
        {
            if (MtkPortBox.SelectedItem is not string selectedPort)
            {
                failure = "Flash_MtkPortRequired";
            }
            else
            {
                port = selectedPort;
            }
        }

        if (failure is null && !attach && _mtkLoaderPath is not { })
        {
            failure = "Flash_MtkDaRequired";
        }

        if (failure is null && !attach && !autoMatch &&
            (_mtkLoaderEntries.Count == 0 || MtkEntryIndexBox.SelectedIndex < 0))
        {
            failure = "Flash_MtkEntryRequired";
        }

        if (failure is null && useSerial)
        {
            if (MtkBaudBox.SelectedItem is not int selectedBaud)
            {
                failure = "Flash_MtkBaudRequired";
            }
            else
            {
                baudRate = selectedBaud;
            }
        }
        else if (failure is null && attach)
        {
            failure = "Flash_MtkUsbAttachUnsupported";
        }
        else if (failure is null && !autoMatch)
        {
            failure = "Flash_MtkUsbAutoMatchRequired";
        }

        if (failure is null && attach && SelectedMtkProtocol != MtkDaProtocol.XFlash)
        {
            failure = "Flash_MtkAttachFamilyUnsupported";
        }

        if (failure is null)
        {
            if (SelectedMtkProtocol == MtkDaProtocol.Xml && _mtkEmi is not null)
            {
                AppendLog(language.GetString("Flash_MtkXmlEmiIgnored"));
            }

            return true;
        }

        AppendLog(language.GetString(failure));
        return false;
    }

    /// <summary>
    /// <para>下载代理家族由选择框决定，不做探测：XFlash 用于新芯片，Legacy 用于旧芯片，XML 用于按区域名
    /// 寻址的 v6 代理。</para>
    /// The download-agent family is taken from the selector and never probed: XFlash for current chips, Legacy
    /// for legacy chips and XML for the region-named v6 agent.
    /// </summary>
    /// <value>The explicitly selected protocol. <para>显式选定的协议。</para></value>
    private MtkDaProtocol SelectedMtkProtocol => MtkProtocolBox.SelectedIndex switch
    {
        1 => MtkDaProtocol.Legacy,
        2 => MtkDaProtocol.Xml,
        _ => MtkDaProtocol.XFlash
    };

    /// <summary>
    /// <para>存储类型选择框对应的存储族；只有 eMMC 与 UFS 两种，NOR/NAND 不在此面板之内。</para>
    /// The storage family behind the storage selection; only eMMC and UFS are offered, NOR and NAND are
    /// outside this pane.
    /// </summary>
    /// <value>The selected storage family. <para>所选存储族。</para></value>
    private MtkXFlashStorageKind SelectedMtkStorageKind => MtkStorageKindBox.SelectedIndex switch
    {
        1 => MtkXFlashStorageKind.Ufs,
        2 => MtkXFlashStorageKind.Nor,
        3 => MtkXFlashStorageKind.Nand,
        _ => MtkXFlashStorageKind.Emmc
    };

    /// <summary>
    /// <para>XML 代理的 DRAM 资源走它自己的主机文件通道，不接受 XFlash 的 EMI 参数，因此家族为 XML 时
    /// 主机必须丢弃面板上残留的 EMI 选择，否则 Comm 会在启动中途拒绝该配置。</para>
    /// The XML agent takes its DRAM resources over its own host-file channel and rejects XFlash EMI
    /// parameters, so the host must drop a leftover EMI selection for the XML family; otherwise Comm
    /// refuses the startup configuration halfway through.
    /// </summary>
    /// <param name="family">The selected download-agent family. <para>所选下载代理家族。</param>
    /// <returns><see langword="true"/> when the family consumes EMI parameters.
    /// <para>家族使用 EMI 参数时为 true。</returns>
    internal static bool FamilyUsesEmiParameters(MtkDaProtocol family) => family != MtkDaProtocol.Xml;

    /// <summary>
    /// <para>电源请求的家族守卫：XML 代理只定义启动模式重启，没有关机请求，因此关机必须在接触设备之前
    /// 以本地化文案拒绝，返回拒绝用的资源键；家族可以服务该请求时返回 null。</para>
    /// The family guard for a power request: the XML agent defines boot-mode reboot only, so a power-off
    /// must be refused with a localized message before any device contact, returning the refusal resource
    /// key; <see langword="null"/> is returned when the family serves the request.
    /// </summary>
    /// <param name="family">The selected download-agent family. <para>所选下载代理家族。</param>
    /// <param name="mode">The requested power or boot transition. <para>请求的电源或启动模式切换。</param>
    /// <returns>The refusal resource key, or <see langword="null"/>. <para>拒绝用的资源键，或为 null。</returns>
    internal static string? MtkPowerRequestRefusalKey(MtkDaProtocol family, MtkXFlashShutdownMode mode)
        => family == MtkDaProtocol.Xml && mode == MtkXFlashShutdownMode.PowerOff
            ? "Flash_MtkXmlPowerOffUnsupported"
            : null;

    /// <summary>
    /// <para>把面板选择的下载代理家族落成一份不接触设备的执行选择：按家族构造步骤数组与区域标识，
    /// XFlash 用清单索引寻址，Legacy 与 XML 用 GPT 分区名寻址各自的区域。刷写与擦除共用该入口，
    /// 只在步骤种类上不同；构造失败时把原因写入日志并返回 null，绝不回退到其他家族。</para>
    /// Materializes the download-agent family chosen on the pane into one device-free execution selection:
    /// the step arrays and region identifiers are built per family, with XFlash addressed through manifest
    /// index tokens and Legacy and XML through GPT partition names in their own regions. Flashing and
    /// erasing share this entry and differ only in the step kind; a build failure writes the reason to the
    /// log and returns <see langword="null"/> and never falls back to another family.
    /// </summary>
    /// <param name="erase">Whether the selection erases instead of writing. <para>选择是擦除而非写入。</param>
    /// <param name="selected">The rows the caller has selected. <para>调用方所选的行。</param>
    /// <param name="verify">Whether written extents require readback. <para>写入范围是否要求回读。</param>
    /// <returns>The family selection, or <see langword="null"/> after the failure was logged.
    /// <para>家族选择；失败并已写日志时为 null。</returns>
    internal MtkFamilyPlan? BuildMtkFamilyPlan(bool erase, IReadOnlyList<FlashPartitionEntry> selected, bool verify)
    {
        var family = SelectedMtkProtocol;
        var kind = SelectedMtkStorageKind;
        if (_mtkResolution is not { } resolution)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkLoadScatterHint"));
            return null;
        }

        try
        {
            MtkScatterFlashStep[] scatterSteps = [];
            MtkPartitionFlashStep[] xFlashEraseSteps = [];
            MtkLegacyPartitionFlashStep[] legacySteps = [];
            MtkXmlPartitionFlashStep[] xmlSteps = [];
            switch (family)
            {
                case MtkDaProtocol.Legacy:
                    legacySteps = [.. selected.Select(row => erase
                        ? MtkScatterPackage.CreateLegacyEraseStep(_mtkPartitions[row])
                        : MtkScatterPackage.CreateLegacyStep(resolution, _mtkRows[row], verify))];
                    break;
                case MtkDaProtocol.Xml:
                    xmlSteps = [.. selected.Select(row => erase
                        ? MtkScatterPackage.CreateXmlEraseStep(resolution, _mtkPartitions[row])
                        : MtkScatterPackage.CreateXmlStep(resolution, _mtkRows[row], verify))];
                    break;
                default:
                    if (erase)
                    {
                        xFlashEraseSteps = [.. selected.Select(row =>
                            MtkScatterPackage.CreateEraseStep(_mtkPartitions[row]))];
                    }
                    else
                    {
                        scatterSteps = [.. selected.Select(row =>
                            MtkScatterPackage.CreateStep(resolution, _mtkRows[row], verify))];
                    }

                    break;
            }

            return new MtkFamilyPlan
            {
                Family = family,
                PhysicalPartition = MtkScatterPackage.GetPhysicalPartition(kind),
                LegacyRegion = family == MtkDaProtocol.Legacy
                    ? MtkScatterPackage.GetLegacyPhysicalPartition(kind)
                    : (byte)0,
                XmlRegion = family == MtkDaProtocol.Xml ? MtkScatterPackage.GetXmlRegionName(kind) : null,
                ScatterSteps = scatterSteps,
                XFlashEraseSteps = xFlashEraseSteps,
                LegacySteps = legacySteps,
                XmlSteps = xmlSteps
            };
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// <para>面板家族选择的不可变结果：三种步骤数组中只有与所选家族对应的一组非空，区域标识同样按家族填充，
    /// 因此后续的转发切换只需读取这一份数据，不必重复推导。</para>
    /// The immutable result of the pane's family selection: only the step array matching the selected
    /// family is populated and the region identifiers are filled per family as well, so the forwarding
    /// switch afterwards reads this one record instead of deriving the same choices again.
    /// </summary>
    internal sealed record MtkFamilyPlan
    {
        /// <summary>Gets the family the selector resolved to. <para>选择框解析出的家族。</para></summary>
        public required MtkDaProtocol Family { get; init; }

        /// <summary>Gets the XFlash physical user region. <para>XFlash 物理用户区。</para></summary>
        public required uint PhysicalPartition { get; init; }

        /// <summary>Gets the Legacy region identifier, zero for the other families.
        /// <para>Legacy 区域标识符；其他家族为 0。</para></summary>
        public required byte LegacyRegion { get; init; }

        /// <summary>Gets the XML region token, null for the other families.
        /// <para>XML 区域标记；其他家族为 null。</para></summary>
        public required string? XmlRegion { get; init; }

        /// <summary>Gets the scatter-indexed XFlash write steps. <para>按索引寻址的 XFlash 写入步骤。</para></summary>
        public required IReadOnlyList<MtkScatterFlashStep> ScatterSteps { get; init; }

        /// <summary>Gets the GPT-named XFlash erase steps. <para>按 GPT 分区名寻址的 XFlash 擦除步骤。</para></summary>
        public required IReadOnlyList<MtkPartitionFlashStep> XFlashEraseSteps { get; init; }

        /// <summary>Gets the Legacy named steps. <para>Legacy 命名步骤。</para></summary>
        public required IReadOnlyList<MtkLegacyPartitionFlashStep> LegacySteps { get; init; }

        /// <summary>Gets the XML named steps. <para>XML 命名步骤。</para></summary>
        public required IReadOnlyList<MtkXmlPartitionFlashStep> XmlSteps { get; init; }
    }

    /// <summary>
    /// <para>为所选家族补齐启动设置：Legacy 用主机操作系统与默认包长且不预设 DA1 响应断言，XML 用参考工具的
    /// 默认运行时策略且不预设属性信封名；XML 家族不接受 EMI 参数，因此这里把它清空，避免启动中途被拒绝。</para>
    /// Completes the startup settings for the selected family: Legacy uses the host operating system and
    /// default packet sizes without a DA1 response assertion, and XML uses the reference tool's default runtime
    /// policy without a preset property envelope. The XML family takes no EMI parameters, so they are cleared
    /// here instead of being rejected halfway through startup.
    /// </summary>
    /// <param name="startup">The identity-matched startup to extend. <para>待补充的身份匹配启动配置。</param>
    /// <returns>The startup configuration for the selected family. <para>所选家族的启动配置。</returns>
    private MtkAutoStartup ApplyMtkFamily(MtkAutoStartup startup)
    {
        var family = SelectedMtkProtocol;
        var emi = FamilyUsesEmiParameters(family) ? startup.EmiParameters : null;
        return family switch
        {
            MtkDaProtocol.Legacy => startup with { Legacy = BuildLegacyOptions(), EmiParameters = emi },
            MtkDaProtocol.Xml => startup with { Xml = BuildXmlStartupOptions(), EmiParameters = emi },
            _ => startup with { EmiParameters = emi }
        };
    }

    private static MtkLegacyOptions BuildLegacyOptions() => new()
    {
        HostOs = MtkHostOs,
        StageOneResponseHex = null
    };

    private static MtkXmlBootstrapOptions BuildXmlStartupOptions() => new()
    {
        Runtime = new MtkXmlRuntimeOptions
        {
            Checksum = MtkXmlChecksumLevel.None,
            HostOs = MtkHostOs,
            InitializeDram = true
        },
        SlaEnvelopeName = null,
        HostInfo = string.Empty
    };

    private static MtkXmlHostOs MtkHostOs => OperatingSystem.IsWindows()
        ? MtkXmlHostOs.Windows
        : MtkXmlHostOs.Linux;

    /// <summary>
    /// <para>按面板选择构造串口 MTK 连接器：手动条目走显式启动配置，自动匹配按身份选条目，
    /// 附加模式直接挂到已运行的代理。</para>
    /// Builds the serial MTK connector from the pane selection: counted entries use an explicit
    /// startup configuration, auto-match picks the entry by identity, and attach mode connects to an
    /// agent that is already running.
    /// </summary>
    /// <param name="port">The selected serial port. <para>所选串口。</param>
    /// <param name="baudRate">The selected baud rate. <para>所选波特率。</param>
    /// <param name="kind">The storage family the agent must serve. <para>代理需服务的存储族。</param>
    /// <param name="attach">Whether to attach instead of booting an agent. <para>是否改为附加而非启动代理。</param>
    /// <param name="autoMatch">Whether the entry is matched from the reported identity.
    /// <para>是否按上报身份匹配条目。</param>
    /// <returns>A connector returning a storage-ready session. <para>返回存储就绪会话的连接器。</returns>
    private Func<CancellationToken, Task<MtkSession>> BuildMtkSerialConnector(string port, int baudRate,
        MtkXFlashStorageKind kind, bool attach, bool autoMatch)
    {
        async Task<MtkSession> StartAgentAsync(CancellationToken token)
        {
            var serial = new SerialConnectionOptions
            {
                PortName = port,
                BaudRate = baudRate,
                Handshake = Handshake.None
            };
            if (!autoMatch)
            {
                var entry = _mtkLoaderEntries[MtkEntryIndexBox.SelectedIndex];
                var protocol = SelectedMtkProtocol;
                var startup = new MtkBootstrapOptions
                {
                    LoaderPath = _mtkLoaderPath!,
                    EntryIndex = entry.Index,
                    StageOneRegionIndex = entry.StageOneRegion,
                    StageTwoRegionIndex = entry.StageTwoRegion,
                    StageTwoContent = protocol == MtkDaProtocol.Legacy
                        ? MtkDaRegionContent.Complete
                        : MtkDaRegionContent.WithoutSignatureSuffix,
                    StorageKind = kind,
                    EmiParameters = FamilyUsesEmiParameters(protocol) ? _mtkEmi : null,
                    SlaKeyProvider = _mtkKeyDirectory is { } manualKeys
                        ? new MtkDirectoryKeyStore(manualKeys)
                        : null,
                    BootResourceProvider = BuildMtkBootResourceProvider()
                };
                if (protocol == MtkDaProtocol.Legacy)
                {
                    startup = startup with { Legacy = BuildLegacyOptions() };
                }
                else if (protocol == MtkDaProtocol.Xml)
                {
                    startup = startup with { Xml = BuildXmlStartupOptions() };
                }
                return await MtkBootstrap.ConnectSerialAsync(serial, protocol, startup, token);
            }

            var matched = ApplyMtkFamily(await BuildMtkAutoStartupAsync(kind, token));
            return await MtkBootstrap.MatchSerialAsync(serial, SelectedMtkProtocol, matched, token);
        }

        async Task<MtkSession> AttachAgentAsync(CancellationToken token)
        {
            var session = await MtkSession.OpenSerialAsync(port, baudRate, token);
            try
            {
                await session.AttachRunningXFlashAgentAsync(kind, 120000, token);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        return attach ? AttachAgentAsync : StartAgentAsync;
    }

    /// <summary>
    /// <para>按面板的 DA 文件与密钥目录构造身份匹配启动配置；密钥目录同时提供 SLA 应答与
    /// DAA 资源。</para>
    /// Builds the identity-matched startup configuration from the pane's DA file and key directory;
    /// the key directory supplies both the SLA responder and the DAA resources.
    /// </summary>
    /// <param name="kind">The storage family the agent must serve. <para>代理需服务的存储族。</param>
    /// <param name="token">Cancellation for loading the loader profile. <para>加载加载器配置的取消令牌。</param>
    /// <returns>The startup configuration. <para>启动配置。</returns>
    private async Task<MtkAutoStartup> BuildMtkAutoStartupAsync(MtkXFlashStorageKind kind, CancellationToken token)
    {
        var profile = await MtkLoaderProfile.FromFileAsync(_mtkLoaderPath!, token);
        return ApplyMtkFamily(new MtkAutoStartup
        {
            Profile = profile,
            StorageKind = kind,
            EmiParameters = _mtkEmi,
            SlaKeyProvider = _mtkKeyDirectory is { } keys ? new MtkDirectoryKeyStore(keys) : null,
            BootResourceProvider = BuildMtkBootResourceProvider()
        });
    }

    /// <summary>
    /// <para>构造 DAA 启动资源提供方：显式认证文件按上游 --auth 约定优先，否则回退到密钥目录。</para>
    /// Builds the DAA boot resource provider: the explicit auth file wins per the upstream --auth
    /// convention, otherwise the key directory is used.
    /// </summary>
    /// <returns>The provider, or null when nothing was selected.
    /// <para>资源提供方；未选择任何内容时为 null。</para></returns>
    private IMtkBootResourceProvider? BuildMtkBootResourceProvider()
        => _mtkAuthFile is { } authFile
            ? new MtkFileBootResourceProvider(authenticationPath: authFile)
            : _mtkKeyDirectory is { } directory
                ? new MtkDirectoryKeyStore(directory)
                : null;

    /// <summary>
    /// <para>把所选 scatter 分区回读到用户目录；与刷写共用本面板的连接选择，并逐行显示字节进度。</para>
    /// Reads the selected scatter partitions into a user-chosen directory; it shares this pane's
    /// connection selection with flashing and shows byte progress per row.
    /// </summary>
    private async void OnMtkReadbackClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _mtkResolution is not { } resolution ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var selected = _model.Partitions
            .Where(row => row.IsSelected && row.CanSelect && _mtkRows.ContainsKey(row))
            .ToArray();
        if (selected.Length == 0) return;
        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        var language = LanguageService.Instance;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = language.GetString("Flash_MtkReadback"),
            AllowMultiple = false
        });
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } destination) return;

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkReadback"),
                $"{resolution.StorageLabel} · {string.Join(", ", selected.Select(row => row.Name))}");
        }
        finally { _confirming = false; }
        if (!confirmed || selected.Any(row => !row.IsSelected || !_model.Partitions.Contains(row)) ||
            !string.Equals(_model.PackagePath, _loadedPath, StringComparison.Ordinal)) return;

        var kind = SelectedMtkStorageKind;
        var physicalPartition = MtkScatterPackage.GetPhysicalPartition(kind);
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        MtkFlashButton.IsEnabled = false;
        PartitionGrid.IsEnabled = false;
        PackagePathBox.IsEnabled = false;
        SetRowsQueued(selected);
        try
        {
            async Task ReadBackAsync(MtkSession session, CancellationToken token)
            {
                var family = SelectedMtkProtocol;
                var legacySnapshot = family == MtkDaProtocol.Legacy
                    ? await session.ReadLegacyGptAsync(MtkScatterPackage.GetLegacyPhysicalPartition(kind), token)
                    : null;
                var xmlSnapshot = family == MtkDaProtocol.Xml
                    ? await session.ReadXmlGptAsync(MtkScatterPackage.GetXmlRegionName(kind), cancellationToken: token)
                    : null;
                var xFlashSnapshot = family == MtkDaProtocol.XFlash
                    ? await session.ReadXFlashGptAsync(physicalPartition, token)
                    : null;
                var table = legacySnapshot?.Table ?? xmlSnapshot?.Table ?? xFlashSnapshot!.Table;
                foreach (var row in selected)
                {
                    token.ThrowIfCancellationRequested();
                    var partition = table.FindPartition(row.Name);
                    SetRowRunning(row, "Flash_Status_Flashing");
                    var progress = new Progress<long>(value =>
                    {
                        row.Percent = partition.ByteLength > 0
                            ? (int)Math.Clamp(value * 100L / partition.ByteLength, 0L, 100L)
                            : 0;
                        UpdateOverall();
                    });
                    var outputPath = Path.Combine(destination,
                        row.FileName.Length > 0 ? row.FileName : $"{row.Name}.img");
                    if (legacySnapshot is not null)
                    {
                        await session.ReadLegacyPartitionToFileAsync(legacySnapshot, row.Name, outputPath,
                            progress, token);
                    }
                    else if (xmlSnapshot is not null)
                    {
                        await session.ReadXmlPartitionToFileAsync(xmlSnapshot, row.Name, outputPath,
                            progress, token);
                    }
                    else
                    {
                        await session.ReadXFlashPartitionToFileAsync(xFlashSnapshot!, row.Name, outputPath,
                            progress, token);
                    }
                    AppendLog(outputPath);
                    SetRowFinished(row, success: true);
                }
            }

            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                using var session = await connect(cancellation.Token);
                await ReadBackAsync(session, cancellation.Token);
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched,
                        cancellation.Token);
                    await ReadBackAsync(ready, cancellation.Token);
                    return true;
                }, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            foreach (var row in selected.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            FlashSelectedButton.IsEnabled = true;
            PartitionGrid.IsEnabled = true;
            PackagePathBox.IsEnabled = true;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>启动到存储就绪的代理后读取 GPT 并把分区表写入日志；串口与 USB 两条路径复用本面板的
    /// 连接选择，与刷写/回读共用同一套校验与取消。</para>
    /// Boots an agent to storage readiness, reads the GPT and writes the partition table to the log;
    /// both the serial and USB paths reuse this pane's connection selection and share the same
    /// validation, confirmation and cancellation as flashing and read-back.
    /// </summary>
    private async void OnMtkShowGptClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner ||
            !TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        var language = LanguageService.Instance;
        var kind = SelectedMtkStorageKind;
        var family = SelectedMtkProtocol;
        var physicalPartition = MtkScatterPackage.GetPhysicalPartition(kind);
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner,
                language.GetString("Flash_MtkShowGpt"), language.GetString("Flash_MtkShowGptConfirm"));
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        MtkFlashButton.IsEnabled = false;
        try
        {
            async Task ShowGptAsync(MtkSession session, CancellationToken token)
            {
                AppendPartitionTable(family switch
                {
                    MtkDaProtocol.Legacy => (await session.ReadLegacyGptAsync(
                        MtkScatterPackage.GetLegacyPhysicalPartition(kind), token)).Table.Partitions,
                    MtkDaProtocol.Xml => (await session.ReadXmlGptAsync(
                        MtkScatterPackage.GetXmlRegionName(kind), cancellationToken: token)).Table.Partitions,
                    _ => (await session.ReadXFlashGptAsync(physicalPartition, token)).Table.Partitions
                });
            }

            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                using var session = await connect(cancellation.Token);
                await ShowGptAsync(session, cancellation.Token);
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched,
                        cancellation.Token);
                    await ShowGptAsync(ready, cancellation.Token);
                    return true;
                }, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            UpdateEdlButton();
        }
    }


    /// <summary>
    /// <para>授权开关改变后重算利用控件的可用性；未勾选时绕过按钮与两个选择框都保持禁用。</para>
    /// Recomputes the exploit controls whenever the authorization switch changes; with it unchecked the
    /// bypass button and both selectors stay disabled.
    /// </summary>
    private void OnMtkExploitAuthorizationClick(object? sender, RoutedEventArgs e) => UpdateEdlButton();

    /// <summary>
    /// <para>把家族选择框的索引映射为显式利用家族，第 0 项表示交由芯片表决定。</para>
    /// Maps the family selector index onto an explicit exploit family, where index 0 leaves the choice to
    /// the chip table.
    /// </summary>
    /// <param name="selectedIndex">The selector index. <para>选择框索引。</para></param>
    /// <returns>The named family, or <see langword="null"/> for the chip-table default.
    /// <para>指名的家族；按芯片表选择时为 <see langword="null"/>。</para></returns>
    internal static MtkExploitFamily? ReadExploitFamily(int selectedIndex) =>
        selectedIndex <= 0 ? null : Enum.GetValues<MtkExploitFamily>()[selectedIndex - 1];

    /// <summary>
    /// <para>解析 RPMB 的偏移与长度文本：帧形态映射到帧范围，字节形态映射到字节区间，整区形态忽略两者；
    /// 文本非法或数值越界时返回 false。</para>
    /// Parses the RPMB offset and length texts: the frame shape maps onto a frame range, the byte shape
    /// onto a byte span, and the whole-area shape ignores both; malformed or out-of-bounds text returns
    /// false.
    /// </summary>
    /// <param name="mode">The addressing shape. <para>寻址形态。</para></param>
    /// <param name="offsetText">The offset text. <para>偏移文本。</para></param>
    /// <param name="lengthText">The length text. <para>长度文本。</para></param>
    /// <param name="frames">The parsed frame range. <para>解析出的帧范围。</para></param>
    /// <param name="byteOffset">The parsed byte offset. <para>解析出的字节偏移。</para></param>
    /// <param name="byteLength">The parsed byte length. <para>解析出的字节长度。</para></param>
    /// <returns><see langword="true"/> when the shape is usable. <para>形态可用时为
    /// <see langword="true"/>。</para></returns>
    internal static bool TryReadRpmbShape(MtkRpmbCaptureMode mode, string? offsetText, string? lengthText,
        out MtkRpmbFrameRange frames, out long byteOffset, out long byteLength)
    {
        frames = new MtkRpmbFrameRange(0, 1);
        byteOffset = 0;
        byteLength = 0;
        if (mode == MtkRpmbCaptureMode.FullArea)
        {
            return true;
        }

        if (!long.TryParse(offsetText?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var offset) ||
            !long.TryParse(lengthText?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var length) ||
            offset < 0 || length <= 0)
        {
            return false;
        }

        if (mode == MtkRpmbCaptureMode.Frames)
        {
            if (length > int.MaxValue)
            {
                return false;
            }

            frames = new MtkRpmbFrameRange(offset, (int)length);
        }
        else
        {
            byteOffset = offset;
            byteLength = length;
        }

        return true;
    }

    /// <summary>
    /// <para>在用户逐项授权后运行 BootROM 绕过：串口路径没有 BootROM 控制面，未勾选授权或家族未经实现时
    /// 都在本地拒绝，不接触设备。</para>
    /// Runs the BootROM bypass only under explicit per-item authorization: the serial path has no BootROM
    /// control surface, and a missing grant or an unimplemented family is refused locally without any
    /// device contact.
    /// </summary>
    private async void OnMtkBypassClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner ||
            !TryReadMtkSelection(out var useSerial, out var usbDevice, out _, out _, out _, out _))
        {
            return;
        }

        var language = LanguageService.Instance;
        if (useSerial || usbDevice is null)
        {
            AppendLog(language.GetString("Flash_MtkUsbOnlyOperation"));
            return;
        }

        if (MtkExploitBypassBox.IsChecked != true || MtkExploitVolatileBox.IsChecked != true)
        {
            AppendLog(language.GetString("Flash_MtkExploitUnauthorized"));
            return;
        }

        var payload = Enum.GetValues<MtkExploitPayloadSource>()[Math.Max(MtkExploitPayloadBox.SelectedIndex, 0)];
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkRunBypass"),
                $"{language.GetString("Flash_MtkRunBypassConfirm")}{Environment.NewLine}{usbDevice.DisplayName}");
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        MtkBypassButton.IsEnabled = false;
        try
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            var handoff = await MtkBypassService.RunOnEndpointAsync(usbDevice, new MtkBypassRequest
            {
                AllowExploitBypass = true,
                AllowUnsafeVolatileWrite = true,
                AllowUnknownVar1Brute = MtkBruteVar1Box.IsChecked == true,
                Family = ReadExploitFamily(MtkExploitFamilyBox.SelectedIndex),
                Payload = payload
            }, progress, cancellation.Token).ConfigureAwait(true);
            AppendLog(handoff.Summary);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>按面板选定的代理家族只读捕获 RPMB：先查该家族支持的寻址形态，再选择落盘文件，任何写入都经
    /// 暂存与校验后替换；捕获内容可能包含密钥，执行前需用户确认。</para>
    /// Captures RPMB read-only for the download-agent family chosen in the pane: the family's supported
    /// addressing shape is consulted first, then a destination file is picked, and every publish is staged
    /// and verified; the captured area can contain keys, so the user must confirm before it runs.
    /// </summary>
    private async void OnMtkRpmbReadClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner ||
            !TryReadMtkSelection(out var useSerial, out var usbDevice, out _, out _, out _, out _))
        {
            return;
        }

        var language = LanguageService.Instance;
        if (useSerial || usbDevice is null)
        {
            AppendLog(language.GetString("Flash_MtkUsbOnlyOperation"));
            return;
        }

        var mode = Enum.GetValues<MtkRpmbCaptureMode>()[Math.Max(MtkRpmbModeBox.SelectedIndex, 0)];
        if (MtkRpmbService.DescribeShapeRefusal(SelectedMtkProtocol, mode) is { } refusal)
        {
            AppendLog(refusal);
            return;
        }

        if (!TryReadRpmbShape(mode, MtkRpmbOffsetBox.Text, MtkRpmbLengthBox.Text, out var frames,
                out var byteOffset, out var byteLength))
        {
            AppendLog(language.GetString("Flash_MtkRpmbRangeInvalid"));
            return;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = language.GetString("Flash_MtkRpmbRead"),
            SuggestedFileName = "rpmb.bin",
            DefaultExtension = "bin"
        });
        if (_busy || _confirming || file?.TryGetLocalPath() is not { } destination) return;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkRpmbRead"),
                $"{language.GetString("Flash_MtkRpmbReadConfirm")}{Environment.NewLine}{usbDevice.DisplayName}");
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        MtkRpmbReadButton.IsEnabled = false;
        try
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            var result = await MtkRpmbService.CaptureOnEndpointAsync(usbDevice, new MtkRpmbCaptureRequest
            {
                Family = SelectedMtkProtocol,
                DestinationPath = destination,
                Mode = mode,
                FrameRange = frames,
                ByteOffset = byteOffset,
                ByteLength = byteLength
            }, progress, cancellation.Token).ConfigureAwait(true);
            AppendLog(result.Summary);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>按面板选择把代理带到能执行安全配置操作的状态，再在该会话上运行给定操作；串口与自动匹配两条路径
    /// 共用这一入口。</para>
    /// Boots the agent to the state the secure-boot operations need from the pane selection and runs the
    /// given operation on that session; the serial and auto-match paths share this single entry.
    /// </summary>
    private async Task RunWithReadyMtkSessionAsync(Func<MtkSession, CancellationToken, Task> operation,
        CancellationToken token)
    {
        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        var kind = SelectedMtkStorageKind;
        if (useSerial)
        {
            var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
            using var session = await connect(token).ConfigureAwait(true);
            await operation(session, token).ConfigureAwait(true);
        }
        else
        {
            var matched = await BuildMtkAutoStartupAsync(kind, token).ConfigureAwait(true);
            await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
            {
                var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched, token)
                    .ConfigureAwait(true);
                await operation(ready, token).ConfigureAwait(true);
                return true;
            }, token).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// <para>安全配置操作的统一外壳：可选的用户确认、忙碌与取消状态、以及结束后按面板状态重算可用性。</para>
    /// The shared shell for secure-boot operations: an optional user confirmation, the busy and
    /// cancellation state, and a recomputed control state once the operation ends.
    /// </summary>
    private async Task RunMtkSecurityOperationAsync(string titleKey, string? confirmKey,
        Func<MtkSession, CancellationToken, Task> operation)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        if (confirmKey is not null)
        {
            _confirming = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                    language.GetString(confirmKey));
            }
            finally { _confirming = false; }
            if (!confirmed) return;
        }

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        MtkSecCfgReadButton.IsEnabled = false;
        MtkSecCfgApplyButton.IsEnabled = false;
        MtkEfusePublicKeyButton.IsEnabled = false;
        MtkEfuseRangeButton.IsEnabled = false;
        MtkFullDumpButton.IsEnabled = false;
        MtkOemKeyProgramButton.IsEnabled = false;
        MtkOemKeyPreviewButton.IsEnabled = false;
        MtkFullDumpButton.IsEnabled = false;
        MtkMemoryPeekButton.IsEnabled = false;
        MtkPokeButton.IsEnabled = false;
        MtkBromDumpButton.IsEnabled = false;
        try
        {
            await RunWithReadyMtkSessionAsync(operation, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>把所选代理家族上报的整个用户数据区读取为一份连续镜像：先让用户挑选目标文件并确认，再在已就绪的会话
    /// 上执行只读采集；字节先写暂存文件，只有暂存长度与上报长度一致才替换目标文件。</para>
    /// Reads the whole user data area the selected agent family reported into one contiguous image: the user
    /// picks the destination file and confirms, then the read-only collection runs on a ready session, staging
    /// the bytes and replacing the destination only when the staged length equals the reported length.
    /// </summary>
    private async void OnMtkFullDumpClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner) return;
        var prompt = LanguageService.Instance;
        var family = SelectedMtkProtocol;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = prompt.GetString("Flash_MtkFullDump"),
            SuggestedFileName = $"mtk-{family.ToString().ToLowerInvariant()}-user-area.img",
            DefaultExtension = "img",
            FileTypeChoices =
            [
                new FilePickerFileType("Images") { Patterns = ["*.img"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is not { Length: > 0 } destination) return;

        await RunMtkSecurityOperationAsync("Flash_MtkFullDump", "Flash_MtkFullDumpConfirm",
            async (session, token) =>
            {
                var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
                var record = await MtkFullDumpService.CaptureAsync(session, family, destination, progress, token)
                    .ConfigureAwait(true);
                AppendLog(prompt.GetString(record.CapacityUsedAsReported ? "Flash_MtkFullDumpReported" :
                    "Flash_MtkFullDumpClamped"));
            });
    }

    /// <summary>
    /// <para>解析内存窗口的形状：地址接受可选 0x 前缀的十六进制，长度接受十进制正整数。上限、四字对齐与写长度
    /// 都由 <c>MtkDaMemoryAccess</c> 在通信层校验，界面不重复实现这些规则。</para>
    /// Parses only the shape of a memory window: the address accepts hexadecimal with an optional 0x prefix and the
    /// length a positive decimal integer. Ceilings, four-byte alignment and write-length rules stay validated by
    /// <c>MtkDaMemoryAccess</c> in the communication layer, so the view never re-implements them.
    /// </summary>
    /// <param name="addressText"><para>地址文本。</para>The address text.</param>
    /// <param name="lengthText"><para>长度文本。</para>The length text.</param>
    /// <param name="address"><para>解析出的地址。</para>The parsed address.</param>
    /// <param name="lengthBytes"><para>解析出的字节数。</para>The parsed byte count.</param>
    /// <returns><para>形状可解析时为真。</para>True when the shape parses.</returns>
    internal static bool TryReadMemoryWindow(string? addressText, string? lengthText, out uint address,
        out int lengthBytes)
    {
        if (addressText?.Trim() is not { Length: > 0 } text || lengthText?.Trim() is not { Length: > 0 } length)
        {
            address = 0;
            lengthBytes = 0;
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        var addressParsed = uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedAddress);
        var lengthParsed = int.TryParse(length, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedLength);
        if (!addressParsed || !lengthParsed || parsedLength <= 0)
        {
            address = 0;
            lengthBytes = 0;
            return false;
        }

        address = parsedAddress;
        lengthBytes = parsedLength;
        return true;
    }

    /// <summary>
    /// <para>把读到的字节排成带地址与 ASCII 列的十六进制窗口，供日志面板显示。</para>
    /// Lays the captured bytes out as a hex window with an address and an ASCII column for the log pane.
    /// </summary>
    /// <param name="startAddress"><para>窗口起始地址。</para>The window start address.</param>
    /// <param name="bytes"><para>读到的字节。</para>The bytes read.</param>
    /// <returns><para>每行十六字节的文本。</para>One line per sixteen bytes.</returns>
    internal static string FormatMemoryWindow(uint startAddress, ReadOnlyMemory<byte> bytes)
    {
        var builder = new StringBuilder();
        for (var row = 0; row < bytes.Length; row += 16)
        {
            var width = Math.Min(16, bytes.Length - row);
            _ = builder.Append($"0x{startAddress + row:X8} ");
            for (var column = 0; column < 16; column++)
            {
                _ = builder.Append(column < width ? $"{bytes.Span[row + column]:X2} " : "   ");
            }

            for (var column = 0; column < width; column++)
            {
                var value = bytes.Span[row + column];
                _ = builder.Append(value is >= (byte)'!' and <= (byte)'~' or (byte)' ' ? (char)value : '.');
            }

            _ = builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// <para>读取所选下载代理家族的一段内存或寄存器：寄存器以四字为单位循环，内存为块读取；两者的具体上限由通信层
    /// 校验，回读到的字节以十六进制窗口写进日志。</para>
    /// Reads one memory or register window through the selected download-agent family: register access loops in
    /// four-byte cells and memory access reads a block, both bounded by the communication layer, and the bytes come
    /// back as a hex window in the log.
    /// </summary>
    private async void OnMtkMemoryPeekClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadMemoryWindow(MtkMemoryAddressBox.Text, MtkMemoryLengthBox.Text, out var address, out var length))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkMemoryShapeInvalid"));
            return;
        }

        var family = SelectedMtkProtocol;
        var asRegister = MtkMemoryRegisterBox.IsChecked == true;
        await RunMtkSecurityOperationAsync("Flash_MtkMemoryPeek", null, async (session, token) =>
        {
            var bytes = asRegister
                ? family switch
                {
                    MtkDaProtocol.XFlash => await session.PeekXFlashRegisterAsync(address, length, token)
                        .ConfigureAwait(true),
                    MtkDaProtocol.Legacy => await session.PeekLegacyRegisterAsync(address, length, token)
                        .ConfigureAwait(true),
                    MtkDaProtocol.Xml => await session.PeekXmlRegisterAsync(address, length, token)
                        .ConfigureAwait(true),
                    _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown agent family.")
                }
                : family switch
                {
                    MtkDaProtocol.XFlash => await session.PeekXFlashMemoryAsync(address, length, token)
                        .ConfigureAwait(true),
                    MtkDaProtocol.Legacy => await session.PeekLegacyMemoryAsync(address, length, token)
                        .ConfigureAwait(true),
                    MtkDaProtocol.Xml => await session.PeekXmlMemoryAsync(address, length, token)
                        .ConfigureAwait(true),
                    _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown agent family.")
                };
            AppendLog(FormatMemoryWindow(address, bytes));
        });
    }

    /// <summary>
    /// <para>向所选下载代理家族写入一段字节：写入是不可逆操作，既要勾选确认，也要在对话框里再次确认；数据十六进制
    /// 与长度不符会在任何设备交互之前报错。</para>
    /// Writes bytes through the selected download-agent family: a poke is irreversible, so it needs both the
    /// confirmation checkbox and the dialog, and a payload whose hex length disagrees with the length box fails
    /// before any device interaction.
    /// </summary>
    private async void OnMtkPokeClick(object? sender, RoutedEventArgs e)
    {
        var prompt = LanguageService.Instance;
        if (!TryReadMemoryWindow(MtkMemoryAddressBox.Text, MtkMemoryLengthBox.Text, out var address, out var length))
        {
            AppendLog(prompt.GetString("Flash_MtkMemoryShapeInvalid"));
            return;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromHexString(MtkPokeDataBox.Text?.Trim() ?? string.Empty);
        }
        catch (FormatException)
        {
            AppendLog(prompt.GetString("Flash_MtkPokeDataInvalid"));
            return;
        }

        if (payload.Length != length)
        {
            AppendLog($"{prompt.GetString("Flash_MtkPokeLengthMismatch")} {payload.Length}/{length}");
            return;
        }

        if (MtkPokeConfirmBox.IsChecked != true)
        {
            AppendLog(prompt.GetString("Flash_MtkPokeConfirmRequired"));
            return;
        }

        var family = SelectedMtkProtocol;
        await RunMtkSecurityOperationAsync("Flash_MtkPoke", "Flash_MtkPokeDialogConfirm", async (session, token) =>
        {
            var written = family switch
            {
                MtkDaProtocol.XFlash => await session.PokeXFlashMemoryAsync(address, payload, confirmWrite: true, token)
                    .ConfigureAwait(true),
                MtkDaProtocol.Legacy => await session.PokeLegacyMemoryAsync(address, payload, confirmWrite: true, token)
                    .ConfigureAwait(true),
                MtkDaProtocol.Xml => await session.PokeXmlMemoryAsync(address, payload, confirmWrite: true, token)
                    .ConfigureAwait(true),
                _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown agent family.")
            };
            AppendLog($"0x{address:X8} {written} {prompt.GetString("Flash_MtkPokeWritten")}");
        });
    }

    /// <summary>
    /// <para>经所选下载代理家族把已记载的 boot ROM 窗口采集到文件：文件名由芯片硬件代号决定，因此先让用户选择目录，
    /// 再在会话内解析路径并确认，最后按步遍历写出。</para>
    /// Captures the documented boot-ROM window through the selected download-agent family into a file whose name
    /// comes from the chip hardware code: the user picks a directory first, the path resolves inside the session,
    /// and the walk publishes after every step arrives.
    /// </summary>
    private async void OnMtkBromDumpClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner) return;
        var prompt = LanguageService.Instance;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = prompt.GetString("Flash_MtkBromDump"),
            AllowMultiple = false
        }).ConfigureAwait(true);
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { Length: > 0 } directory)
        {
            return;
        }

        var family = SelectedMtkProtocol;
        await RunMtkSecurityOperationAsync("Flash_MtkBromDump", "Flash_MtkBromDumpConfirm",
            async (session, token) =>
            {
                var destination = MtkMemoryCaptureService.ResolveBromDestinationPath(session, directory);
                var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
                var record = await MtkMemoryCaptureService.CaptureAsync(session, family, destination, progress, token)
                    .ConfigureAwait(true);
                AppendLog($"{prompt.GetString("Flash_MtkBromDumpDone")} {record.ByteLength}");
            });
    }

    /// <summary>
    /// <para>把分区号文本解析为非负无符号整数；文本非法时返回 false，由调用方给出本地化提示。</para>
    /// Parses the partition text as an unsigned integer and returns false when the text is not a plain
    /// non-negative number, leaving the localized message to the caller.
    /// </summary>
    /// <param name="text">The raw box text. <para>输入框原文。</para></param>
    /// <param name="partitionId">The parsed partition number. <para>解析出的分区号。</para></param>
    /// <returns><see langword="true"/> when the text parsed. <para>文本可解析时为
    /// <see langword="true"/>。</para></returns>
    internal static bool TryReadSecCfgPartition(string? text, out uint partitionId) =>
        uint.TryParse(text?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out partitionId);

    /// <summary>
    /// <para>读取设备当前的安全配置报告；只读操作不需要确认，XML 家族没有该通路由服务层拒绝。</para>
    /// Reads the device's current secure-boot configuration report; the read needs no confirmation and the
    /// XML family, which offers no such path, is refused by the service layer.
    /// </summary>
    private async void OnMtkSecCfgReadClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadSecCfgPartition(MtkSecCfgPartitionBox.Text, out var partitionId))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkSecCfgPartitionInvalid"));
            return;
        }

        var family = SelectedMtkProtocol;
        await RunMtkSecurityOperationAsync("Flash_MtkSecCfgRead", null, async (session, token) =>
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            var outcome = await MtkSecCfgService.ReadReportAsync(session, family, partitionId, progress, token)
                .ConfigureAwait(true);
            AppendLog(outcome.Status.Summary);
        });
    }

    /// <summary>
    /// <para>按选定意图写入安全配置锁状态，写完后立即重新读取报告：设备回执只证明命令被接受，当前状态必须以
    /// 回读为准。</para>
    /// Applies the chosen lock intent and immediately re-reads the report afterwards: a device receipt only
    /// proves the command was accepted, so the current state has to come from the re-read.
    /// </summary>
    private async void OnMtkSecCfgApplyClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadSecCfgPartition(MtkSecCfgPartitionBox.Text, out var partitionId))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkSecCfgPartitionInvalid"));
            return;
        }

        var intent = Enum.GetValues<MtkSecCfgLockIntent>()[Math.Max(MtkSecCfgLockBox.SelectedIndex, 0)];
        var family = SelectedMtkProtocol;
        await RunMtkSecurityOperationAsync("Flash_MtkSecCfgApply", "Flash_MtkSecCfgApplyConfirm",
            async (session, token) =>
            {
                var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
                var applied = await MtkSecCfgService.ApplyLockStateAsync(session, family, intent, partitionId,
                    confirmed: true, progress, token).ConfigureAwait(true);
                AppendLog(applied.Status.Summary);
                var reread = await MtkSecCfgService.ReadReportAsync(session, family, partitionId, null, token)
                    .ConfigureAwait(true);
                AppendLog(reread.Status.Summary);
            });
    }

    /// <summary>
    /// <para>读取启动公钥哈希的 eFuse 字段：基址取自芯片表里该硬件代号记录的条目，芯片未知或没有记录基址时
    /// 直接拒绝，不猜测基址。</para>
    /// Reads the boot public-key eFuse field with the base address taken from the chip table entry for the
    /// reported hardware code; an unknown chip or an entry without a recorded base is refused rather than
    /// guessed.
    /// </summary>
    private async void OnMtkEfusePublicKeyClick(object? sender, RoutedEventArgs e)
    {
        var family = SelectedMtkProtocol;
        var language = LanguageService.Instance;
        await RunMtkSecurityOperationAsync("Flash_MtkEfusePublicKey", null, async (session, token) =>
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            if (family != MtkDaProtocol.XFlash)
            {
                AppendLog(language.GetString("Flash_MtkEfuseFamilyUnsupported"));
                return;
            }

            var chip = await session.GetXFlashChipIdAsync(token).ConfigureAwait(true);
            if (MtkBromChipTable.Find(chip.HardwareCode)?.EfuseBaseAddress is not { } efuseBase)
            {
                AppendLog($"{language.GetString("Flash_MtkEfuseBaseUnknown")} 0x{chip.HardwareCode:x4}");
                return;
            }

            var outcome = await MtkSecCfgService.ReadEfuseFieldAsync(session, family,
                MtkSecEfuseField.BootPublicKey, efuseBase, progress, token).ConfigureAwait(true);
            AppendLog(outcome.Status.Summary);
        });
    }

    /// <summary>
    /// <para>按选择框与文本框构造 OEM 密钥请求并立即解码校验：非法基址、非法密钥或缺失 SEJ 基址都会抛出，
    /// 因而发生在确认对话框之前，不会先打扰用户再报错。</para>
    /// Builds the OEM key request from the selectors and boxes and decodes it immediately, so a malformed
    /// base, a malformed key or a missing SEJ base throws before any confirmation dialog is shown.
    /// </summary>
    /// <param name="kindIndex">The kind selector index. <para>类别选择框索引。</para></param>
    /// <param name="keyHex">The key text. <para>密钥文本。</para></param>
    /// <param name="sejBaseText">The optional SEJ base text in hexadecimal.
    /// <para>可选的十六进制 SEJ 基址文本。</para></param>
    /// <returns>The validated request. <para>校验通过的请求。</para></returns>
    /// <exception cref="FormatException">The base text is not hexadecimal.
    /// <para>基址文本不是十六进制。</para></exception>
    /// <exception cref="ArgumentException">The key is malformed or the base is missing where required.
    /// <para>密钥非法或必需基址缺失。</para></exception>
    internal static MtkOemKeyRequest BuildOemKeyRequest(int kindIndex, string? keyHex, string? sejBaseText)
    {
        var kind = Enum.GetValues<MtkOemKeyKind>()[Math.Max(kindIndex, 0)];
        uint? sejBase = null;
        var trimmed = sejBaseText?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            sejBase = uint.Parse(trimmed, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var request = new MtkOemKeyRequest(kind, keyHex?.Trim() ?? string.Empty, sejBase);
        request.DecodeKey();
        return request;
    }

    /// <summary>
    /// <para>写入 OEM 密钥：先在本地完成密钥与基址校验，再经用户确认提交，回执只报告类别、单元数与密钥摘要。</para>
    /// Programs an OEM key: the key and base are validated locally first, the user confirms, and the
    /// receipt reports only the class, the submitted unit count and the key digest.
    /// </summary>
    private async void OnMtkOemKeyProgramClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        MtkOemKeyRequest request;
        try
        {
            request = BuildOemKeyRequest(MtkOemKeyKindBox.SelectedIndex, MtkOemKeyHexBox.Text,
                MtkOemKeySejBaseBox.Text);
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            return;
        }

        var family = SelectedMtkProtocol;
        await RunMtkSecurityOperationAsync("Flash_MtkOemKeyProgram", "Flash_MtkOemKeyProgramConfirm",
            async (session, token) =>
            {
                var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
                var outcome = await MtkSecCfgService.ProgramOemKeyAsync(session, family, request,
                    confirmed: true, progress, token).ConfigureAwait(true);
                AppendLog(outcome.Status.Summary);
            });
    }

    /// <summary>
    /// <para>解析按位置的 eFuse 单元窗口：起始必须是非负十进制整数，数量必须是正十进制整数，其他文本被拒绝。</para>
    /// Parses a positional eFuse cell window: the start must be a non-negative decimal integer and the count
    /// a positive one, and any other text is refused.
    /// </summary>
    /// <param name="startText">The start box text. <para>起始框文本。</para></param>
    /// <param name="countText">The count box text. <para>数量框文本。</para></param>
    /// <param name="startIndex">The parsed first cell index. <para>解析出的首单元下标。</para></param>
    /// <param name="count">The parsed cell count. <para>解析出的单元数量。</para></param>
    /// <returns><see langword="true"/> when the window is usable. <para>窗口可用时为
    /// <see langword="true"/>。</para></returns>
    internal static bool TryReadEfuseWindow(string? startText, string? countText, out int startIndex,
        out int count)
    {
        startIndex = 0;
        count = 0;
        if (!int.TryParse(startText?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(countText?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var cells) ||
            start < 0 || cells <= 0)
        {
            return false;
        }

        startIndex = start;
        count = cells;
        return true;
    }

    /// <summary>
    /// <para>读取按位置的 eFuse 单元区间：硬件代号与基址取自设备上报的启动身份与芯片表，起始与数量取自输入框，
    /// 单元表里没有该芯片时由服务层拒绝。</para>
    /// Reads a positional eFuse cell window: the hardware code and base come from the boot identity the
    /// device reports plus the chip table, the start and count from the boxes, and a chip with no cell
    /// table is refused by the service layer.
    /// </summary>
    private async void OnMtkEfuseRangeClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadEfuseWindow(MtkEfuseStartBox.Text, MtkEfuseCountBox.Text, out var startIndex, out var count))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkEfuseWindowInvalid"));
            return;
        }

        var family = SelectedMtkProtocol;
        var language = LanguageService.Instance;
        await RunMtkSecurityOperationAsync("Flash_MtkEfuseRange", null, async (session, token) =>
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            if (family != MtkDaProtocol.XFlash)
            {
                AppendLog(language.GetString("Flash_MtkEfuseFamilyUnsupported"));
                return;
            }

            var chip = await session.GetXFlashChipIdAsync(token).ConfigureAwait(true);
            if (MtkBromChipTable.Find(chip.HardwareCode)?.EfuseBaseAddress is not { } efuseBase)
            {
                AppendLog($"{language.GetString("Flash_MtkEfuseBaseUnknown")} 0x{chip.HardwareCode:x4}");
                return;
            }

            var outcome = await MtkSecCfgService.ReadEfuseRangeAsync(session, family, chip.HardwareCode,
                efuseBase, startIndex, count, progress, token).ConfigureAwait(true);
            AppendLog(outcome.Status.Summary);
        });
    }

    /// <summary>
    /// <para>预览 OEM 密钥写入将提交的寄存器窗口、单元数与密钥摘要；该入口不发出任何字节，提交仍属另一个动作。</para>
    /// Previews the register window, unit count and key digest an OEM key submission would use; this entry
    /// sends no byte, and submitting stays a separate action.
    /// </summary>
    private async void OnMtkOemKeyPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        MtkOemKeyRequest request;
        try
        {
            request = BuildOemKeyRequest(MtkOemKeyKindBox.SelectedIndex, MtkOemKeyHexBox.Text,
                MtkOemKeySejBaseBox.Text);
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            return;
        }

        var family = SelectedMtkProtocol;
        await RunMtkSecurityOperationAsync("Flash_MtkOemKeyPreview", null, (session, token) =>
        {
            // The prepare entry still demands the confirmation flag even though it frames nothing.
            var outcome = MtkSecCfgService.PrepareOemKeyRequest(session, family, request, confirmed: true,
                cancellationToken: token);
            AppendLog(outcome.Status.Summary);
            return Task.CompletedTask;
        });
    }

    private const int EdlAuthTimeoutDefaultMs = 60000;

    /// <summary>
    /// <para>解析鉴权截止毫秒：只接受 1 至 86400000 的十进制整数，其他文本返回 false 由调用方提示。</para>
    /// Parses the authentication deadline in milliseconds, accepting only a decimal integer from 1 through
    /// 86400000 and returning false for any other text so the caller can report it.
    /// </summary>
    /// <param name="text">The raw box text. <para>输入框原文。</para></param>
    /// <param name="timeoutMs">The parsed deadline. <para>解析出的截止时间。</para></param>
    /// <returns><see langword="true"/> when the value is usable. <para>取值可用时为
    /// <see langword="true"/>。</para></returns>
    internal static bool TryReadEdlAuthTimeout(string? text, out int timeoutMs) =>
        int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out timeoutMs) &&
        timeoutMs is >= 1 and <= 86400000;

    /// <summary>
    /// <para>把厂商鉴权与兼容开关写进项目鉴权配置，两处分支共用同一组赋值。</para>
    /// Writes the vendor authentication and compatibility switches into a project authentication
    /// configuration, shared by both auth branches.
    /// </summary>
    /// <param name="options">The options under construction. <para>正在构造的启动要求。</para></param>
    /// <param name="netType">Whether SetNetType is requested. <para>是否请求 SetNetType。</para></param>
    /// <param name="qudRecovery">Whether QUD HELLO recovery is granted.
    /// <para>是否授予 QUD HELLO 恢复。</para></param>
    /// <param name="pendingCompletion">Whether a single pending completion is tolerated.
    /// <para>是否容忍单次 pending 完成。</para></param>
    /// <param name="authTimeoutMs">The authentication deadline. <para>鉴权截止时间。</para></param>
    /// <returns>The options with the switches applied. <para>已套用开关的启动要求。</para></returns>
    private EdlInitializationOptions WithEdlCompatibility(EdlInitializationOptions options, bool netType,
        bool qudRecovery, bool pendingCompletion, int authTimeoutMs) =>
        options with
        {
            SendOnePlusNetType = netType,
            QudHelloRecoveryEnabled = qudRecovery,
            AllowSingleFirehosePendingCompletion = pendingCompletion,
            AuthenticationTimeoutMs = authTimeoutMs,
            AllowStreamDload = EdlAllowStreamDloadBox.IsChecked == true
        };

    /// <summary>
    /// <para>在 preloader 命令阶段的新会话上运行给定操作：每次点击都经租约取一份全新、已初始化但未上传下载代理的
    /// 会话，因此协议 fault 后下一次点击自动重建，不在故障会话上重试。</para>
    /// Runs the given operation on a fresh preloader command-stage session: every click takes a newly initialized
    /// lease with no download-agent uploaded, so a protocol fault is rebuilt by the next click rather than retried
    /// on the faulted session.
    /// </summary>
    private async Task RunMtkPreloaderAsync(string? confirmKey, Func<MtkSession, CancellationToken, Task> operation)
    {
        if (_busy || _confirming ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Mtk, Transport: DeviceTransportKind.Usb } usbDevice ||
            usbDevice.State != "preloader")
        {
            return;
        }

        var language = LanguageService.Instance;
        if (confirmKey is not null && TopLevel.GetTopLevel(this) is Window owner)
        {
            _confirming = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkPreloaderGroup"),
                    language.GetString(confirmKey));
            }
            finally { _confirming = false; }
            if (!confirmed) return;
        }

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        try
        {
            await App.DeviceMonitor.RunMtkAsync(usbDevice, async session =>
            {
                await session.InitializeAsync(cancellation.Token).ConfigureAwait(true);
                await operation(session, cancellation.Token).ConfigureAwait(true);
                return true;
            }, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            AppendLog(language.GetString("Flash_MtkPreloaderSessionFaulted"));
        }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    internal static bool TryReadPreloaderAddress(string? text, out uint value)
    {
        var trimmed = text?.Trim();
        if (trimmed is not null && trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadPreloaderCount(string? text, out int value)
    {
        if (TryReadPreloaderAddress(text, out var hex))
        {
            value = checked((int)hex);
            return true;
        }

        return int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private async void OnMtkPreloaderDumpClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner ||
            !TryReadPreloaderAddress(MtkPreloaderOffsetBox.Text, out var offset) ||
            !TryReadPreloaderCount(MtkPreloaderLengthBox.Text, out var length) || length <= 0 ||
            !TryReadPreloaderCount(MtkPreloaderStepBox.Text, out var step) || step <= 0)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkMemoryLength") + " / " +
                LanguageService.Instance.GetString("Flash_MtkPreloaderStep"));
            return;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Flash_MtkPreloaderDump"),
            SuggestedFileName = "iflash.bin",
            DefaultExtension = "bin"
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } destination) return;
        var confirmFlashMapping = MtkPreloaderConfirmFlashMappingBox.IsChecked == true;
        await RunMtkPreloaderAsync(null, async (session, token) =>
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            var record = await MtkPreloaderService.DumpInternalFlashAsync(session, offset, length, step,
                confirmFlashMapping, destination, progress, token).ConfigureAwait(true);
            AppendLog($"{record.RegionName} 0x{record.StartOffset:X8} -> {record.DestinationPath} " +
                $"({record.ByteLength} bytes, SHA-256 {record.Sha256Hex})");
        });
    }

    private async void OnMtkPreloaderPeekClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadPreloaderAddress(MtkPreloaderOffsetBox.Text, out var address) ||
            !TryReadPreloaderCount(MtkPreloaderLengthBox.Text, out var length) || length <= 0)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkMemoryAddress"));
            return;
        }

        await RunMtkPreloaderAsync(null, async (session, token) =>
        {
            var data = await MtkPreloaderService.PeekWindowAsync(session, address, length, token)
                .ConfigureAwait(true);
            AppendLog($"0x{address:X8}: {Convert.ToHexString(data)}");
        });
    }

    private async void OnMtkPreloaderWriteClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadPreloaderAddress(MtkPreloaderOffsetBox.Text, out var address) ||
            MtkPreloaderWriteDataBox.Text?.Trim() is not { Length: > 0 } hex ||
            !TryParseHexBytes(hex, out var data))
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkPreloaderWriteData"));
            return;
        }

        if (MtkPreloaderWriteConfirmBox.IsChecked != true)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkPreloaderWriteConfirm"));
            return;
        }

        await RunMtkPreloaderAsync("Flash_MtkPreloaderWriteConfirmDialog", async (session, token) =>
        {
            var submitted = await MtkPreloaderService.WriteWindowAsync(session, address, data,
                confirmed: true, token).ConfigureAwait(true);
            AppendLog($"0x{address:X8} <- {submitted} bytes");
        });
    }

    private async void OnMtkPreloaderReturnToBromClick(object? sender, RoutedEventArgs e)
        => await RunMtkPreloaderAsync("Flash_MtkPreloaderReturnToBromConfirm", async (session, token) =>
            await MtkPreloaderService.ReturnToBootRomAsync(session, timeoutSeconds: 0, confirmed: true, token)
                .ConfigureAwait(true));

    private async void OnMtkPreloaderDisableWatchdogClick(object? sender, RoutedEventArgs e)
        => await RunMtkPreloaderAsync("Flash_MtkPreloaderDisableWatchdogConfirm", async (session, token) =>
            await MtkPreloaderService.DisableWatchdogAsync(session, confirmed: true, token).ConfigureAwait(true));

    private async void OnMtkPreloaderJumpClick(object? sender, RoutedEventArgs e)
    {
        if (MtkPreloaderPartitionNameBox.Text?.Trim() is not { Length: > 0 } partitionName)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_MtkPreloaderPartitionName"));
            return;
        }

        await RunMtkPreloaderAsync("Flash_MtkPreloaderJumpConfirm", async (session, token) =>
            await MtkPreloaderService.JumpToPartitionAsync(session, partitionName, confirmed: true, token)
                .ConfigureAwait(true));
    }

    internal static bool TryParseHexBytes(string text, out byte[] bytes)
    {
        var cleaned = new string(text.Replace(" ", string.Empty).Replace("-", string.Empty)
            .Where(Uri.IsHexDigit).ToArray());
        if (cleaned.Length == 0 || cleaned.Length % 2 != 0)
        {
            bytes = [];
            return false;
        }

        bytes = Convert.FromHexString(cleaned);
        return true;
    }

    private async void OnMtkCrashClick(object? sender, RoutedEventArgs e)
    {
        var language = LanguageService.Instance;
        if (!TryReadMtkSelection(out _, out _, out _, out _, out _, out _) ||
            TopLevel.GetTopLevel(this) is not Window owner ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Mtk } usbDevice)
        {
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkCrashButton"),
                language.GetString("Flash_MtkCrashConfirm"));
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        var mode = Enum.GetValues<MtkDaCrashMode>()[Math.Max(MtkCrashModeBox.SelectedIndex, 0)];
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        try
        {
            await RunWithReadyMtkSessionAsync(async (session, token) =>
            {
                var result = await session.CrashDaAsync(mode, confirm: true, token).ConfigureAwait(true);
                AppendLog($"{result.Mode}: {result.Detail}");
                AppendLog(language.GetString("Flash_MtkCrashConfirm"));
            }, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    private async void OnMtkSetSpeedClick(object? sender, RoutedEventArgs e)
    {
        var language = LanguageService.Instance;
        await RunWithReadyMtkOperationAsync(async (session, token) =>
        {
            var applied = SelectedMtkProtocol switch
            {
                MtkDaProtocol.Legacy => (await session.SetLegacyTransferSpeedAsync(
                    SelectedMtkBaudrate, token).ConfigureAwait(true)).ToString(),
                MtkDaProtocol.XFlash => (await session.SetXFlashUsbSpeedAsync(token).ConfigureAwait(true)).ToString(),
                _ => null
            };
            AppendLog(applied is null
                ? language.GetString("Flash_MtkSpeedFamilyUnsupported")
                : string.Format(language.GetString("Flash_MtkSpeedApplied"), applied));
        });
    }

    private async Task RunWithReadyMtkOperationAsync(Func<MtkSession, CancellationToken, Task> operation)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        try
        {
            await RunWithReadyMtkSessionAsync(operation, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    private int SelectedMtkBaudrate => MtkBaudBox.SelectedItem is int baud ? baud : 921600;

    private void OnMtkRebootClick(object? sender, RoutedEventArgs e)
        => RunMtkShutdownAsync(MtkXFlashShutdownMode.NormalBoot,
            "Flash_MtkReboot", "Flash_MtkRebootConfirm", "Flash_MtkRebootDone");
    private void OnMtkRebootFastbootClick(object? sender, RoutedEventArgs e)
        => RunMtkShutdownAsync(MtkXFlashShutdownMode.Fastboot,
            "Flash_MtkRebootFastboot", "Flash_MtkRebootFastbootConfirm", "Flash_MtkRebootFastbootDone");
    private void OnMtkPowerOffClick(object? sender, RoutedEventArgs e)
        => RunMtkShutdownAsync(MtkXFlashShutdownMode.PowerOff,
            "Flash_MtkPowerOff", "Flash_MtkPowerOffConfirm", "Flash_MtkPowerOffDone");

    private void OnMtkRebootMetaClick(object? sender, RoutedEventArgs e)
        => RunMtkXmlRebootAsync(MtkXmlBootMode.Meta,
            "Flash_MtkRebootMeta", "Flash_MtkRebootMetaConfirm", "Flash_MtkRebootMetaDone");

    private void OnMtkRebootAndroidTestClick(object? sender, RoutedEventArgs e)
        => RunMtkXmlRebootAsync(MtkXmlBootMode.AndroidTest,
            "Flash_MtkRebootAndroidTest", "Flash_MtkRebootAndroidTestConfirm", "Flash_MtkRebootAndroidTestDone");

    /// <summary>
    /// <para>META 与 Android 测试模式重启只由 XML 下载代理家族定义：非 XML 家族在接触设备前本地拒绝；家族匹配时先
    /// 启动到存储就绪的 XML 代理，再请求该启动目标，执行前需用户确认。</para>
    /// META and Android-test reboots are defined only by the XML download-agent family: a non-XML family is refused
    /// locally before any device contact; when the family matches, a storage-ready XML agent is booted first and the
    /// boot destination is requested afterwards under confirmation.
    /// </summary>
    private async void RunMtkXmlRebootAsync(MtkXmlBootMode mode, string titleKey, string confirmKey, string doneKey)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner) return;
        var language = LanguageService.Instance;
        if (SelectedMtkProtocol != MtkDaProtocol.Xml)
        {
            AppendLog(language.GetString("Flash_MtkXmlRebootFamilyRequired"));
            return;
        }

        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                language.GetString(confirmKey));
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        try
        {
            async Task FinishAsync(MtkSession session, CancellationToken token)
            {
                await session.RebootXmlAsync(new MtkXmlRebootOptions { Mode = mode }, cancellationToken: token)
                    .ConfigureAwait(true);
            }

            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, SelectedMtkStorageKind, attach, autoMatch);
                using var session = await connect(cancellation.Token);
                await FinishAsync(session, cancellation.Token);
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(SelectedMtkStorageKind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched,
                        cancellation.Token);
                    await FinishAsync(ready, cancellation.Token);
                    return true;
                }, cancellation.Token);
            }

            AppendLog(language.GetString(doneKey));
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>先按面板选择启动到存储就绪的代理，再向代理请求关机或启动模式切换；执行前需用户确认。</para>
    /// Boots a storage-ready agent from the pane selection and then requests a power-off or a boot-mode
    /// transition from that agent; the user must confirm before anything is sent.
    /// </summary>
    /// <param name="mode">The authorized power/boot request. <para>经授权的电源/启动请求。</param>
    /// <param name="titleKey">Confirmation title resource key. <para>确认标题的资源键。</param>
    /// <param name="confirmKey">Confirmation text resource key. <para>确认正文的资源键。</param>
    /// <param name="doneKey">Success message resource key. <para>成功消息的资源键。</param>
    private async void RunMtkShutdownAsync(MtkXFlashShutdownMode mode, string titleKey, string confirmKey,
        string doneKey)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(this) is not Window owner ||
            !TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        var language = LanguageService.Instance;
        var kind = SelectedMtkStorageKind;
        if (MtkPowerRequestRefusalKey(SelectedMtkProtocol, mode) is { } refusal)
        {
            AppendLog(language.GetString(refusal));
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner,
                language.GetString(titleKey), language.GetString(confirmKey));
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        CancelFlashButton.IsEnabled = true;
        MtkFlashButton.IsEnabled = false;
        try
        {
            async Task FinishAsync(MtkSession session, CancellationToken token)
            {
                switch (SelectedMtkProtocol)
                {
                    case MtkDaProtocol.Legacy:
                        await session.ShutdownLegacyAsync(mode switch
                        {
                            MtkXFlashShutdownMode.PowerOff => MtkLegacyShutdownMode.PowerOff,
                            MtkXFlashShutdownMode.Fastboot => MtkLegacyShutdownMode.Fastboot,
                            _ => MtkLegacyShutdownMode.NormalBoot
                        }, token);
                        return;
                    case MtkDaProtocol.Xml
                        when mode is not (MtkXFlashShutdownMode.NormalBoot or MtkXFlashShutdownMode.Fastboot):
                        throw new InvalidOperationException(
                            "The XML download agent defines boot-mode reboot only; the pane refuses a power-off request for this family before any device contact.");
                    case MtkDaProtocol.Xml:
                        await session.RebootXmlAsync(new MtkXmlRebootOptions
                        {
                            Mode = mode == MtkXFlashShutdownMode.Fastboot
                                ? MtkXmlBootMode.Fastboot
                                : MtkXmlBootMode.Normal
                        }, cancellationToken: token);
                        return;
                    default:
                        await session.ShutdownXFlashAsync(mode, token);
                        return;
                }
            }

            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                using var session = await connect(cancellation.Token);
                await FinishAsync(session, cancellation.Token);
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched,
                        cancellation.Token);
                    await FinishAsync(ready, cancellation.Token);
                    return true;
                }, cancellation.Token);
            }

            AppendLog(language.GetString(doneKey));
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            CancelFlashButton.IsEnabled = false;
            UpdateEdlButton();
        }
    }

    internal static bool FlashModeAllowed(DeviceProtocolKind mode, bool flatBin,
        bool hasProgrammer, bool hasRockchipParameter)
        => mode == DeviceProtocolKind.Fastboot ||
            mode == DeviceProtocolKind.Rkdl && hasRockchipParameter ||
            flatBin && (mode == DeviceProtocolKind.Adb ||
                        mode == DeviceProtocolKind.Edl && hasProgrammer);
}