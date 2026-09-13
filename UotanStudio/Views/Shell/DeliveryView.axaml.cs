using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Firmware;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Shell;

/// <summary><para>ROM 交付页面，协调原有表单、方案保存和文件输出。</para>Coordinates the existing delivery form, plan persistence and file output.</summary>
public partial class DeliveryView : UserControl
{
    private const double MinimumSidebarWidth = 268;
    private const double MinimumContentWidth = 260;
    private bool _updatingOptions;
    private bool _attached;
    private string? _solutionPath;
    private string? _appliedSourceSettings;
    // Preserve source and saved group metadata without exposing a second layout editor.
    // 原包和已保存的分组元数据继续保留，不再提供第二组布局编辑框。
    private List<DeliveryGroup> _deliveryGroups = [];
    private bool? _stacked;
    private double _defaultSidebarWidth = MinimumSidebarWidth;
    private double? _customSidebarWidth;
    private ColumnDefinition? _sidebarColumn;
    private bool _updatingSidebarWidth;
    private double _stackedRatio = 0.4;
    private bool? _outputDirectoryStacked;
    private bool _pickingOutput;
    private bool _pickingPayloadFile;
    private ColumnDefinitions? _rightImagesColumns;
    private double _bottomImagesHeightRatio = 0.6;
    private double _rightImagesHeightRatio = 0.6;

    /// <summary><para>初始化交付配置与共用 SUPER 设置。</para>Initializes delivery options and shared SUPER settings.</summary>
    public DeliveryView() : this(new UotanStudio.Services.Keys.KeyLibraryService()) { }

    internal DeliveryView(UotanStudio.Services.Keys.KeyLibraryService keyLibrary)
    {
        _payloadKeyLibrary = keyLibrary;
        InitializeComponent();
        DirectOutputBox.IsCheckedChanged += OnOutputOptionChanged;
        PackSuperBox.IsCheckedChanged += OnOutputOptionChanged;
        PackPayloadBox.IsCheckedChanged += OnOutputOptionChanged;
        UseProjectSuperSettingsBox.IsCheckedChanged += (_, _) =>
        {
            if (UseProjectSuperSettingsBox.IsChecked != true) _appliedSourceSettings = null;
            UpdateProjectSuperSelector();
        };
        SuperVabBox.IsCheckedChanged += OnSuperDeviceTypeChanged;
        SuperAbBox.IsCheckedChanged += OnSuperDeviceTypeChanged;
        SuperSettingsProjectBox.SelectionChanged += (_, _) => ApplySourceSettings();
        SuperAOnlyBox.IsCheckedChanged += OnSuperDeviceTypeChanged;
        ImageSelection.SelectedImagesChanged += (_, _) => UpdateCompositionImages();
        ImageSelection.LayoutChanged += (_, _) => UpdateImagesLayout();
        // Lock the whole sidebar pack-settings panel while the composition list is confirmed.
        // 编排列表确认后，左侧打包设置面板整体禁用，取消确认后恢复。
        Composition.Changed += (_, _) => UpdateSidebarLock();
        DeliveryContent.SizeChanged += (_, _) => UpdateImagesColumnMinimums();
        foreach (var option in OutputOptions.Children)
            option.SizeChanged += OnOutputOptionSizeChanged;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateProjectDefaults();
        UpdatePackingFields();
        InitializeDelivery();
        UpdateSidebarLock();
    }

    private void UpdateSidebarLock()
    {
        SidebarForm.IsEnabled = !Composition.IsCompositionConfirmed && !Composition.Model.IsRunning;
        ImageSelection.IsEnabled = !Composition.Model.IsRunning;
    }

