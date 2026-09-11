using UotanStudio.Services.Delivery;
using UotanStudio.ViewModels.Shell;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.Views.Controls;

internal sealed class DeliveryScriptDocument(string title)
{
    public string Title { get; } = title;
    public TextDocument Document { get; } = new();
    public int CaretOffset { get; set; }
    internal string? Baseline { get; set; }
    internal bool Modified => Baseline is not null && Baseline != Document.Text;
}

/// <summary><para>使用现有控件维护交付选项、生成文本和持久化草稿。</para>Delivery options, generated scripts and persistent drafts using the existing controls.</summary>
public partial class DeliveryOutputView : UserControl
{
    private const double MinimumSettingsHeight = 340;

    /// <summary><para>输出设置栏与相邻镜像列表共用的宽度。</para>Width shared by the output settings and the neighboring image list.</summary>
    public static readonly StyledProperty<double> SettingsWidthProperty =
        AvaloniaProperty.Register<DeliveryOutputView, double>(nameof(SettingsWidth), 180);

    /// <summary><para>获取或设置输出设置栏的宽度。</para>Gets or sets the width of the output settings pane.</summary>
    public double SettingsWidth { get => GetValue(SettingsWidthProperty); set => SetValue(SettingsWidthProperty, value); }

    private DeliveryViewModel? _model;
    private readonly Dictionary<string, DeliveryScriptDocument> _allDocuments = [];
    private bool _syncing;
    private string _fingerprint = "";
    internal event EventHandler? Changed;
    internal event EventHandler? OutputRequested;
    internal bool RecoveryRequested => RecoveryScriptBox.IsChecked == true;
    private readonly ObservableCollection<DeliveryScriptDocument> _documents = [];
    private DeliveryScriptDocument? _activeDocument;
    private bool? _stacked;
    private Border? _editorHost;

