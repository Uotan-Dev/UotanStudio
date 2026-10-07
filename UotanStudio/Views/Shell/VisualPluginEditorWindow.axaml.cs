using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.ViewModels.Nodes;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>Edits one composite node in an isolated node-editor context.</summary>
public partial class VisualPluginEditorWindow : Window
{
    private VisualPluginRegistration? _registration;
    private Action<VisualPluginPackage>? _publishLocal;
    private readonly NodeEditorViewModel _editor = new(showVisualPluginWorkspace: false);
    private WindowToastManager? _toastManager;
    private VisualPluginManager _manager = VisualPluginManager.Instance;
    private readonly ObservableCollection<VisualPluginAsset> _resources = [];
    private bool _resourceBusy;
    private bool _settingRequirements;
    private bool _invalidRequirements;
    private readonly CancellationTokenSource _lifetime = new();

    public VisualPluginEditorWindow()
    {
        InitializeComponent();
        DataContext = _editor;
        SavePluginButton.IsEnabled = false;
        ResourcePanel.IsEnabled = false;
        RequirementsPanel.IsEnabled = false;
        ResourceList.ItemsSource = _resources;
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _toastManager?.CloseAll();
            _editor.Dispose();
        };
    }

    public VisualPluginEditorWindow(VisualPluginRegistration registration, VisualPluginManager? manager = null) : this()
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.Kind != VisualPluginKind.CompositeNode)
            throw new ArgumentException("Only composite nodes have an editable internal graph.", nameof(registration));

        _registration = registration;
        _manager = manager ?? VisualPluginManager.Instance;
        SetResourceScope(registration.Package);
        ResourcePanel.IsEnabled = true;
        RequirementsPanel.IsEnabled = true;
        _editor.ValidatePackage = () => _invalidRequirements
            ? [LanguageService.Instance.GetString("Plugin_RequirementsInvalid")]
            : _manager.ValidateRequirements(_editor.ResourcePackage!);
        SavePluginButton.IsEnabled = true;
        Title = $"{LanguageService.Instance.GetString("Plugin_EditGraph")} - {registration.Name}";
        PluginNameText.Text = registration.Name;
        PluginVersionText.Text = registration.Version;
        _ = _editor.LoadFromAsync(registration.Package.Graph);
    }

    /// <summary>Edits an embedded composite without installing it in the global catalog.</summary>
    public VisualPluginEditorWindow(VisualPluginPackage package, Action<VisualPluginPackage> publish) : this()
    {
        _publishLocal = publish;
        SetResourceScope(package);
        ResourcePanel.IsEnabled = true;
        RequirementsPanel.IsEnabled = true;
        SavePluginButton.IsEnabled = true;
        Title = LanguageService.Instance.GetString("Plugin_EditGraph") + " - " + package.Manifest.Name;
        PluginNameText.Text = package.Manifest.Name;
        PluginVersionText.Text = package.Manifest.Version;
        _ = _editor.LoadFromAsync(package.Graph);
    }

    private async void SaveClick(object? sender, RoutedEventArgs e)
    {
        if (_registration is null && _publishLocal is null) return;
        if (!TryBeginResourceOperation()) return;
        try
        {
            EnsureValidRequirements();
            var updated = VisualPluginPackageBuilder.CreateUpdated(_editor.ResourcePackage!,
                _editor.Nodes, _editor.Connections, portable: _publishLocal is null);
            if (_publishLocal is { } publish)
            {
                publish(updated);
                SetResourceScope(updated);
                Close();
                return;
            }
            _registration = await _manager.UpdateAsync(_registration!, updated, _lifetime.Token);
            SetResourceScope(_registration.Package);
            ShowResult(LanguageService.Instance.GetString("Plugin_GraphSaved"), NotificationType.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ShowResult(string.Format(LanguageService.Instance.GetString("Plugin_OperationFailed"), global::UotanStudio.Services.UserError.Describe(ex)),
                NotificationType.Error);
        }
        catch (OperationCanceledException) { }
        finally { EndResourceOperation(); }
    }

    private async void EditInterfaceClick(object? sender, RoutedEventArgs e)
    {
        if (_editor.IsExecuting || _editor.ResourcePackage is not { } package)
        {
            return;
        }
        var analysis = CompositeBoundaryAnalyzer.Analyze(_editor.Nodes, _editor.Connections);
        var inputs = package.Inputs.Select(CloneInterfacePort).ToArray();
        var outputs = package.Outputs.Select(CloneInterfacePort).ToArray();
        var inputTargets = inputs.SelectMany(port => port.GetBindings()).Select(binding => (binding.NodeInstanceId, binding.PortId)).ToHashSet();
        var outputTargets = outputs.Select(port => (port.NodeInstanceId, port.PortId)).ToHashSet();
        var review = new CompositeBoundaryAnalysis(inputs, outputs,
            [.. analysis.Inputs.Concat(analysis.InputCandidates).Where(port => !inputTargets.Contains((port.NodeInstanceId, port.PortId)))],
            [.. analysis.Outputs.Concat(analysis.OutputCandidates).Where(port => !outputTargets.Contains((port.NodeInstanceId, port.PortId)))]);
        var dialog = new Nodes.CompositeInterfaceWindow(review, package.Manifest.Name);
        if (await dialog.ShowDialog<bool>(this))
        {
            var result = dialog.GetBoundary();
            package.Inputs = [.. result.Inputs];
            package.Outputs = [.. result.Outputs];
            package.Manifest.Name = dialog.CompositeName;
            PluginNameText.Text = package.Manifest.Name;
        }
    }

    private static VisualPluginPortDefinition CloneInterfacePort(VisualPluginPortDefinition port) => new()
    {
        Id = port.Id,
        Name = port.Name,
        PortType = port.PortType,
        NodeInstanceId = port.NodeInstanceId,
        PortId = port.PortId,
        DefaultValue = port.DefaultValue,
        Required = port.Required,
        IsSensitive = port.IsSensitive,
        AdditionalBindings = [.. port.AdditionalBindings.Select(binding => new VisualPluginPortBinding
        {
            NodeInstanceId = binding.NodeInstanceId,
            PortId = binding.PortId
        })]
    };

    private void SetResourceScope(VisualPluginPackage package)
    {
        _resources.Clear();
        foreach (var asset in package.Assets)
        {
            _resources.Add(asset.Snapshot());
        }
        _editor.ResourcePackage = new VisualPluginPackage
        {
            Kind = package.Kind,
            Manifest = new VisualPluginManifest
            {
                Id = package.Manifest.Id,
                Name = package.Manifest.Name,
                Version = package.Manifest.Version,
                Description = package.Manifest.Description,
                Author = package.Manifest.Author,
                NodeApiVersion = package.Manifest.NodeApiVersion,
                Platforms = [.. package.Manifest.Platforms],
                Capabilities = [.. package.Manifest.Capabilities],
                RequiredNodeTypes = [.. package.Manifest.RequiredNodeTypes],
                Dependencies = [.. package.Manifest.Dependencies.Select(dependency => new VisualPluginDependency
                {
                    Id = dependency.Id,
                    MinimumVersion = dependency.MinimumVersion,
                    MaximumVersionExclusive = dependency.MaximumVersionExclusive
                })],
                Conflicts = [.. package.Manifest.Conflicts]
            },
            Graph = package.Graph,
            Inputs = package.Inputs,
            Outputs = package.Outputs,
            Assets = [.. _resources],
            BundlePath = package.BundlePath
        };
        _settingRequirements = true;
        VersionInput.Text = package.Manifest.Version;
        DependenciesInput.Text = string.Join(Environment.NewLine, package.Manifest.Dependencies.Select(dependency =>
            $"{dependency.Id} {dependency.MinimumVersion ?? "*"} {dependency.MaximumVersionExclusive ?? "*"}"));
        ConflictsInput.Text = string.Join(Environment.NewLine, package.Manifest.Conflicts);
        _settingRequirements = false;
        _invalidRequirements = false;
        RequirementsError.IsVisible = false;
    }

    internal async Task AddResourceAsync(string id, string path, CancellationToken token)
    {
        var asset = await VisualPluginAsset.FromFileAsync(id, path, token);
        var package = _editor.ResourcePackage ?? throw new InvalidOperationException("No package is open.");
        var previous = package.Assets;
        package.Assets = [.. previous, asset];
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count != 0)
        {
            package.Assets = previous;
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
        _resources.Add(asset);
        ResourceList.SelectedItem = asset;
    }

    private async void AddResourceClick(object? sender, RoutedEventArgs e)
    {
        if (!TryBeginResourceOperation()) return;
        try
        {
            var id = ResourceIdText.Text?.Trim() ?? string.Empty;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Plugin_AddResource"),
                AllowMultiple = false
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                await AddResourceAsync(id, path, _lifetime.Token);
                ResourceIdText.Text = string.Empty;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ShowResult(LanguageService.Instance.GetString("Plugin_OperationFailed", ex.Message), NotificationType.Error);
        }
        finally { EndResourceOperation(); }
    }

    private void RemoveResourceClick(object? sender, RoutedEventArgs e)
    {
        if (!TryBeginResourceOperation()) return;
        try
        {
            if (ResourceList.SelectedItem is VisualPluginAsset asset)
            {
                _resources.Remove(asset);
                _editor.ResourcePackage!.Assets = [.. _resources];
            }
        }
        finally { EndResourceOperation(); }
    }

    private async void ExportBundleClick(object? sender, RoutedEventArgs e)
    {
        if (!TryBeginResourceOperation()) return;
        try
        {
            EnsureValidRequirements();
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LanguageService.Instance.GetString("Plugin_ExportBundle"),
                SuggestedFileName = _editor.ResourcePackage!.Manifest.Id + ".uotanplugin",
                DefaultExtension = "uotanplugin",
                FileTypeChoices = [new FilePickerFileType(LanguageService.Instance.GetString("Plugin_ExportBundle"))
                {
                    Patterns = ["*.uotanplugin"]
                }]
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                var package = VisualPluginPackageBuilder.CreateUpdated(_editor.ResourcePackage!, _editor.Nodes, _editor.Connections);
                await VisualPluginBundle.SaveAsync(path, package, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ShowResult(LanguageService.Instance.GetString("Plugin_OperationFailed", ex.Message), NotificationType.Error);
        }
        finally { EndResourceOperation(); }
    }

    private bool TryBeginResourceOperation()
    {
        if (_registration is null && _publishLocal is null) return false;
        if (_resourceBusy || _editor.IsExecuting)
        {
            ShowResult(LanguageService.Instance.GetString("Plugin_ResourceBusy"), NotificationType.Warning);
            return false;
        }
        _resourceBusy = true;
        ResourcePanel.IsEnabled = false;
        RequirementsPanel.IsEnabled = false;
        SavePluginButton.IsEnabled = false;
        GraphHost.IsEnabled = false;
        return true;
    }

    private void EndResourceOperation()
    {
        _resourceBusy = false;
        ResourcePanel.IsEnabled = _registration is not null || _publishLocal is not null;
        RequirementsPanel.IsEnabled = ResourcePanel.IsEnabled;
        SavePluginButton.IsEnabled = ResourcePanel.IsEnabled;
        GraphHost.IsEnabled = true;
    }

    private void RequirementsChanged(object? sender, TextChangedEventArgs e)
        => UpdateRequirements();

    private void UpdateRequirements()
    {
        if (_settingRequirements || _editor.ResourcePackage is not { } package) return;
        var previousDependencies = package.Manifest.Dependencies;
        var previousConflicts = package.Manifest.Conflicts;
        var previousVersion = package.Manifest.Version;
        try
        {
            List<VisualPluginDependency> dependencies = [];
            foreach (var line in (DependenciesInput.Text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length is not (1 or 3)) throw new InvalidDataException();
                dependencies.Add(new VisualPluginDependency
                {
                    Id = fields[0],
                    MinimumVersion = fields.Length == 3 && fields[1] != "*" ? fields[1] : null,
                    MaximumVersionExclusive = fields.Length == 3 && fields[2] != "*" ? fields[2] : null
                });
            }
            package.Manifest.Dependencies = dependencies;
            package.Manifest.Conflicts = [.. (ConflictsInput.Text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
            package.Manifest.Version = VersionInput.Text?.Trim() ?? string.Empty;
            if (VisualPluginValidation.Validate(package).Count > 0) throw new InvalidDataException();
            _invalidRequirements = false;
        }
        catch (InvalidDataException)
        {
            package.Manifest.Dependencies = previousDependencies;
            package.Manifest.Conflicts = previousConflicts;
            package.Manifest.Version = previousVersion;
            _invalidRequirements = true;
        }
        RequirementsError.Text = LanguageService.Instance.GetString("Plugin_RequirementsInvalid");
        RequirementsError.IsVisible = _invalidRequirements;
    }

    private void EnsureValidRequirements()
    {
        UpdateRequirements();
        if (_invalidRequirements)
        {
            throw new InvalidDataException(LanguageService.Instance.GetString("Plugin_RequirementsInvalid"));
        }
    }

    private void ShowResult(string text, NotificationType type)
    {
        _toastManager ??= WindowToastManager.TryGetToastManager(this, out var existing)
            ? existing
            : new WindowToastManager(this) { MaxItems = 1 };
        _toastManager?.Show(text, type, TimeSpan.FromSeconds(type == NotificationType.Error ? 6 : 2),
            showIcon: true, showClose: type == NotificationType.Error);
    }
}