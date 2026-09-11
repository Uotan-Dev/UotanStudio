using UotanStudio.Services.Delivery;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Services;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using UotanStudio.Services.Projects;
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
    private int _documentNumber;
    private bool? _stacked;
    private Border? _editorHost;
    private bool _pickingArtifact;

    private async void OnBrowseArtifactClick(object? sender, RoutedEventArgs e)
    {
        if (_pickingArtifact || sender is not Button { Tag: string name } || this.FindControl<TextBox>(name) is not { } field
            || TopLevel.GetTopLevel(this) is not { StorageProvider.CanOpen: true } owner) return;
        _pickingArtifact = true;
        var project = ProjectContextService.Instance.ActiveSubProject?.RootPath;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = field.PlaceholderText ?? "选择文件 / Select file", AllowMultiple = false });
            using var file = files.FirstOrDefault();
            if (project == ProjectContextService.Instance.ActiveSubProject?.RootPath && field.IsEffectivelyEnabled && file?.TryGetLocalPath() is { } path)
                field.Text = path;
        }
        catch (Exception ex)
        {
            if (owner is Window window) await DialogService.ShowAsync(window, "Delivery", ex.Message, Ursa.Controls.MessageBoxIcon.Error);
        }
        finally { _pickingArtifact = false; }
    }

    /// <summary><para>初始化输出表单和第一份空白草稿。</para>Initializes the output form and first empty draft.</summary>
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
        foreach (var box in new[] { TxtScriptBox, BatchShellScriptBox, LinuxScriptBox, MacScriptBox, AskWipeBox, RebootBox, OtaZipBox, RecoveryScriptBox })
            box.IsCheckedChanged += (_, _) => NotifyChanged();
        FlashSlotBox.SelectionChanged += (_, _) => NotifyChanged();
        foreach (var box in new[] { WindowsZstdBox, LinuxZstdBox, MacZstdBox, PackageCertificateBox, PackageKeyBox, BuildFingerprintBox, IncrementalBox, SdkLevelBox }) box.TextChanged += (_, _) => NotifyChanged();
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
        AddDocument();
    }

    internal void SetInteractionState(bool outputEnabled, bool editorEnabled)
    {
        if (!outputEnabled && OutputSettings.IsEnabled) OutputImages.CommitEdit();
        OutputSettings.IsEnabled = outputEnabled;
        DocumentPane.IsEnabled = editorEnabled;
    }

    private void NotifyChanged() { if (!_syncing) Changed?.Invoke(this, EventArgs.Empty); }
    internal void SetModel(DeliveryViewModel model) { _model = model; OutputImages.ItemsSource = model.Output; }
    internal void SetRunning(bool running, double progress)
    {
        OutputProgress.Value = progress;
        StartOutputButton.Content = running ? "取消 / Cancel" : LanguageService.Instance.GetString("Delivery_StartOutput");
        OutputImages.IsEnabled = !running; ScriptSettingsExpander.IsEnabled = !running; DocumentPane.IsEnabled = !running;
    }
    internal DeliveryPlan Capture(DeliveryPlan plan)
    {
        OutputImages.CommitEdit();
        if (_activeDocument is not null) _activeDocument.CaretOffset = ScriptEditor.CaretOffset;
        var files = _model!.Output.Select(i => i.Snapshot()).ToList();
        return plan with
        {
            Package = new() { Ota = OtaZipBox.IsChecked == true, CertificatePath = PackageCertificateBox.Text?.Trim() ?? "",
                SigningKeyPath = PackageKeyBox.Text?.Trim() ?? "", BuildFingerprint = BuildFingerprintBox.Text?.Trim() ?? "",
                Incremental = IncrementalBox.Text?.Trim() ?? "", SdkLevel = uint.TryParse(SdkLevelBox.Text, out var sdk) ? sdk : 0 },
            Files = files, Recovery = RecoveryScriptBox.IsChecked == true, Txt = TxtScriptBox.IsChecked == true, TxtVariants = true,
            Windows = BatchShellScriptBox.IsChecked == true, Linux = LinuxScriptBox.IsChecked == true, MacOS = MacScriptBox.IsChecked == true,
            AskWipe = AskWipeBox.IsChecked == true, Reboot = RebootBox.IsChecked == true,
            Slot = FlashSlotBox.SelectedIndex switch { 1 => "a", 2 => "b", 3 => "all", _ => "" },
            LocalZstd = new() { ["Windows"] = WindowsZstdBox.Text?.Trim() ?? "", ["Linux"] = LinuxZstdBox.Text?.Trim() ?? "", ["macOS"] = MacZstdBox.Text?.Trim() ?? "" },
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
                else ScriptEditor.Document = new();
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
            RecoveryScriptBox.IsChecked = plan.Recovery;
            OtaZipBox.IsChecked = plan.Package.Ota; PackageCertificateBox.Text = plan.Package.CertificatePath;
            PackageKeyBox.Text = plan.Package.SigningKeyPath; BuildFingerprintBox.Text = plan.Package.BuildFingerprint;
            IncrementalBox.Text = plan.Package.Incremental; SdkLevelBox.Text = plan.Package.SdkLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtScriptBox.IsChecked = plan.Txt; BatchShellScriptBox.IsChecked = plan.Windows;
            LinuxScriptBox.IsChecked = plan.Linux; MacScriptBox.IsChecked = plan.MacOS;
            AskWipeBox.IsChecked = plan.AskWipe; RebootBox.IsChecked = plan.Reboot;
            FlashSlotBox.SelectedIndex = plan.Slot switch { "a" => 1, "b" => 2, "all" => 3, _ => 0 };
            WindowsZstdBox.Text = plan.LocalZstd.GetValueOrDefault("Windows", "");
            LinuxZstdBox.Text = plan.LocalZstd.GetValueOrDefault("Linux", "");
            MacZstdBox.Text = plan.LocalZstd.GetValueOrDefault("macOS", "");
            DeviceCodenameBox.Text = plan.DeviceCodename; AuthorInfoBox.Text = plan.Information;
            _documents.Clear(); _allDocuments.Clear(); _activeDocument = null;
            foreach (var (name, text) in plan.Scripts)
            {
                var doc = new DeliveryScriptDocument(name) { Baseline = plan.GeneratedScripts.GetValueOrDefault(name), CaretOffset = Math.Max(0, plan.ScriptCarets.GetValueOrDefault(name)) };
                doc.Document.Text = text; _allDocuments.Add(name, doc);
                if (plan.OpenScripts.Contains(name)) _documents.Add(doc);
            }
            _fingerprint = plan.ScriptFingerprint;
            if (_allDocuments.Count == 0) AddDocument();
            else if (_documents.Count > 0) DocumentTabs.SelectedItem = _documents[0];
            else ScriptEditor.Document = new();
        }
        finally { _syncing = false; }
    }

    internal void Reset()
    {
        OutputImages.CancelEdit();
        _allDocuments.Clear(); _fingerprint = "";
        TxtScriptBox.IsChecked = true;
        BatchShellScriptBox.IsChecked = false;
        LinuxScriptBox.IsChecked = false; MacScriptBox.IsChecked = false;
        AskWipeBox.IsChecked = false; RebootBox.IsChecked = false; FlashSlotBox.SelectedIndex = 0;
        WindowsZstdBox.Text = ""; LinuxZstdBox.Text = ""; MacZstdBox.Text = "";
        RecoveryScriptBox.IsChecked = false;
        OtaZipBox.IsChecked = false; PackageCertificateBox.Text = ""; PackageKeyBox.Text = "";
        BuildFingerprintBox.Text = ""; IncrementalBox.Text = ""; SdkLevelBox.Text = "0";
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
        while (_allDocuments.ContainsKey(number == 1 ? "script.txt" : $"script-{number}.txt")) number = ++_documentNumber;
        var document = new DeliveryScriptDocument(number == 1 ? "script.txt" : $"script-{number}.txt");
        _allDocuments.Add(document.Title, document);
        document.Document.TextChanged += (_, _) => NotifyChanged();
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
        if (_documents.Count == 0) { _activeDocument = null; ScriptEditor.Document = new(); }
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