    /// <summary><para>初始化输出表单和无文档的编辑区域。</para>Initializes the output form and empty editor pane.</summary>
    public DeliveryOutputView()
    {
        InitializeComponent();

        DocumentTabs.ItemsSource = _documents;
        ScriptEditor.Options.AllowScrollBelowDocument = false;
        SizeChanged += (_, _) => UpdateOutputLayout();
        ScriptSettingsExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty || !ScriptSettingsExpander.IsExpanded) return;
            OutputImages.CommitEdit();
            OutputImagesExpander.IsExpanded = false;
        };
        StartOutputButton.Click += (_, _) => OutputRequested?.Invoke(this, EventArgs.Empty);
        foreach (var box in new[] { TxtScriptBox, BatchShellScriptBox, RecoveryScriptBox })
            box.IsCheckedChanged += (_, _) => NotifyChanged();
        DeviceCodenameBox.TextChanged += (_, _) => NotifyChanged();
        AuthorInfoBox.TextChanged += (_, _) => NotifyChanged();
        ScriptEditor.TextChanged += (_, _) => NotifyChanged();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.T || !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) return;
            if (_allDocuments.Values.LastOrDefault(d => !_documents.Contains(d)) is not { } closed) return;
            _documents.Add(closed); DocumentTabs.SelectedItem = closed;
            e.Handled = true; NotifyChanged();
        };
    }

    internal void SetInteractionState(bool outputEnabled, bool editorEnabled)
    {
        if (!outputEnabled && OutputSettings.IsEnabled) OutputImages.CommitEdit();
        OutputSettings.IsEnabled = outputEnabled;
        DocumentPane.IsEnabled = editorEnabled;
    }

    private void NotifyChanged() { if (!_syncing) Changed?.Invoke(this, EventArgs.Empty); }
    internal void SetModel(DeliveryViewModel model)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelPropertyChanged;
        _model = model;
        _model.PropertyChanged += OnModelPropertyChanged;
        OutputImages.ItemsSource = model.Output;
        ResetScriptOptions();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeliveryViewModel.PackPayload)) ResetScriptOptions();
    }

    private void ResetScriptOptions()
    {
        var payload = _model?.PackPayload == true;
        var previous = _syncing;
        _syncing = true;
        try
        {
            TxtScriptBox.IsEnabled = !payload;
            BatchShellScriptBox.IsEnabled = !payload;
            TxtScriptBox.IsChecked = !payload;
            BatchShellScriptBox.IsChecked = false;
            RecoveryScriptBox.IsChecked = payload;
        }
        finally { _syncing = previous; }
        NotifyChanged();
    }
    internal void SetRunning(bool running, double progress)
    {
        OutputProgress.Value = progress;
        StartOutputButton.Classes.Set("running", running);
        OutputImages.IsEnabled = !running; ScriptSettingsExpander.IsEnabled = !running; DocumentPane.IsEnabled = !running;
    }
    internal DeliveryPlan Capture(DeliveryPlan plan)
    {
        OutputImages.CommitEdit();
        if (_activeDocument is not null) _activeDocument.CaretOffset = ScriptEditor.CaretOffset;
        var files = _model!.Output.Select(i => i.Snapshot()).ToList();
        return plan with
        {
            Package = new(),
            Files = files, Recovery = RecoveryScriptBox.IsChecked == true, Txt = TxtScriptBox.IsChecked == true, TxtVariants = true,
            Windows = BatchShellScriptBox.IsChecked == true, Linux = BatchShellScriptBox.IsChecked == true, MacOS = BatchShellScriptBox.IsChecked == true,
            AskWipe = false, Reboot = false,
            Slot = "",
            LocalZstd = [],
            DeviceCodename = DeviceCodenameBox.Text?.Trim() ?? "", Information = AuthorInfoBox.Text ?? "",
            Scripts = _allDocuments.ToDictionary(p => p.Key, p => p.Value.Document.Text),
            ScriptFingerprint = _fingerprint,
            GeneratedScripts = _allDocuments.Where(p => p.Value.Baseline is not null).ToDictionary(p => p.Key, p => p.Value.Baseline!),
            OpenScripts = _documents.Select(d => d.Title).ToList(),
            ScriptCarets = _allDocuments.ToDictionary(p => p.Key, p => p.Value.CaretOffset)
        };
    }
    internal bool SynchronizeScripts(DeliveryPlan plan, bool replaceModified = false)
    {
        var fingerprint = DeliveryScriptGenerator.Fingerprint(plan);
        if (_fingerprint == fingerprint) return true;
        var generated = DeliveryScriptGenerator.Generate(plan);
        if (!replaceModified && (_allDocuments.Values.Any(d => d.Modified)
            || generated.Keys.Any(name => _allDocuments.TryGetValue(name, out var doc) && doc.Baseline is null && doc.Document.TextLength > 0))) return false;
        _syncing = true;
        try
        {
            foreach (var doc in _allDocuments.Values.Where(d => d.Baseline is not null && !generated.ContainsKey(d.Title)).ToArray())
            { _documents.Remove(doc); _allDocuments.Remove(doc.Title); }
            foreach (var (name, text) in generated)
            {
                if (!_allDocuments.TryGetValue(name, out var doc))
                { doc = new(name); _allDocuments.Add(name, doc); _documents.Add(doc); }
                doc.Document.Text = text; doc.Baseline = text;
            }
            if (_activeDocument is null || !_allDocuments.ContainsKey(_activeDocument.Title))
            {
                _activeDocument = null;
                if (_documents.Count > 0) DocumentTabs.SelectedItem = _documents[0];
                else ClearEditor();
            }
            _fingerprint = fingerprint;
            return true;
        }
        finally { _syncing = false; }
    }
    internal void Restore(DeliveryPlan plan)
    {
        _syncing = true;
        try
        {
            Reset();
            RecoveryScriptBox.IsChecked = plan.PackPayload || plan.Recovery;
            TxtScriptBox.IsChecked = !plan.PackPayload && plan.Txt;
            BatchShellScriptBox.IsChecked = !plan.PackPayload && (plan.Windows || plan.Linux || plan.MacOS);
            DeviceCodenameBox.Text = plan.DeviceCodename; AuthorInfoBox.Text = plan.Information;
            _documents.Clear(); _allDocuments.Clear(); _activeDocument = null;
            foreach (var (name, text) in plan.Scripts)
            {
                // Drop only the untouched placeholder created by older versions; retain saved edits.
                // 仅忽略旧版自动创建且未编辑的占位文档，保留已保存的编辑内容。
                if (name == "script.txt" && text.Length == 0 && !plan.GeneratedScripts.ContainsKey(name)) continue;
                var doc = new DeliveryScriptDocument(name) { Baseline = plan.GeneratedScripts.GetValueOrDefault(name), CaretOffset = Math.Max(0, plan.ScriptCarets.GetValueOrDefault(name)) };
                doc.Document.Text = text; _allDocuments.Add(name, doc);
                if (plan.OpenScripts.Contains(name)) _documents.Add(doc);
            }
            _fingerprint = plan.ScriptFingerprint;
            if (_documents.Count > 0) DocumentTabs.SelectedItem = _documents[0];
            else ClearEditor();
        }
        finally { _syncing = false; }
    }

    internal void Reset()
    {
        OutputImages.CancelEdit();
        _allDocuments.Clear(); _fingerprint = "";
        ResetScriptOptions();
        DeviceCodenameBox.Text = string.Empty;
        AuthorInfoBox.Text = string.Empty;
        OutputImagesExpander.IsExpanded = true;
        ScriptSettingsExpander.IsExpanded = false;
        OutputProgress.Value = 0;
        _activeDocument = null;
        _documents.Clear();
        ClearEditor();
    }

    private void ClearEditor()
    {
        _activeDocument = null;
        ScriptEditor.Document = new();
        ScriptEditor.IsReadOnly = true;
    }

    private void OnCloseDocumentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DeliveryScriptDocument document }) return;
        var index = _documents.IndexOf(document);
        if (index < 0) return;
        var wasActive = ReferenceEquals(document, _activeDocument);
        _documents.RemoveAt(index);
        if (_documents.Count == 0) ClearEditor();
        else if (wasActive) DocumentTabs.SelectedItem = _documents[Math.Min(index, _documents.Count - 1)];
        e.Handled = true;
        NotifyChanged();
    }

    private void OnDocumentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentTabs.SelectedItem is not DeliveryScriptDocument document || ReferenceEquals(document, _activeDocument)) return;
        if (_activeDocument is not null) _activeDocument.CaretOffset = ScriptEditor.CaretOffset;
        _activeDocument = document;
        // Swap documents rather than their text so each draft retains its own undo history.
        // 切换文档对象，保留各草稿独立的撤销历史。
        ScriptEditor.Document = document.Document;
        ScriptEditor.IsReadOnly = false;
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
        DocumentTabs.Classes.Set("inline-editor", host is null);
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
