using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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

/// <summary>
/// Edits one composite node in an isolated node-editor context.
/// <para>在隔离的节点编辑上下文中编辑单个复合节点。</para>
/// </summary>
public partial class VisualPluginEditorWindow : Window
{
    private VisualPluginRegistration? _registration;
    private Action<VisualPluginPackage>? _publishLocal;
    private readonly NodeEditorViewModel _editor = new(showVisualPluginWorkspace: false);
    private WindowToastManager? _toastManager;
    private VisualPluginManager _manager = VisualPluginManager.Instance;
    private static readonly VisualPluginEditorInstanceTracker Instances = new();
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
        ReplaceResourceButton.IsEnabled = false;
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            NodePortChoiceSource.Clear(this);
            _toastManager?.CloseAll();
            _editor.Dispose();
        };
    }

    /// <summary>Shows the editor for an installed package, focusing the window already open for that package instead
    /// of creating a second one so unsaved graph edits are not lost.
    /// <para>显示已安装包的编辑器；该包已有打开窗口时聚焦该窗口而不是再建一个，避免丢失未保存的图编辑。</para></summary>
    /// <param name="registration">Installed package to edit; it must describe a composite node.
    /// <para>要编辑的已安装包；必须描述一个复合节点。</para></param>
    /// <param name="manager">Manager receiving saved package updates, or <see langword="null"/> for the shared instance.
    /// <para>接收保存后包更新的管理器；为 <see langword="null"/> 时使用共享实例。</para></param>
    /// <returns>The newly created or already open editor window.
    /// <para>新建或已打开的编辑器窗口。</para></returns>
    internal static VisualPluginEditorWindow ShowRegistration(VisualPluginRegistration registration,
        VisualPluginManager? manager = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return ShowOrFocus(GetInstanceKey(registration), () => new VisualPluginEditorWindow(registration, manager));
    }

    /// <summary>Shows the editor for an embedded package, focusing the window already open for that package instance
    /// instead of creating a second one so unsaved graph edits are not lost.
    /// <para>显示内嵌包的编辑器；该包实例已有打开窗口时聚焦该窗口而不是再建一个，避免丢失未保存的图编辑。</para></summary>
    /// <param name="package">Embedded composite package to edit.
    /// <para>要编辑的内嵌复合包。</para></param>
    /// <param name="publish">Callback receiving the updated package on save.
    /// <para>保存时接收更新后包的回调。</para></param>
    /// <returns>The newly created or already open editor window.
    /// <para>新建或已打开的编辑器窗口。</para></returns>
    internal static VisualPluginEditorWindow ShowLocal(VisualPluginPackage package,
        Action<VisualPluginPackage> publish)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(publish);
        return ShowOrFocus(GetInstanceKey(package), () => new VisualPluginEditorWindow(package, publish));
    }

    internal static string GetInstanceKey(VisualPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return "registration:" + registration.Id;
    }

    internal static string GetInstanceKey(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return "local:" + package.Manifest.Id + ":"
            + RuntimeHelpers.GetHashCode(package).ToString("X", CultureInfo.InvariantCulture);
    }

    private static VisualPluginEditorWindow ShowOrFocus(string key, Func<VisualPluginEditorWindow> create)
    {
        if (Instances.TryGet(key, out var existing) && existing is VisualPluginEditorWindow window)
        {
            if (window.IsVisible)
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }
                window.Activate();
                return window;
            }
            Instances.Remove(key, window);
        }
        var created = create();
        Instances.TryRegister(key, created);
        created.Closed += (_, _) => Instances.Remove(key, created);
        created.Show();
        return created;
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

    /// <summary>
    /// Edits an embedded composite without installing it in the global catalog.
    /// <para>编辑内嵌复合节点而不将其安装到全局目录。</para>
    /// </summary>
    /// <param name="package">
    /// The embedded composite package to edit.
    /// <para>要编辑的内嵌复合包。</para>
    /// </param>
    /// <param name="publish">
    /// The callback receiving the updated package on save.
    /// <para>保存时接收更新后包的回调。</para>
    /// </param>
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
            ShowResult(string.Format(LanguageService.Instance.GetString("Plugin_OperationFailed"), UserError.Describe(ex)),
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
        AuthorInput.Text = package.Manifest.Author;
        DescriptionInput.Text = package.Manifest.Description;
        PlatformsInput.Text = string.Join(", ", package.Manifest.Platforms);
        NodeApiVersionInput.Text = package.Manifest.NodeApiVersion.ToString(CultureInfo.InvariantCulture);
        DependenciesInput.Text = string.Join(Environment.NewLine, package.Manifest.Dependencies.Select(dependency =>
            $"{dependency.Id} {dependency.MinimumVersion ?? "*"} {dependency.MaximumVersionExclusive ?? "*"}"));
        ConflictsInput.Text = string.Join(Environment.NewLine, package.Manifest.Conflicts);
        _settingRequirements = false;
        _invalidRequirements = false;
        RequirementsError.IsVisible = false;
        PublishResourceChoices();
    }

    /// <summary>
    /// Publishes the open package's asset identifiers and display names as the candidates for resource identifier
    /// ports, so an <c>Asset ID</c> port inside this graph offers exactly the assets the read node can resolve here.
    /// The source is process-wide and this window owns the published scope: closing it clears only its own scope, so
    /// a second package editor keeps its candidates.
    /// </summary>
    private void PublishResourceChoices()
        => NodePortChoiceSource.SetResources(this, (_editor.ResourcePackage?.Assets ?? []).Select(asset => (asset.Id, asset.Name)));

    private void ResourceListSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ReplaceResourceButton.IsEnabled = ResourceList.SelectedItem is VisualPluginAsset;

    private async Task<IStorageFolder?> ResolveProjectStartLocationAsync()
    {
        if (ProjectContextService.Instance.RootDirectory is not { Length: > 0 } root || !Directory.Exists(root))
        {
            return null;
        }
        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(root)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? CurrentProjectRoot
        => ProjectContextService.Instance.RootDirectory is { Length: > 0 } root && Directory.Exists(root)
            ? Path.GetFullPath(root)
            : null;

    private async Task<VisualPluginAsset> CopyResourceAsync(string id, string path, CancellationToken token)
    {
        var package = _editor.ResourcePackage ?? throw new InvalidOperationException("No package is open.");
        if (CurrentProjectRoot is { } root)
        {
            return await VisualPluginAssetStore.CopyIntoProjectAsync(id, path, root, package.Manifest.Id, token);
        }
        ShowResult(LanguageService.Instance.GetString("ImageView_NoProject"), NotificationType.Warning);
        return await VisualPluginAsset.FromFileAsync(id, path, token);
    }

    internal async Task AddResourceAsync(string id, string path, CancellationToken token)
    {
        var package = _editor.ResourcePackage ?? throw new InvalidOperationException("No package is open.");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidDataException("A resource id is required.");
        }
        if (package.Assets.Any(asset => string.Equals(asset.Id, id, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Patch resource '{id}' already exists.");
        }
        if (package.Assets.Count >= VisualPluginBundle.MaximumAssetCount)
        {
            throw new InvalidDataException("Patch resources exceed the resource count limit.");
        }
        var asset = await CopyResourceAsync(id, path, token);
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
        PublishResourceChoices();
    }

    internal async Task ReplaceResourceAsync(VisualPluginAsset target, string path, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target);
        var package = _editor.ResourcePackage ?? throw new InvalidOperationException("No package is open.");
        var previousAssets = package.Assets;
        var index = previousAssets.FindIndex(asset => string.Equals(asset.Id, target.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException("The selected resource is not part of the open package.");
        }
        var replacement = await CopyResourceAsync(target.Id, path, token);
        var updatedAssets = previousAssets.ToArray();
        updatedAssets[index] = replacement;
        package.Assets = [.. updatedAssets];
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count != 0)
        {
            package.Assets = previousAssets;
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
        var resourceIndex = _resources.IndexOf(target);
        if (resourceIndex >= 0)
        {
            _resources[resourceIndex] = replacement;
        }
        ResourceList.SelectedItem = replacement;
        PublishResourceChoices();
    }

    private async void ReplaceResourceClick(object? sender, RoutedEventArgs e)
    {
        if (ResourceList.SelectedItem is not VisualPluginAsset asset) return;
        if (!TryBeginResourceOperation()) return;
        try
        {
            using var startFolder = await ResolveProjectStartLocationAsync();
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Plugin_Resources"),
                AllowMultiple = false,
                SuggestedStartLocation = startFolder
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                await ReplaceResourceAsync(asset, path, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowResult(LanguageService.Instance.GetString("Plugin_OperationFailed", ex.Message), NotificationType.Error);
        }
        finally { EndResourceOperation(); }
    }

    private async void AddResourceClick(object? sender, RoutedEventArgs e)
    {
        if (!TryBeginResourceOperation()) return;
        try
        {
            var id = ResourceIdText.Text?.Trim() ?? string.Empty;
            using var startFolder = await ResolveProjectStartLocationAsync();
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Plugin_AddResource"),
                AllowMultiple = false,
                SuggestedStartLocation = startFolder
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                await AddResourceAsync(id, path, _lifetime.Token);
                ResourceIdText.Text = string.Empty;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
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
                PublishResourceChoices();
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
                if (CurrentProjectRoot is { } root)
                {
                    await VisualPluginBundle.SaveAsync(path, package, root, _lifetime.Token);
                }
                else
                {
                    await VisualPluginBundle.SaveAsync(path, package, _lifetime.Token);
                }
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
        var previousAuthor = package.Manifest.Author;
        var previousDescription = package.Manifest.Description;
        var previousPlatforms = package.Manifest.Platforms;
        var previousNodeApiVersion = package.Manifest.NodeApiVersion;
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
            if (!VisualPluginManifestEditor.TryApply(package.Manifest, AuthorInput.Text, DescriptionInput.Text,
                    PlatformsInput.Text, NodeApiVersionInput.Text))
            {
                throw new InvalidDataException();
            }
            if (VisualPluginValidation.Validate(package).Count > 0) throw new InvalidDataException();
            _invalidRequirements = false;
        }
        catch (InvalidDataException)
        {
            package.Manifest.Dependencies = previousDependencies;
            package.Manifest.Conflicts = previousConflicts;
            package.Manifest.Version = previousVersion;
            package.Manifest.Author = previousAuthor;
            package.Manifest.Description = previousDescription;
            package.Manifest.Platforms = previousPlatforms;
            package.Manifest.NodeApiVersion = previousNodeApiVersion;
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