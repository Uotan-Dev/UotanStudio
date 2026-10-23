using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using UotanStudio.Delivery;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Controls;

internal sealed class DeliveryScriptDocument(string title)
{
    public string Title { get; } = title;
    public TextDocument Document { get; } = new();
    public int CaretOffset { get; set; }
    internal string? Baseline { get; set; }
    internal bool Modified => Baseline is not null && Baseline != Document.Text;
}

/// <summary>Delivery options, generated scripts and persistent drafts using the existing controls. <para>使用现有控件维护交付选项、生成文本和持久化草稿。</para></summary>
public partial class DeliveryOutputView : UserControl
{
    private const double MinimumSettingsHeight = 340;

    /// <summary>Width shared by the output settings and the neighboring image list. <para>输出设置栏与相邻镜像列表共用的宽度。</para></summary>
    public static readonly StyledProperty<double> SettingsWidthProperty =
        AvaloniaProperty.Register<DeliveryOutputView, double>(nameof(SettingsWidth), 180);

    /// <summary>Gets or sets the width of the output settings pane. <para>获取或设置输出设置栏的宽度。</para></summary>
    public double SettingsWidth { get => GetValue(SettingsWidthProperty); set => SetValue(SettingsWidthProperty, value); }

    private DeliveryViewModel? _model;
    private readonly Dictionary<string, DeliveryScriptDocument> _allDocuments = [];
    private bool _syncing;
    private bool _txtScriptBlockedByZstd;
    private bool _txtScriptBeforeZstd = true;
    private string _fingerprint = "";
    internal event EventHandler? Changed;
    internal event EventHandler? OutputRequested;
    internal event EventHandler? GenerateRequested;
    internal event EventHandler? SaveRequested;
    internal bool RecoveryRequested => RecoveryScriptBox.IsChecked == true
        && (_model?.PackPayload == true || IsRecoverySupported());
    private readonly ObservableCollection<DeliveryScriptDocument> _documents = [];
    private DeliveryScriptDocument? _activeDocument;
    private bool? _stacked;
    private Border? _editorHost;
    private bool _editorEnabled = true;

    /// <summary>Initializes the output form and empty editor pane. <para>初始化输出表单和无文档的编辑区域。</para></summary>
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
        GenerateScriptButton.Click += (_, _) => GenerateRequested?.Invoke(this, EventArgs.Empty);
        SaveDocumentButton.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
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

    internal void SetInteractionState(bool outputEnabled, bool editorEnabled, bool emptyOnly)
    {
        if (!outputEnabled && OutputSettings.IsEnabled) OutputImages.CommitEdit();
        OutputSettings.IsEnabled = outputEnabled;

        _editorEnabled = outputEnabled && editorEnabled;
        DocumentPane.IsEnabled = _editorEnabled && _model?.IsRunning != true;
        ScriptSettingsExpander.IsEnabled = outputEnabled && _model?.PackPayload != true;

        OutputFooter.IsEnabled = outputEnabled || emptyOnly;
    }

