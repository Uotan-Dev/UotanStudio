using UotanStudio.Services.Delivery;
using UotanStudio.ViewModels.Shell;
using DeliveryImageItem = UotanStudio.ViewModels.Shell.DeliveryImage;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

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
    internal event EventHandler? Changed;
    internal event EventHandler? OutputRequested;
    internal event EventHandler<Exception>? OperationFailed;
    internal bool AutoZero { get => AutoZeroPartitionsBox.IsChecked == true; set => AutoZeroPartitionsBox.IsChecked = value; }
    private bool? _stacked;
    private bool _outputToDirectory = true;
    private bool _packImages;
    private bool _compositionConfirmed;
    private bool _editorExternal;
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
        Model.Changed += (_, _) => { _compositionConfirmed = Model.Confirmed; UpdateInteractionState(); UpdateEmptyState(); Changed?.Invoke(this, EventArgs.Empty); };
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
        AutoZeroPartitionsBox.IsVisible = !IsPayload;
        SuperMembershipHeader.IsVisible = IsPayload;
        ZeroPartitionForm.IsVisible = !IsPayload && AutoZeroPartitionsBox.IsChecked != true;
        OrderedImages.ItemsSource = ActiveImages;
        UpdateEmptyState();
        UpdateCompositionLayout();
    }

    internal bool SetOutputOptions(bool outputToDirectory, bool packSuper, bool packPayload)
    {
        var packImages = packSuper || packPayload;
        if (_outputToDirectory != outputToDirectory || _packImages != packImages || IsPayload != packPayload)
            _compositionConfirmed = false;
        _outputToDirectory = outputToDirectory;
        _packImages = packImages;
        IsPayload = packPayload;
        var changed = Model.SetMode(outputToDirectory, packSuper, packPayload);
        UpdateInteractionState();
        return changed;
    }

    private void UpdateInteractionState()
    {
        var editingComposition = _packImages && !_compositionConfirmed;
        if (!editingComposition) CancelImageDrag();
        ImageCompositionContent.IsEnabled = editingComposition && !Model.IsRunning;
        var confirmBeforeOutput = _packImages && _outputToDirectory;
        CompositionFooter.IsVisible = !(_packImages && !_outputToDirectory);
        ConfirmCompositionButton.IsEnabled = confirmBeforeOutput && !_compositionConfirmed && !Model.IsRunning;
        // Keep Cancel outside the locked content so a confirmed list can be edited again.
        // 取消按钮不随列表内容锁定，确认后仍可返回编辑。
        CancelCompositionButton.IsEnabled = confirmBeforeOutput && _compositionConfirmed && !Model.IsRunning;
        CompositionOutputFooter.IsVisible = _packImages && !_outputToDirectory;
        ScriptPane.SetInteractionState(_outputToDirectory && (!_packImages || _compositionConfirmed),
            _outputToDirectory || _packImages);
    }

    private void OnConfirmCompositionClick(object? sender, RoutedEventArgs e)
    {
        if (!_packImages || !_outputToDirectory || _compositionConfirmed) return;
        try { Model.Confirm(ReadOptions()); }
        catch (Exception ex) { Model.Status = ex.Message; OperationFailed?.Invoke(this, ex); }
        UpdateInteractionState();
    }

    private void OnCancelCompositionClick(object? sender, RoutedEventArgs e)
    {
        if (!_packImages || !_outputToDirectory || !_compositionConfirmed) return;
        Model.ReturnToContainer();
        UpdateInteractionState();
    }

    internal void SetImages(IReadOnlyList<PackImageSelection> images)
    {
        Model.SelectSources(images.Select(i => new DeliveryFile
        { SourcePath = i.FilePath, FileName = Path.GetFileName(i.FilePath), Partition = Path.GetFileNameWithoutExtension(i.FilePath), SourceGroup = i.GroupName, SourceLength = i.SizeBytes }).ToArray());
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
        _compositionConfirmed = false;
        CompositionOutputProgress.Value = 0;
        UpdateInteractionState();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyImagesHint.IsVisible = ActiveImages.Count == 0;
        var total = ActiveImages.Sum(image => (decimal)(image.SizeBytes ?? 0));
        TotalImagesSize.Text = PackImageFileInfo.FormatSize(total) + (ActiveImages.Any(image => image.SizeBytes is null) ? " + ?" : string.Empty);
        UpdateAddPartitionButton();
    }

    private bool CanAddPartition(string name) => DeliveryValidation.IsPartition(name) && !Model.Container.Any(i => i.PartitionName == name);

    private void UpdateAddPartitionButton() => AddZeroPartitionButton.IsEnabled =
        !IsPayload && AutoZeroPartitionsBox.IsChecked != true && CanAddPartition(ZeroPartitionNameBox.Text?.Trim() ?? string.Empty);

    private void OnAddZeroPartitionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = ZeroPartitionNameBox.Text?.Trim() ?? string.Empty;
        if (IsPayload || AutoZeroPartitionsBox.IsChecked == true || !CanAddPartition(name)) return;
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
