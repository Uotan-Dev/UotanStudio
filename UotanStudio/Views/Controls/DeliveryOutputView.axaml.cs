using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.Views.Controls;

internal sealed partial class DeliveryOutputImage(string filePath) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public string FileName { get; } = Path.GetFileName(filePath);
    [ObservableProperty] private string _partitionName = Path.GetFileNameWithoutExtension(filePath);
    [ObservableProperty] private bool _isOptional;
}

internal sealed class DeliveryScriptDocument(string title)
{
    public string Title { get; } = title;
    public TextDocument Document { get; } = new();
    public int CaretOffset { get; set; }
}

/// <summary><para>交付输出选项与多文档草稿编辑，仅保存界面状态。</para>Delivery output options and multiple editable drafts, held only in UI state.</summary>
public partial class DeliveryOutputView : UserControl
{
    private const double MinimumSettingsHeight = 340;

    /// <summary><para>输出设置栏与相邻镜像列表共用的宽度。</para>Width shared by the output settings and the neighboring image list.</summary>
    public static readonly StyledProperty<double> SettingsWidthProperty =
        AvaloniaProperty.Register<DeliveryOutputView, double>(nameof(SettingsWidth), 180);

    /// <summary><para>获取或设置输出设置栏的宽度。</para>Gets or sets the width of the output settings pane.</summary>
    public double SettingsWidth { get => GetValue(SettingsWidthProperty); set => SetValue(SettingsWidthProperty, value); }

    private readonly ObservableCollection<DeliveryOutputImage> _images = [];
    private readonly Dictionary<string, DeliveryOutputImage> _imageOptions = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ObservableCollection<DeliveryScriptDocument> _documents = [];
    private DeliveryScriptDocument? _activeDocument;
    private int _documentNumber;
    private bool? _stacked;
    private Border? _editorHost;

