using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.Views.Controls;

internal sealed partial class DeliveryImageItem(string name, string groupName, string? filePath, bool showSuperChoice = false) : ObservableObject
{
    public string Name { get; } = name;
    public string GroupName { get; } = groupName;
    public string? FilePath { get; } = filePath;
    public bool ShowSuperChoice { get; } = showSuperChoice;
    [ObservableProperty] private bool _includeInSuper;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplaySize))] private long? _sizeBytes = filePath is null ? 0 : null;
    public string DisplaySize => SizeBytes is { } bytes ? PackImageFileInfo.FormatSize(bytes) : "—";
    public bool IsZeroSize => FilePath is null;
    public string Description => FilePath is null ? Name + " (0 B)" : GroupName + "\n" + FilePath;
}

/// <summary><para>交付镜像顺序与脚本草稿界面，不执行打包或生成脚本。</para>Local delivery ordering and script draft UI; does not package or generate scripts.</summary>
public partial class DeliveryCompositionView : UserControl
{
    /// <summary><para>是否显示 Payload 编排。</para>Whether to display Payload composition.</summary>
    public static readonly StyledProperty<bool> IsPayloadProperty =
        AvaloniaProperty.Register<DeliveryCompositionView, bool>(nameof(IsPayload));

    private readonly ObservableCollection<DeliveryImageItem> _superImages = [];
    private readonly ObservableCollection<DeliveryImageItem> _payloadImages = [];
    private bool? _stacked;
    private bool _outputToDirectory = true;
    private bool _packImages;
    private bool _compositionConfirmed;
    private ObservableCollection<DeliveryImageItem> ActiveImages => IsPayload ? _payloadImages : _superImages;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary><para>在 Super 与 Payload 镜像编排之间切换。</para>Switches between Super and Payload composition.</summary>
    public bool IsPayload { get => GetValue(IsPayloadProperty); set => SetValue(IsPayloadProperty, value); }