    private void OnOutputOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingOptions) return;
        _updatingOptions = true;
        try
        {
            if (ReferenceEquals(sender, PackSuperBox) && PackSuperBox.IsChecked == true)
            {
                PackPayloadBox.IsChecked = false;
                SuperDeviceNameBox.Text = "super";
            }
            else if (ReferenceEquals(sender, PackPayloadBox) && PackPayloadBox.IsChecked == true)
            {
                PackSuperBox.IsChecked = false;
                SuperDeviceNameBox.Text = "payload.bin";
            }
            UpdatePackingFields();
        }
        finally { _updatingOptions = false; }
    }

    private void UpdatePackingFields()
    {
        var packSuper = PackSuperBox.IsChecked == true;
        var packPayload = PackPayloadBox.IsChecked == true;
        SettingsDivider.IsVisible = packSuper || packPayload;
        ProjectSettingsSelector.IsVisible = packSuper || packPayload;
        SuperSettings.IsVisible = packSuper || packPayload;
        SuperFormatOptions.IsVisible = packSuper;
        SuperMetadataSizeField.IsVisible = packSuper;
        SuperBlockSizeField.IsVisible = packSuper || packPayload;
        SuperMetadataSlotsField.IsVisible = packSuper;
        SuperImageOptions.IsVisible = packSuper;
        PayloadSettings.IsVisible = packPayload;
        SuperDeviceNameBox.IsReadOnly = packPayload;
        if (packPayload) SuperDeviceNameBox.Text = "payload.bin";
        UseProjectSuperSettingsBox.Classes.Set("payload", packPayload);
        UpdateSuperProjectChoices(ProjectContextService.Instance, resetSelection: false);
        UpdateDeviceTypeOptions();
        var modeChanged = Composition.SetOutputOptions(DirectOutputBox.IsChecked == true, packSuper, packPayload);
        if (modeChanged && !_restoringDelivery) ImageSelection.SetAssignedPaths([]);
        UpdateCompositionImages();
        UpdateSidebarLock();
    }

    private void UpdateDeviceTypeOptions()
    {
        SuperAbBox.IsVisible = PackSuperBox.IsChecked != true;
        SuperAOnlyBox.IsVisible = PackPayloadBox.IsChecked != true;
        if ((!SuperAbBox.IsVisible && SuperAbBox.IsChecked == true)
            || (!SuperAOnlyBox.IsVisible && SuperAOnlyBox.IsChecked == true))
            SuperVabBox.IsChecked = true;
        // A-Only 联动仅在实际打包 Super 时生效；其他模式下 IsAOnly 复位，避免隐藏残留。
        // The A-Only linkage only applies while actually packing Super; IsAOnly resets otherwise so the hide state never lingers.
        Composition.IsAOnly = PackSuperBox.IsChecked == true
            && SuperAOnlyBox.IsChecked == true
            && SuperVabBox.IsChecked != true
            && SuperAbBox.IsChecked != true;
        UpdateSuperFormatOptions();
    }

    private void UpdateSuperFormatOptions()
    {
        var zipOutput = DirectOutputBox.IsChecked == true;
        if (zipOutput)
        {
            // ZIP delivery carries a complete RAW Super image; sparse output is not a
            // valid selectable format for this mode.
            // ZIP 交付必须携带完整 RAW Super 镜像，SPARSE 在此模式下不可选。
            SuperRawBox.IsChecked = true;
            SuperSparseBox.IsChecked = false;
        }
        SuperSparseBox.IsEnabled = !zipOutput;
        Composition.OutputView.SetTxtScriptBlockedByZstd(PackSuperBox.IsChecked == true && SuperZstdBox.IsChecked == true);
    }

    private void UpdateCompositionImages()
    {
        if (_restoringDelivery) return;
        var previous = Composition.Model.Container.ToHashSet();
        Composition.SetImages(ImageSelection.GetSelectedImages());
        if (UseProjectSuperSettingsBox.IsChecked == true && PackPayloadBox.IsChecked == true)
        {
            var members = _deliveryGroups.SelectMany(g => g.Partitions).ToHashSet(StringComparer.Ordinal);
            foreach (var row in Composition.Model.Container.Where(row => !previous.Contains(row))) row.IncludeInSuper = members.Contains(row.PartitionName);
        }
    }

    private void UpdateImagesLayout()
    {
        var onRight = ImageSelection.ImagesOnRight;
        // The new divider occupies the middle of the existing 12 DIP gap before the editor.
        // 新分隔线放在编辑器前方原有的 12 DIP 间距中，避免额外留白挤压相邻两栏。
        const double gapBeforeDivider = 6;
        const double gapAfterDivider = 5;
        var rows = DeliveryContent.RowDefinitions;
        var heightRatio = rows[0].Height.Value / (rows[0].Height.Value + rows[2].Height.Value);
        if (onRight)
        {
            _bottomImagesHeightRatio = heightRatio;
            var editorBounds = Composition.GetInlineEditorBounds(DeliveryContent);
            var availableWidth = Math.Max(0, DeliveryContent.Bounds.Width - 1);
            var minimum = Math.Min(220, availableWidth / 2);
            var centerWidth = Math.Clamp(editorBounds?.X - gapAfterDivider - 1 ?? availableWidth / 2,
                minimum, availableWidth - minimum);
            var rightInset = editorBounds is { } editor ? Math.Max(0, DeliveryContent.Bounds.Width - editor.Right) : 12;
            _rightImagesColumns =
            [
                new ColumnDefinition(new GridLength(centerWidth, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(1)),
                new ColumnDefinition(new GridLength(availableWidth - centerWidth, GridUnitType.Star))
            ];
            DeliveryContent.ColumnDefinitions = _rightImagesColumns;
            UpperArea.Padding = new Thickness(12, 12, gapBeforeDivider, 12);
            ImageSelection.Margin = new Thickness(gapAfterDivider, 12, rightInset, 12);
        }
        else
        {
            _rightImagesHeightRatio = heightRatio;
            DeliveryContent.ColumnDefinitions = new ColumnDefinitions("*");
            UpperArea.Padding = new Thickness(12);
            ImageSelection.Margin = new Thickness(12);
        }
        var restoredHeightRatio = onRight ? _rightImagesHeightRatio : _bottomImagesHeightRatio;
        rows[0].Height = new GridLength(restoredHeightRatio, GridUnitType.Star);
        rows[2].Height = new GridLength(1 - restoredHeightRatio, GridUnitType.Star);
        Grid.SetColumn(ImageSelection, onRight ? 2 : 0);
        Grid.SetRow(ImageSelection, onRight ? 0 : 2);
        Grid.SetRowSpan(ImageSelection, onRight ? 3 : 1);
        LowerEditorHost.IsVisible = onRight;
        RightImagesDivider.IsVisible = onRight;
        RightImagesSplitter.IsVisible = onRight;
        Composition.SetEditorHost(onRight ? LowerEditorHost : null);
        UpdateImagesColumnMinimums();
    }

    private void UpdateImagesColumnMinimums()
    {
        if (!ImageSelection.ImagesOnRight || _rightImagesColumns is null) return;
        var minimum = Math.Min(220, Math.Max(0, (DeliveryContent.Bounds.Width - 1) / 2));
        _rightImagesColumns[0].MinWidth = minimum;
        _rightImagesColumns[2].MinWidth = minimum;
    }

    private void UpdateProjectDefaults()
    {
        var context = ProjectContextService.Instance;
        UpdateSuperProjectChoices(context, _solutionPath != context.CurrentSolutionPath);
        if (_solutionPath == context.CurrentSolutionPath) return;
        _solutionPath = context.CurrentSolutionPath;
        Composition.Reset();
        OutputDirectoryBox.Text = context.SubProjects.FirstOrDefault()?.OutputDir ?? string.Empty;
        DirectOutputBox.IsChecked = true;
        PackSuperBox.IsChecked = false;
        PackPayloadBox.IsChecked = false;
        ImageSelection.ShowOriginals = false;
        UseProjectSuperSettingsBox.IsChecked = false;
        SuperVabBox.IsChecked = true;
        SuperAOnlyBox.IsChecked = false;
        SuperDeviceNameBox.Text = "super";
        SuperMetadataSizeBox.Text = "65536";
        SuperBlockSizeBox.Text = "4096";
        SuperMetadataSlotsBox.Text = "3";
        GenerateSuperEmptyBox.IsChecked = false;
        PayloadVersionBox.Text = "2";
        PayloadMinorVersionBox.Text = "0";
        PayloadBuildFingerprintBox.Text = string.Empty;
        PayloadIncrementalBox.Text = string.Empty;
        PayloadSdkLevelBox.Text = string.Empty;
        DynamicPartitionsFileBox.Text = string.Empty;
        PayloadSignatureBox.Text = string.Empty;
        _legacyPayloadSignatureFile = "";
        _payloadSigningFingerprint = "";
        _payloadSigningIdentityPath = "";
        PayloadPostinstallBox.Text = string.Empty;
        PayloadTimestampBox.Text = "0"; PayloadSecurityPatchBox.Text = "";
        SuperZstdBox.IsChecked = false; _deliveryGroups = [];
    }

    private void OnSuperDeviceTypeChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            SuperMetadataSlotsBox.Text = ReferenceEquals(sender, SuperVabBox) ? "3" : "2";
            // Keep the composition panel's A-Only visibility in sync immediately when
            // switching between device types. Use the event source instead of reading
            // the other radio button because Avalonia may clear the previous selection
            // after this checked event. This prevents A-Only from lingering on VAB.
            // 直接使用事件来源判断设备类型，避免旧 RadioButton 尚未取消选中时留下 A-Only 状态。
            Composition.IsAOnly = PackSuperBox.IsChecked == true && ReferenceEquals(sender, SuperAOnlyBox);
        }
    }

    private void UpdateProjectSuperSelector()
    {
        // 勾选使用项目 Super/Payload 设置且存在可选项目时，仅禁用由项目配置驱动的表单字段，避免用户覆盖项目值；
        // 底部镜像选项（压缩为ZST、生成Super_Empty）不属于项目配置驱动字段，保持可交互。
        // While a source configuration is applied, only the fields it drives are disabled so the project values
        // cannot be overridden; the image options (ZST, Super_Empty) are not project-driven and stay interactive.
        var applied = UseProjectSuperSettingsBox.IsChecked == true && SuperSettingsProjectBox.Items.Count > 0;
        SuperSettingsProjectBox.IsEnabled = applied;
        SuperFormatOptions.IsEnabled = !applied;
        SuperDeviceTypeOptions.IsEnabled = !applied;
        SuperDeviceNameField.IsEnabled = !applied;
        SuperBlockSizeField.IsEnabled = !applied;
        SuperMetadataSizeField.IsEnabled = !applied;
        SuperSizeField.IsEnabled = !applied;
        SuperGroupNameField.IsEnabled = !applied;
        SuperMetadataSlotsField.IsEnabled = !applied;
        PayloadTimestampBox.IsEnabled = !applied;
        PayloadSecurityPatchBox.IsEnabled = !applied;
        ApplySourceSettings();
    }

    private SourceContainerSettings? ReadSourceSettings(string root)
    {
        try { return SourceContainerMetadataService.Read(root, PackPayloadBox.IsChecked == true ? "payload" : "super"); }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Delivery", ex.Message);
            return null;
        }
    }

    private void UpdateSuperProjectChoices(ProjectContextService context, bool resetSelection)
    {
        var selected = resetSelection ? null : SuperSettingsProjectBox.SelectedItem as string;
        var names = context.SubProjects.Where(project => ReadSourceSettings(project.RootPath) is not null)
            .Select(project => project.Name).ToArray();
        if (SuperSettingsProjectBox.ItemsSource is not string[] previous || !previous.SequenceEqual(names))
            SuperSettingsProjectBox.ItemsSource = names;
        SuperSettingsProjectBox.SelectedItem = names.FirstOrDefault(name => name == selected)
            ?? names.FirstOrDefault(name => name == context.ActiveSubProject?.Name) ?? names.FirstOrDefault();
        UpdateProjectSuperSelector();
    }

    private void ApplySourceSettings()
    {
        if (UseProjectSuperSettingsBox.IsChecked != true) return;
        var project = ProjectContextService.Instance.SubProjects.FirstOrDefault(p => p.Name == SuperSettingsProjectBox.SelectedItem as string);
        if (project is null || ReadSourceSettings(project.RootPath) is not { } settings) return;
        var sourcePath = SourceContainerMetadataService.GetPath(project.RootPath, settings.Kind);
        var identity = sourcePath + "|" + File.GetLastWriteTimeUtc(sourcePath).Ticks;
        if (_appliedSourceSettings == identity) return;
        try
        {
            var groups = DeliveryGroups.FromSource(settings);
            _deliveryGroups = groups;
            if (settings.Kind == "payload")
            {
                var members = groups.SelectMany(g => g.Partitions).ToHashSet(StringComparer.Ordinal);
                foreach (var row in Composition.Model.Container) row.IncludeInSuper = members.Contains(row.PartitionName);
            }
        }
        catch (Exception ex) { UseProjectSuperSettingsBox.IsChecked = false; _ = ShowDeliveryMessageAsync(ex.Message, true); return; }
        _appliedSourceSettings = identity;
        SuperVabBox.IsChecked = settings.DeviceType == "VAB";
        SuperAbBox.IsChecked = settings.DeviceType == "AB";
        SuperAOnlyBox.IsChecked = settings.DeviceType == "AOnly";
        SuperGroupNameBox.Text = settings.GroupNames.FirstOrDefault(name => name != "default") ?? settings.GroupNames.FirstOrDefault() ?? "";
        if (settings.Kind == "super")
        {
            SuperRawBox.IsChecked = settings.Storage == "RAW";
            SuperSparseBox.IsChecked = settings.Storage == "SPARSE";
            SuperSizeBox.Text = settings.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SuperMetadataSlotsBox.Text = settings.MetadataSlots.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SuperMetadataSizeBox.Text = settings.MetadataMaxSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SuperBlockSizeBox.Text = settings.BlockSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            PayloadVersionBox.Text = "2";
            PayloadMinorVersionBox.Text = "0";
            SuperBlockSizeBox.Text = settings.BlockSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            PayloadTimestampBox.Text = settings.MaxTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            PayloadSecurityPatchBox.Text = settings.SecurityPatchLevel;
        }
        UpdateDeviceTypeOptions();
    }

    private void OnOutputDirectoryRowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (OutputDirectoryRow.Bounds.Width <= 0) return;
        var stacked = OutputDirectoryRow.Bounds.Width < 300;
        if (_outputDirectoryStacked == stacked) return;
        _outputDirectoryStacked = stacked;
        OutputDirectoryRow.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,8,Auto");
        OutputDirectoryRow.RowDefinitions = new RowDefinitions(stacked ? "Auto,8,Auto" : "Auto");
        Grid.SetColumn(BrowseOutputButton, stacked ? 0 : 2);
        Grid.SetRow(BrowseOutputButton, stacked ? 2 : 0);
    }

    private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        if (_pickingOutput || TopLevel.GetTopLevel(this) is not { StorageProvider.CanPickFolder: true } owner) return;
        var solutionPath = ProjectContextService.Instance.CurrentSolutionPath;
        _pickingOutput = true;
        BrowseOutputButton.IsEnabled = false;
        try
        {
            var currentPath = OutputDirectoryBox.Text?.Trim();
            using var startFolder = Directory.Exists(currentPath)
                ? await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(currentPath!))) : null;
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("PackDialog_BrowseOutput"),
                AllowMultiple = false,
                SuggestedStartLocation = startFolder
            });
            using var selectedFolder = folders.FirstOrDefault();
            if (_attached && solutionPath == ProjectContextService.Instance.CurrentSolutionPath &&
                selectedFolder?.TryGetLocalPath() is { } path)
                OutputDirectoryBox.Text = path;
        }
        catch (Exception ex)
        {
            if (_attached && owner is Window window)
                await DialogService.ShowAsync(window, LanguageService.Instance.GetString("NewProject_PickerFailed"),
                    ex.Message, Ursa.Controls.MessageBoxIcon.Error);
        }
        finally
        {
            _pickingOutput = false;
            BrowseOutputButton.IsEnabled = true;
        }
    }

    private void OnPayloadFileRowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid row || row.Bounds.Width <= 0 || row.Children[1] is not Button button) return;
        var stacked = row.Bounds.Width < 300;
        if (stacked == (row.ColumnDefinitions.Count == 1)) return;
        row.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,8,Auto");
        row.RowDefinitions = new RowDefinitions(stacked ? "Auto,8,Auto" : "Auto");
        Grid.SetColumn(button, stacked ? 0 : 2);
        Grid.SetRow(button, stacked ? 2 : 0);
    }

    private async void OnBrowsePayloadFileClick(object? sender, RoutedEventArgs e)
    {
        if (_pickingPayloadFile || TopLevel.GetTopLevel(this) is not { StorageProvider.CanOpen: true } owner) return;
        var editor = ReferenceEquals(sender, BrowseDynamicPartitionsButton) ? DynamicPartitionsFileBox
            : ReferenceEquals(sender, BrowsePayloadSignatureButton) ? PayloadSignatureBox
            : ReferenceEquals(sender, BrowsePayloadPostinstallButton) ? PayloadPostinstallBox : null;
        if (editor is null) return;
        var solutionPath = ProjectContextService.Instance.CurrentSolutionPath;
        _pickingPayloadFile = true;
        SetPayloadFileButtonsEnabled(false);
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Delivery_SelectFile"),
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.All]
            });
            using var selectedFile = files.FirstOrDefault();
            if (_attached && solutionPath == ProjectContextService.Instance.CurrentSolutionPath &&
                selectedFile?.TryGetLocalPath() is { } path)
                editor.Text = path;
        }
        catch (Exception ex)
        {
            if (_attached && owner is Window window)
                await DialogService.ShowAsync(window, LanguageService.Instance.GetString("NewProject_PickerFailed"),
                    ex.Message, Ursa.Controls.MessageBoxIcon.Error);
        }
        finally
        {
            _pickingPayloadFile = false;
            SetPayloadFileButtonsEnabled(true);
        }
    }

    private void SetPayloadFileButtonsEnabled(bool enabled)
    {
        BrowseDynamicPartitionsButton.IsEnabled = enabled;
        BrowsePayloadSignatureButton.IsEnabled = enabled;
        BrowsePayloadPostinstallButton.IsEnabled = enabled;
    }

    /// <summary><para>进入交付页面时刷新项目镜像，并保留当前项目的表单选择。</para>Refreshes images on entry, retaining the current project's form choices.</summary>
    public void Refresh()
    {
        _ = RefreshDeliveryAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        WeakReferenceMessenger.Default.Register<DeliveryView, ProjectContextChangedMessage>(this, static (view, _) =>
            Dispatcher.UIThread.Post(() => { if (view._attached) view.Refresh(); }));
        WeakReferenceMessenger.Default.Register<DeliveryView, LanguageStateChangedMessage>(this, static (view, _) =>
            Dispatcher.UIThread.Post(() => { if (view._attached) view.RefreshImageLanguage(); }));
        RefreshImageLanguage();
        _ = RefreshDeliveryAsync();
        UpdateResponsiveLayout();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _deliverySaveTimer.Stop();
        _ = SaveDeliveryAsync(false);
        _attached = false;
        WeakReferenceMessenger.Default.Unregister<ProjectContextChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<LanguageStateChangedMessage>(this);
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshImageLanguage()
    {
        foreach (var image in Composition.Model.Container.Concat(Composition.Model.Output)) image.RefreshLanguage();
    }

    private void OnOutputOptionSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (OutputOptions.Children.Any(option => option.DesiredSize.Width <= 0)) return;
        var width = Math.Max(MinimumSidebarWidth, Math.Ceiling(
            OutputOptions.Children.Sum(option => option.DesiredSize.Width) +
            OutputOptions.ItemSpacing * (OutputOptions.Children.Count - 1) +
            SidebarForm.Margin.Left + SidebarForm.Margin.Right + Sidebar.Padding.Left + Sidebar.Padding.Right));
        if (Math.Abs(width - _defaultSidebarWidth) < 0.5) return;
        _defaultSidebarWidth = width;
        UpdateResponsiveLayout();
    }

    private void OnSidebarColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // GridSplitter changes this property for both pointer and keyboard resizing.
        // 鼠标拖动及键盘调整均由 GridSplitter 更新列宽；保留用户选择的固定宽度。
        if (!_updatingSidebarWidth && e.Property == ColumnDefinition.WidthProperty && _sidebarColumn is { } column)
            _customSidebarWidth = column.Width.Value;
    }

    private void UpdateSidebarWidth()
    {
        if (_sidebarColumn is not { } column) return;
        _updatingSidebarWidth = true;
        try
        {
            column.MaxWidth = Math.Max(MinimumSidebarWidth, Bounds.Width - 1 - MinimumContentWidth);
            column.Width = new GridLength(_customSidebarWidth ?? _defaultSidebarWidth);
        }
        finally { _updatingSidebarWidth = false; }
    }

    private void UpdateResponsiveLayout()
    {
        if (Bounds.Width <= 0) return;
        var stacked = Bounds.Width < Math.Max(640, _defaultSidebarWidth + 1 + MinimumContentWidth);
        if (_stacked == stacked)
        {
            if (!stacked) UpdateSidebarWidth();
            return;
        }
        // Preserve the stacked layout's height ratio while keeping the sidebar width independent of window size.
        // 上下布局保留高度比例，左右布局的侧栏使用独立于窗口大小的固定宽度。
        if (_stacked == true)
            _stackedRatio = DeliveryLayout.RowDefinitions[0].Height.Value /
                (DeliveryLayout.RowDefinitions[0].Height.Value + DeliveryLayout.RowDefinitions[2].Height.Value);
        if (_sidebarColumn is not null)
            _sidebarColumn.PropertyChanged -= OnSidebarColumnChanged;
        _sidebarColumn = null;
        _stacked = stacked;
        SidebarTopDivider.Margin = stacked ? default : new Thickness(0, 0, 6, 0);
        SidebarDivider.Classes.Set("vertical", !stacked);
        SidebarDivider.Classes.Set("horizontal", stacked);
        SidebarSplitter.Classes.Set("vertical", !stacked);
        SidebarSplitter.Classes.Set("horizontal", stacked);
        if (stacked)
        {
            DeliveryLayout.ColumnDefinitions = new ColumnDefinitions("*");
            DeliveryLayout.RowDefinitions =
            [
                new RowDefinition(new GridLength(_stackedRatio, GridUnitType.Star)) { MinHeight = 120 },
                new RowDefinition(new GridLength(1)),
                new RowDefinition(new GridLength(1 - _stackedRatio, GridUnitType.Star)) { MinHeight = 240 }
            ];
            Grid.SetColumn(SidebarDivider, 0);
            Grid.SetRow(SidebarDivider, 1);
            Grid.SetColumn(SidebarSplitter, 0);
            Grid.SetRow(SidebarSplitter, 1);
            Grid.SetColumn(DeliveryContent, 0);
            Grid.SetRow(DeliveryContent, 2);
        }
        else
        {
            _sidebarColumn = new ColumnDefinition(new GridLength(_customSidebarWidth ?? _defaultSidebarWidth))
            { MinWidth = MinimumSidebarWidth };
            DeliveryLayout.ColumnDefinitions =
            [
                _sidebarColumn,
                new ColumnDefinition(new GridLength(1)),
                new ColumnDefinition(GridLength.Star) { MinWidth = MinimumContentWidth }
            ];
            _sidebarColumn.PropertyChanged += OnSidebarColumnChanged;
            UpdateSidebarWidth();
            DeliveryLayout.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(SidebarDivider, 1);
            Grid.SetRow(SidebarDivider, 0);
            Grid.SetColumn(SidebarSplitter, 1);
            Grid.SetRow(SidebarSplitter, 0);
            Grid.SetColumn(DeliveryContent, 2);
            Grid.SetRow(DeliveryContent, 0);
        }
    }
}