    /// <summary><para>初始化输出表单和第一份空白草稿。</para>Initializes the output form and first empty draft.</summary>
    public DeliveryOutputView()
    {
        InitializeComponent();
        OutputImages.ItemsSource = _images;
        DocumentTabs.ItemsSource = _documents;
        ScriptEditor.Options.AllowScrollBelowDocument = false;
        SizeChanged += (_, _) => UpdateOutputLayout();
        ScriptSettingsExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty || !ScriptSettingsExpander.IsExpanded) return;
            OutputImages.CommitEdit();
            OutputImagesExpander.IsExpanded = false;
        };
        AddDocument();
    }

    internal void SetInteractionState(bool outputEnabled, bool editorEnabled)
    {
        if (!outputEnabled && OutputSettings.IsEnabled) OutputImages.CommitEdit();
        OutputSettings.IsEnabled = outputEnabled;
        DocumentPane.IsEnabled = editorEnabled;
    }

    internal void SetImages(IReadOnlyList<PackImageSelection> images)
    {
        var paths = images.Select(image => image.FilePath).ToHashSet(_imageOptions.Comparer);
        for (var index = _images.Count - 1; index >= 0; index--)
            if (!paths.Contains(_images[index].FilePath)) _images.RemoveAt(index);
        var existing = _images.Select(image => image.FilePath).ToHashSet(_imageOptions.Comparer);
        foreach (var image in images)
        {
            if (!existing.Add(image.FilePath)) continue;
            if (!_imageOptions.TryGetValue(image.FilePath, out var row))
                _imageOptions.Add(image.FilePath, row = new DeliveryOutputImage(image.FilePath));
            _images.Add(row);
        }
    }

    internal void Reset()
    {
        OutputImages.CancelEdit();
        _images.Clear();
        _imageOptions.Clear();
        TxtScriptBox.IsChecked = true;
        BatchShellScriptBox.IsChecked = false;
        RecoveryScriptBox.IsChecked = false;
        DeviceCodenameBox.Text = string.Empty;
        AuthorInfoBox.Text = string.Empty;
        OutputImagesExpander.IsExpanded = true;
        ScriptSettingsExpander.IsExpanded = false;
        OutputProgress.Value = 0;
        _activeDocument = null;
        _documents.Clear();
        _documentNumber = 0;
        AddDocument();
    }

    private void AddDocument()
    {
        var number = ++_documentNumber;
        var document = new DeliveryScriptDocument(number == 1 ? "script.txt" : $"script-{number}.txt");
        _documents.Add(document);
        DocumentTabs.SelectedItem = document;
        DocumentTabs.ScrollIntoView(document);
    }

    private void OnAddDocumentClick(object? sender, RoutedEventArgs e) => AddDocument();

    private void OnCloseDocumentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DeliveryScriptDocument document }) return;
        var index = _documents.IndexOf(document);
        if (index < 0) return;
        var wasActive = ReferenceEquals(document, _activeDocument);
        _documents.RemoveAt(index);
        if (_documents.Count == 0) AddDocument();
        else if (wasActive) DocumentTabs.SelectedItem = _documents[Math.Min(index, _documents.Count - 1)];
        e.Handled = true;
    }

    private void OnDocumentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentTabs.SelectedItem is not DeliveryScriptDocument document || ReferenceEquals(document, _activeDocument)) return;
        if (_activeDocument is not null) _activeDocument.CaretOffset = ScriptEditor.CaretOffset;
        _activeDocument = document;
        // Swap documents rather than their text so each draft retains its own undo history.
        // 切换文档对象，保留各草稿独立的撤销历史。
        ScriptEditor.Document = document.Document;
        ScriptEditor.CaretOffset = Math.Min(document.CaretOffset, document.Document.TextLength);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SettingsWidthProperty && OutputLayout is not null) UpdateOutputLayout();
    }

    internal Rect? GetInlineEditorBounds(Visual relativeTo) =>
        _editorHost is null && _stacked == false && DocumentPane.TranslatePoint(default, relativeTo) is { } position
            ? new Rect(position, DocumentPane.Bounds.Size) : null;

    internal void SetEditorHost(Border? host)
    {
        if (ReferenceEquals(host, _editorHost)) return;
        OutputImages.CommitEdit();
        if (_editorHost is not null) _editorHost.Child = null;
        else OutputLayout.Children.Remove(DocumentPane);
        _editorHost = host;
        _stacked = null;
        if (host is not null)
        {
            Grid.SetColumn(DocumentPane, 0);
            Grid.SetRow(DocumentPane, 0);
            host.Child = DocumentPane;
            OutputLayout.ColumnDefinitions = new ColumnDefinitions("*");
            OutputLayout.RowDefinitions = new RowDefinitions("*");
        }
        else OutputLayout.Children.Add(DocumentPane);
        UpdateOutputLayout();
    }

    internal double MinimumHeight(double width, double settingsWidth) =>
        _editorHost is null && width < settingsWidth + 12 + 220 ? MinimumSettingsHeight + 12 + 220 : MinimumSettingsHeight;

    private void UpdateOutputLayout()
    {
        if (Bounds.Width <= 0 || _editorHost is not null) return;
        var settingsWidth = Math.Max(180, SettingsWidth);
        var stacked = Bounds.Width < settingsWidth + 12 + 220;
        if (!stacked && _stacked == false)
            OutputLayout.ColumnDefinitions[0].Width = new GridLength(settingsWidth);
        if (_stacked == stacked) return;
        _stacked = stacked;
        OutputLayout.ColumnDefinitions = stacked ? new ColumnDefinitions("*") :
            [new ColumnDefinition(new GridLength(settingsWidth)), new ColumnDefinition(new GridLength(12)),
                new ColumnDefinition(GridLength.Star) { MinWidth = 220 }];
        OutputLayout.RowDefinitions = stacked ?
            [new RowDefinition(GridLength.Star) { MinHeight = MinimumSettingsHeight }, new RowDefinition(new GridLength(12)),
                new RowDefinition(GridLength.Star) { MinHeight = 220 }] : new RowDefinitions("*");
        Grid.SetColumn(DocumentPane, stacked ? 0 : 2);
        Grid.SetRow(DocumentPane, stacked ? 2 : 0);
    }
}