    /// <summary><para>初始化本地镜像列表和文档编辑器。</para>Initializes local image lists and the document editor.</summary>
    public DeliveryCompositionView()
    {
        InitializeComponent();
        AutoZeroPartitionsBox.IsCheckedChanged += (_, _) => UpdateMode();
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

    internal void SetOutputOptions(bool outputToDirectory, bool packSuper, bool packPayload)
    {
        var packImages = packSuper || packPayload;
        if (_outputToDirectory != outputToDirectory || _packImages != packImages || IsPayload != packPayload)
            _compositionConfirmed = false;
        _outputToDirectory = outputToDirectory;
        _packImages = packImages;
        IsPayload = packPayload;
        UpdateInteractionState();
    }

    private void UpdateInteractionState()
    {
        var editingComposition = _packImages && !_compositionConfirmed;
        if (!editingComposition) CancelImageDrag();
        ImageCompositionContent.IsEnabled = editingComposition;
        var confirmBeforeOutput = _packImages && _outputToDirectory;
        CompositionFooter.IsVisible = !(_packImages && !_outputToDirectory);
        ConfirmCompositionButton.IsEnabled = confirmBeforeOutput && !_compositionConfirmed;
        // Keep Cancel outside the locked content so a confirmed list can be edited again.
        // 取消按钮不随列表内容锁定，确认后仍可返回编辑。
        CancelCompositionButton.IsEnabled = confirmBeforeOutput && _compositionConfirmed;
        CompositionOutputFooter.IsVisible = _packImages && !_outputToDirectory;
        ScriptPane.SetInteractionState(_outputToDirectory && (!_packImages || _compositionConfirmed),
            _outputToDirectory || _packImages);
    }

    private void OnConfirmCompositionClick(object? sender, RoutedEventArgs e)
    {
        if (!_packImages || !_outputToDirectory || _compositionConfirmed) return;
        _compositionConfirmed = true;
        UpdateInteractionState();
    }

    private void OnCancelCompositionClick(object? sender, RoutedEventArgs e)
    {
        if (!_packImages || !_outputToDirectory || !_compositionConfirmed) return;
        _compositionConfirmed = false;
        UpdateInteractionState();
    }

    internal void SetImages(IReadOnlyList<PackImageSelection> images)
    {
        var selected = images.DistinctBy(image => image.FilePath, PathComparer).ToArray();
        ScriptPane.SetImages(selected);
        var paths = selected.Select(image => image.FilePath).ToHashSet(PathComparer);
        var rows = ActiveImages;
        var selectionChanged = false;
        for (var index = rows.Count - 1; index >= 0; index--)
            if (rows[index].FilePath is { } path && !paths.Contains(path))
            {
                rows.RemoveAt(index);
                selectionChanged = true;
            }
        var existing = rows.Where(row => row.FilePath is not null).ToDictionary(row => row.FilePath!, PathComparer);
        foreach (var image in selected)
        {
            if (!existing.TryGetValue(image.FilePath, out var row))
            {
                row = new DeliveryImageItem(Path.GetFileName(image.FilePath), image.GroupName, image.FilePath, IsPayload);
                rows.Add(row);
                selectionChanged = true;
            }
            selectionChanged |= row.SizeBytes != image.SizeBytes;
            row.SizeBytes = image.SizeBytes;
        }
        if (selectionChanged && _compositionConfirmed)
        {
            _compositionConfirmed = false;
            UpdateInteractionState();
        }
        if (_draggedImage is not null && !rows.Contains(_draggedImage)) CancelImageDrag();
        UpdateEmptyState();
    }

    internal void Reset()
    {
        CancelImageDrag();
        _superImages.Clear();
        _payloadImages.Clear();
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

    private bool CanAddPartition(string name) => name.Length is > 0 and <= 64 && name is not "." and not ".." &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') &&
        !_superImages.Any(image => string.Equals(image.IsZeroSize ? image.Name : Path.GetFileNameWithoutExtension(image.Name),
            name, StringComparison.OrdinalIgnoreCase));

    private void UpdateAddPartitionButton() => AddZeroPartitionButton.IsEnabled =
        !IsPayload && AutoZeroPartitionsBox.IsChecked != true && CanAddPartition(ZeroPartitionNameBox.Text?.Trim() ?? string.Empty);

    private void OnAddZeroPartitionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = ZeroPartitionNameBox.Text?.Trim() ?? string.Empty;
        if (IsPayload || AutoZeroPartitionsBox.IsChecked == true || !CanAddPartition(name)) return;
        var item = new DeliveryImageItem(name, string.Empty, null);
        _superImages.Add(item);
        ZeroPartitionNameBox.Text = string.Empty;
        OrderedImages.SelectedItem = item;
        OrderedImages.ScrollIntoView(item);
        UpdateEmptyState();
    }

    private void OnRemoveZeroPartitionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DeliveryImageItem { IsZeroSize: true } item })
        {
            _superImages.Remove(item);
            UpdateEmptyState();
        }
    }

    private void UpdateCompositionLayout()
    {
        if (Bounds.Width <= 0) return;
        var stacked = Bounds.Width < 460;
        var imageMinimum = ZeroPartitionForm.IsVisible ? 372 : 252;
        var contentWidth = Math.Max(0, Bounds.Width - 12);
        var imageWidth = stacked ? contentWidth : Math.Max(180, (contentWidth - 25) / 4);
        var outputWidth = stacked ? contentWidth : contentWidth - 25 - imageWidth;
        var outputMinimum = DeliveryOutputView.MinimumHeight(outputWidth, imageWidth);
        CompositionLayout.Height = Math.Max(Bounds.Height,
            stacked ? imageMinimum + 25 + outputMinimum : Math.Max(imageMinimum, outputMinimum));
        if (_stacked != stacked)
        {
            _stacked = stacked;
            CompositionLayout.ColumnDefinitions = stacked ? new ColumnDefinitions("*") :
                [new ColumnDefinition(GridLength.Star) { MinWidth = 180 }, new ColumnDefinition(new GridLength(25)),
                    new ColumnDefinition(new GridLength(3, GridUnitType.Star)) { MinWidth = 220 }];
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