    private void NotifyChanged() { if (!_syncing) Changed?.Invoke(this, EventArgs.Empty); }
    internal void SetModel(DeliveryViewModel model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.Changed -= OnModelChanged;
        }
        _model = model;
        _model.PropertyChanged += OnModelPropertyChanged;
        _model.Changed += OnModelChanged;
        OutputImages.ItemsSource = model.Output;
        ResetScriptOptions();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeliveryViewModel.PackPayload)) ResetScriptOptions();
        UpdateRecoveryAvailability();
        OutputStatusText.Text = _model?.ProgressText;
        OutputStatusText.IsVisible = !string.IsNullOrEmpty(OutputStatusText.Text);
        OutputStatsText.Text = _model?.ProgressStats;
        OutputStatsText.IsVisible = !string.IsNullOrEmpty(OutputStatsText.Text);
    }

    private void OnModelChanged(object? sender, EventArgs e) => UpdateRecoveryAvailability();

    private void ResetScriptOptions()
    {
        var payload = _model?.PackPayload == true;
        var previous = _syncing;
        _syncing = true;
        try
        {
            TxtScriptBox.IsEnabled = !payload && !_txtScriptBlockedByZstd;
            BatchShellScriptBox.IsEnabled = !payload;
            TxtScriptBox.IsChecked = !payload && !_txtScriptBlockedByZstd;
            BatchShellScriptBox.IsChecked = false;
            RecoveryScriptBox.IsChecked = payload;
            RecoveryScriptBox.IsEnabled = !payload;
        }
        finally { _syncing = previous; }
        ScriptSettingsExpander.IsEnabled = OutputSettings.IsEnabled && !payload;
        UpdateRecoveryAvailability();
        NotifyChanged();
    }

    /// <summary>Disables the legacy TXT manifest when an image requires ZST decompression.</summary>
    internal void SetTxtScriptBlockedByZstd(bool blocked)
    {
        var changed = _txtScriptBlockedByZstd != blocked;
        if (!changed) return;
        if (blocked) _txtScriptBeforeZstd = TxtScriptBox.IsChecked == true;
        _txtScriptBlockedByZstd = blocked;
        var previous = _syncing;
        _syncing = true;
        try
        {
            if (blocked)
            {
                TxtScriptBox.IsChecked = false;
                TxtScriptBox.IsEnabled = false;
            }
            else if (_model?.PackPayload != true)
            {
                TxtScriptBox.IsEnabled = true;
                TxtScriptBox.IsChecked = _txtScriptBeforeZstd;
            }
        }
        finally { _syncing = previous; }
        NotifyChanged();
    }

    private bool IsRecoverySupported()
    {
        if (_model is null || _model.PackPayload || !_model.OrganizeDirectory) return false;
        if (_model.PackSuper) return _model.Output.Any(i => i.Generated && i.PartitionName == "super");
        var files = _model.Output.Where(i => !i.Generated).Select(i => i.Snapshot()).ToArray();
        return files.Length > 0
            && files.Any(file => !file.Optional || file.Default)
            && files.All(file => !file.Generated && !file.IncludeInSuper && !file.Fastbootd
                && file.Partition is not ("super" or "super_empty")
                && DeliveryPackageBuilder.IsRecoveryArtifact(file.FileName));
    }

    private void UpdateRecoveryAvailability()
    {
        if (_model?.PackPayload == true)
        {
            RecoveryScriptBox.IsChecked = true;
            RecoveryScriptBox.IsEnabled = false;
            return;
        }

        var supported = IsRecoverySupported();
        var previous = _syncing;
        _syncing = true;
        try
        {
            RecoveryScriptBox.IsEnabled = supported;
            if (!supported) RecoveryScriptBox.IsChecked = false;
        }
        finally { _syncing = previous; }
    }
    internal void SetRunning(bool running, double progress)
    {
        OutputProgress.Value = progress;
        StartOutputButton.Classes.Set("running", running);
        OutputImages.IsEnabled = !running; ScriptSettingsExpander.IsEnabled = !running; DocumentPane.IsEnabled = _editorEnabled && !running;
    }
    internal DeliveryPlan Capture(DeliveryPlan plan)
    {
        OutputImages.CommitEdit();
        if (_activeDocument is not null) _activeDocument.CaretOffset = ScriptEditor.CaretOffset;
        var files = _model!.Output.Select(i => i.Snapshot()).ToList();
        return plan with
        {
            Package = plan.Package with { Ota = plan.PackPayload },
            Files = files,
            Recovery = RecoveryRequested,
            Txt = TxtScriptBox.IsChecked == true && !_txtScriptBlockedByZstd,
            TxtVariants = true,
            Windows = BatchShellScriptBox.IsChecked == true,
            Linux = BatchShellScriptBox.IsChecked == true,
            MacOS = BatchShellScriptBox.IsChecked == true,
            AskWipe = false,
            Reboot = false,
            Slot = "",
            LocalZstd = [],
            DeviceCodename = DeviceCodenameBox.Text?.Trim() ?? "",
            Information = AuthorInfoBox.Text ?? "",
            Scripts = _allDocuments.ToDictionary(p => p.Key, p => p.Value.Document.Text),
            ScriptFingerprint = _fingerprint,
            GeneratedScripts = _allDocuments.Where(p => p.Value.Baseline is not null).ToDictionary(p => p.Key, p => p.Value.Baseline!),
            OpenScripts = [.. _documents.Select(d => d.Title)],
            ScriptCarets = _allDocuments.ToDictionary(p => p.Key, p => p.Value.CaretOffset)
        };
    }
    internal bool SynchronizeScripts(DeliveryPlan plan, bool replaceModified = false, bool force = false)
    {
        var fingerprint = DeliveryScriptGenerator.Fingerprint(plan);
        if (_fingerprint == fingerprint && !force) return true;

        var generated = DeliveryScriptGenerator.Generate(plan, id => LanguageService.Instance.GetString("Delivery_Script_" + id));
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
            RecoveryScriptBox.IsEnabled = !plan.PackPayload;
            TxtScriptBox.IsChecked = !plan.PackPayload && plan.Txt;
            BatchShellScriptBox.IsChecked = !plan.PackPayload && (plan.Windows || plan.Linux || plan.MacOS);
            DeviceCodenameBox.Text = plan.DeviceCodename; AuthorInfoBox.Text = plan.Information;
            _documents.Clear(); _allDocuments.Clear(); _activeDocument = null;

            _fingerprint = "";
            ClearEditor();
            UpdateRecoveryAvailability();
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