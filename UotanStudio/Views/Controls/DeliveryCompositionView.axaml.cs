using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using UotanStudio.Services.Delivery;
using UotanStudio.ViewModels.Shell;
using DeliveryImageItem = UotanStudio.ViewModels.Shell.DeliveryImage;

namespace UotanStudio.Views.Controls;

/// <summary><para>交付镜像编排，通过现有按钮连接交付服务。</para>Delivery composition wired to application services through the existing controls.</summary>
public partial class DeliveryCompositionView : UserControl
{
    /// <summary><para>是否显示 Payload 编排。</para>Whether to display Payload composition.</summary>
    public static readonly StyledProperty<bool> IsPayloadProperty =
        AvaloniaProperty.Register<DeliveryCompositionView, bool>(nameof(IsPayload));

    internal DeliveryViewModel Model { get; } = new();
    internal DeliveryOutputView OutputView => ScriptPane;
    internal Func<DeliverySuperOptions> ReadOptions { get; set; } = () => new();
    internal Func<DeliveryPlan> ReadPlan { get; set; } = () => new DeliveryPlan();
    internal event EventHandler? Changed;
    internal event EventHandler? OutputRequested;
    internal event EventHandler<Exception>? OperationFailed;
    internal bool AutoZero { get => AutoZeroPartitionsBox.IsChecked == true; set => AutoZeroPartitionsBox.IsChecked = value; }
    private bool _isAOnly;
    /// <summary><para>当前为 A-Only 设备类型时隐藏自动零分区选项；进入时移除已物化的自动 B 槽行，离开时按复选框勾选态恢复。</para>
    /// Hides the auto-zero option for A-Only devices; entering clears materialized auto B-slot rows, leaving restores per the checkbox state.</summary>
    internal bool IsAOnly
    {
        get => _isAOnly;
        set
        {
            if (_isAOnly == value) return;
            _isAOnly = value;
            Model.AutoZero = !value && AutoZeroPartitionsBox.IsChecked == true;
            UpdateMode();
        }
    }
    /// <summary><para>仅生成Super_Empty：输出设置禁用但开始输出按钮保持可用。</para>
    /// Super_Empty only: output settings disabled while the start-output button stays available.</summary>
    internal bool SuperEmptyOnly
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            UpdateInteractionState();
        }
    }
    internal bool IsSuperEmptyOnly => Model.PackSuper && SuperEmptyOnly;
    private bool? _stacked;
    private bool _outputToDirectory = true;
    private bool _packImages;
    private bool _editorExternal;
    /// <summary><para>当前正在编排容器镜像（Super 或 Payload）。</para>
    /// True while composing a Super or Payload container.</summary>
    internal bool PackContainerMode => Model.PackSuper || Model.PackPayload;
    private ObservableCollection<DeliveryImageItem> ActiveImages => Model.Container;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary><para>在 Super 与 Payload 镜像编排之间切换。</para>Switches between Super and Payload composition.</summary>
    public bool IsPayload { get => GetValue(IsPayloadProperty); set => SetValue(IsPayloadProperty, value); }

    /// <summary><para>初始化本地镜像列表和文档编辑器。</para>Initializes local image lists and the document editor.</summary>
    public DeliveryCompositionView()
    {
        InitializeComponent();
        ScriptPane.SetModel(Model);
        Model.PropertyChanged += (_, _) =>
        {
            CompositionStatusText.Text = Model.ProgressText;
            CompositionStatusText.IsVisible = Model.ProgressText.Length > 0;
            CompositionStatsText.Text = Model.ProgressStats;
            CompositionStatsText.IsVisible = Model.ProgressStats.Length > 0;
        };
        Model.Changed += (_, _) => { UpdateInteractionState(); UpdateEmptyState(); Changed?.Invoke(this, EventArgs.Empty); };
        StartCompositionOutputButton.Click += (_, _) => OutputRequested?.Invoke(this, EventArgs.Empty);
        ScriptPane.OutputRequested += (_, _) => OutputRequested?.Invoke(this, EventArgs.Empty);
        AutoZeroPartitionsBox.IsCheckedChanged += (_, _) => { Model.AutoZero = AutoZeroPartitionsBox.IsChecked == true; UpdateMode(); Changed?.Invoke(this, EventArgs.Empty); };
        ZeroPartitionNameBox.TextChanged += (_, _) => UpdateAddPartitionButton();
        SizeChanged += (_, _) => UpdateCompositionLayout();
        InitializeImageDragging();
        UpdateMode();
        UpdateInteractionState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPayloadProperty && OrderedImages is not null) UpdateMode();
    }

    private void UpdateMode()
    {
        CancelImageDrag();
        ImageListTitle.Classes.Set("payload", IsPayload);

        AutoZeroPartitionsBox.IsVisible = PackContainerMode && !IsPayload && !_isAOnly;
        SuperMembershipHeader.IsVisible = IsPayload;
        ZeroPartitionForm.IsVisible = !IsPayload && !_isAOnly && AutoZeroPartitionsBox.IsChecked != true;
        OrderedImages.ItemsSource = ActiveImages;
        UpdateEmptyState();
        UpdateCompositionLayout();
    }

    internal bool SetOutputOptions(bool outputToDirectory, bool packSuper, bool packPayload)
    {
        var packImages = packSuper || packPayload;
        _outputToDirectory = outputToDirectory;
        _packImages = packImages;
        IsPayload = packPayload;
        var changed = Model.SetMode(outputToDirectory, packSuper, packPayload);

        UpdateMode();
        UpdateInteractionState();
        return changed;
    }

    /// <summary><para>宿主视图据此把左侧打包设置面板随确认态整体禁用或恢复。</para>
    /// Host views use this to disable or restore the whole sidebar pack-settings panel on confirmation.</summary>
    internal bool IsCompositionConfirmed => _packImages && _outputToDirectory && Model.Confirmed && !IsSuperEmptyOnly;

    private void UpdateInteractionState()
    {
        var editingComposition = _packImages && (!Model.Confirmed || IsSuperEmptyOnly);
        if (!editingComposition) CancelImageDrag();
        ImageCompositionContent.IsEnabled = editingComposition && !Model.IsRunning;
        var confirmBeforeOutput = _packImages && _outputToDirectory && !IsSuperEmptyOnly;
        CompositionFooter.IsVisible = !(_packImages && !_outputToDirectory);
        ConfirmCompositionButton.IsEnabled = confirmBeforeOutput && !Model.Confirmed && !Model.IsRunning;

        CancelCompositionButton.IsEnabled = confirmBeforeOutput && Model.Confirmed && !Model.IsRunning;
        CompositionOutputFooter.IsVisible = _packImages && !_outputToDirectory;

        ScriptPane.SetInteractionState(_outputToDirectory && (!_packImages || Model.Confirmed) && !IsSuperEmptyOnly,
            (_outputToDirectory || _packImages) && !IsSuperEmptyOnly, IsSuperEmptyOnly);
    }

    private void OnConfirmCompositionClick(object? sender, RoutedEventArgs e)
    {
        if (!_packImages || !_outputToDirectory || Model.Confirmed) return;
        try { Model.Confirm(ReadPlan()); }
        catch (Exception ex) { Model.Status = global::UotanStudio.Services.UserError.Describe(ex); OperationFailed?.Invoke(this, ex); }
        UpdateInteractionState();
    }

    private void OnCancelCompositionClick(object? sender, RoutedEventArgs e)
    {
        if (!_packImages || !_outputToDirectory || !Model.Confirmed) return;
        Model.ReturnToContainer();
        UpdateInteractionState();
    }

    internal void SetImages(IReadOnlyList<PackImageSelection> images)
    {
        Model.SelectSources([.. images.Select(i => new DeliveryFile
        {
            SourcePath = i.FilePath,
            FileName = Path.GetFileName(i.FilePath),
            Partition = DeliveryPackageBuilder.IsDatArtifact(i.FilePath)
                ? DeliveryPackageBuilder.DatStem(Path.GetFileName(i.FilePath)) : Path.GetFileNameWithoutExtension(i.FilePath),
            SourceGroup = i.GroupName,
            SourceLength = i.SizeBytes
        })]);
        foreach (var image in images) Model.ApplySourcePartitions(image.FilePath, image.DynamicPartitions);
    }

    internal void SetRunning(bool running, double progress)
    {
        Model.IsRunning = running; Model.Progress = progress;
        CompositionOutputProgress.Value = progress;
        StartCompositionOutputButton.Classes.Set("running", running);
        UpdateInteractionState();
        ScriptPane.SetRunning(running, progress);
    }

    internal void Reset()
    {
        CancelImageDrag();
        Model.Reset();
        AutoZeroPartitionsBox.IsChecked = true;
        ZeroPartitionNameBox.Text = string.Empty;
        ScriptPane.Reset();
        CompositionOutputProgress.Value = 0;
        UpdateInteractionState();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyImagesHint.IsVisible = ActiveImages.Count == 0;
        var total = ActiveImages.Sum(image => (decimal)(image.SizeBytes ?? 0));
        var unknown = ActiveImages.Any(image => image.SizeBytes is null);
        TotalImagesSize.Text = $"{PackImageFileInfo.FormatSize(total)}{(unknown ? " + ?" : string.Empty)}";
        UpdateAddPartitionButton();
    }

    private bool CanAddPartition(string name) => DeliveryValidation.IsPartition(name) && !Model.Container.Any(i => i.PartitionName == name);

    private void UpdateAddPartitionButton() => AddZeroPartitionButton.IsEnabled =
        !IsPayload && !_isAOnly && AutoZeroPartitionsBox.IsChecked != true && CanAddPartition(ZeroPartitionNameBox.Text?.Trim() ?? string.Empty);

    private void OnAddZeroPartitionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = ZeroPartitionNameBox.Text?.Trim() ?? string.Empty;
        if (IsPayload || _isAOnly || AutoZeroPartitionsBox.IsChecked == true || !CanAddPartition(name)) return;
        Model.AddZero(name);
        var item = Model.Container.Last();
        ZeroPartitionNameBox.Text = string.Empty;
        OrderedImages.SelectedItem = item;
        OrderedImages.ScrollIntoView(item);
        UpdateEmptyState();
    }

    private void OnRemoveZeroPartitionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DeliveryImageItem { IsZeroSize: true } item })
        {
            Model.Remove(item);
            UpdateEmptyState();
        }
    }

    internal Rect? GetInlineEditorBounds(Visual relativeTo) =>
        _stacked == false ? ScriptPane.GetInlineEditorBounds(relativeTo) : null;

    internal void SetEditorHost(Border? host)
    {
        _editorExternal = host is not null;
        CompositionLayout.Margin = _editorExternal ? default : new Thickness(0, 0, 12, 0);
        ScriptPane.SetEditorHost(host);
        _stacked = null;
        UpdateCompositionLayout();
    }

    private void UpdateCompositionLayout()
    {
        if (Bounds.Width <= 0) return;
        var stacked = Bounds.Width < (_editorExternal ? 385 : 460);
        var imageMinimum = ZeroPartitionForm.IsVisible ? 372 : 252;
        var contentWidth = Math.Max(0, Bounds.Width - CompositionLayout.Margin.Right);
        var imageWidth = stacked ? contentWidth : Math.Max(180, (contentWidth - 25) / (_editorExternal ? 2 : 4));
        var outputWidth = stacked ? contentWidth : contentWidth - 25 - imageWidth;
        var outputMinimum = ScriptPane.MinimumHeight(outputWidth, imageWidth);
        CompositionLayout.Height = Math.Max(Bounds.Height,
            stacked ? imageMinimum + 25 + outputMinimum : Math.Max(imageMinimum, outputMinimum));
        if (_stacked != stacked)
        {
            _stacked = stacked;
            CompositionLayout.ColumnDefinitions = stacked ? new ColumnDefinitions("*") :
                [new ColumnDefinition(GridLength.Star) { MinWidth = 180 }, new ColumnDefinition(new GridLength(25)),
                    new ColumnDefinition(new GridLength(_editorExternal ? 1 : 3, GridUnitType.Star))
                    { MinWidth = _editorExternal ? 180 : 220 }];
            CompositionLayout.RowDefinitions = new RowDefinitions(stacked ? "*,25,*" : "*");
            Grid.SetColumn(CompositionDivider, stacked ? 0 : 1);
            Grid.SetRow(CompositionDivider, stacked ? 1 : 0);
            Grid.SetColumn(ScriptPane, stacked ? 0 : 2);
            Grid.SetRow(ScriptPane, stacked ? 2 : 0);
            CompositionDivider.Width = stacked ? double.NaN : 1;
            CompositionDivider.Height = stacked ? 1 : double.NaN;
            CompositionDivider.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            CompositionDivider.VerticalAlignment = stacked ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            CompositionDivider.Margin = stacked ? new Thickness(8, 0) : new Thickness(0, 8);
        }
        if (stacked)
        {
            CompositionLayout.RowDefinitions[0].MinHeight = imageMinimum;
            CompositionLayout.RowDefinitions[2].MinHeight = outputMinimum;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelImageDrag();
        base.OnDetachedFromVisualTree(e);
    }
}