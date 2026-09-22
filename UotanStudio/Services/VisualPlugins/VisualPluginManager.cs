using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.Services.VisualPlugins;

/// <summary>Installs and activates host-rendered JSON workflow and composite-node packages.</summary>
public sealed class VisualPluginManager
{
    private const long MaximumStateFileSize = 256 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _statePath;
    private readonly HashSet<string> _registeredTypeKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _disabledIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Application-wide visual-plugin manager.</summary>
    public static VisualPluginManager Instance { get; } = new();

    /// <summary>Current installed package registrations and diagnostics.</summary>
    public ObservableCollection<VisualPluginRegistration> Registrations { get; } = [];

    /// <summary>Raised after installed packages or activation state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Creates a manager. A custom directory is supported for isolated tests.</summary>
    public VisualPluginManager(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UotanStudio", "visual-plugins");
        _statePath = Path.Combine(_directory, "state.json");
        Refresh();
    }

    /// <summary>Re-scans installed packages, validates dependencies, and refreshes toolbox registrations.</summary>
    public void Refresh()
    {
        Directory.CreateDirectory(_directory);
        LoadState();
        foreach (var typeKey in _registeredTypeKeys.ToArray())
            NodeFactoryRegistrar.UnregisterVisualPlugin(typeKey);
        _registeredTypeKeys.Clear();

        var loaded = new List<(string Path, VisualPluginPackage Package)>();
        var invalid = new List<VisualPluginRegistration>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => path.EndsWith(".uotanplugin.json", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".uotanplugin", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                loaded.Add((path, VisualPluginFile.Load(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                invalid.Add(VisualPluginRegistration.Invalid(path, ex.Message));
            }
        }

        var duplicates = loaded.GroupBy(entry => entry.Package.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compositeByType = loaded
            .Where(entry => entry.Package.Kind == VisualPluginKind.CompositeNode)
            .GroupBy(entry => entry.Package.NodeTypeKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Package, StringComparer.OrdinalIgnoreCase);
        var cycleTypes = FindDependencyCycles(compositeByType);
        var available = new HashSet<string>(NodeFactoryRegistrar.BuiltInTypeKeys, StringComparer.OrdinalIgnoreCase);
        var errorsById = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<VisualPluginPackage>();
        var enabledPackages = loaded.Select(entry => entry.Package)
            .Where(package => package.Kind == VisualPluginKind.CompositeNode && !_disabledIds.Contains(package.Manifest.Id))
            .ToArray();

        foreach (var (_, package) in loaded)
        {
            var errors = new List<string>();
            errorsById[package.Manifest.Id] = errors;
            if (duplicates.Contains(package.Manifest.Id)) errors.Add("Multiple installed files use the same plugin id.");
            if (!SupportsCurrentPlatform(package.Manifest.Platforms)) errors.Add("This package does not support the current platform.");
            if (cycleTypes.Contains(package.NodeTypeKey)) errors.Add("Composite-plugin dependency cycle detected.");
            errors.AddRange(GetRequirementErrors(package, enabledPackages));
            if (package.Kind == VisualPluginKind.CompositeNode
                && !_disabledIds.Contains(package.Manifest.Id)
                && errors.Count == 0)
            {
                pending.Add(package);
            }
        }

        var madeProgress = true;
        while (madeProgress && pending.Count > 0)
        {
            madeProgress = false;
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var package = pending[index];
                if (!GetExternalNodeTypes(package).All(available.Contains)) continue;
                if (!package.Manifest.Dependencies.All(dependency => available.Contains("VisualPlugin." + dependency.Id))) continue;
                NodeFactoryRegistrar.RegisterVisualPlugin(package, () => GetExecutionErrors(package));
                _registeredTypeKeys.Add(package.NodeTypeKey);
                available.Add(package.NodeTypeKey);
                pending.RemoveAt(index);
                madeProgress = true;
            }
        }

        foreach (var package in pending)
        {
            var missing = GetExternalNodeTypes(package).Where(type => !available.Contains(type)).ToArray();
            if (missing.Length > 0)
            {
                errorsById[package.Manifest.Id].Add("Unavailable node dependencies: " + string.Join(", ", missing));
            }
            foreach (var dependency in package.Manifest.Dependencies.Where(dependency => !available.Contains("VisualPlugin." + dependency.Id)))
            {
                errorsById[package.Manifest.Id].Add(LanguageService.Instance.GetString("Plugin_DependencyUnavailable", dependency.Id));
            }
        }

        Registrations.Clear();
        foreach (var (path, package) in loaded.OrderBy(entry => entry.Package.Manifest.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var enabled = package.Kind == VisualPluginKind.CompositeNode && !_disabledIds.Contains(package.Manifest.Id);
            Registrations.Add(new VisualPluginRegistration(path, package, enabled,
                [.. errorsById[package.Manifest.Id]]));
        }
        foreach (var registration in invalid) Registrations.Add(registration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Installs or replaces a package after bounded parsing and structural validation.</summary>
    public async Task<VisualPluginRegistration> InstallAsync(string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var package = await VisualPluginFile.LoadAsync(sourcePath, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var matches = Registrations.Where(candidate => string.Equals(candidate.Id, package.Manifest.Id,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidOperationException("Resolve duplicate installed package ids before replacing a package.");
            }
            await PublishPackageAsync(matches.FirstOrDefault(), package, cancellationToken);
            if (package.Kind == VisualPluginKind.CompositeNode) _disabledIds.Remove(package.Manifest.Id);
            await SaveStateAsync(cancellationToken);
            Refresh();
            return Registrations.First(registration =>
                string.Equals(registration.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Enables or disables an installed composite node without modifying its portable package.</summary>
    public async Task SetEnabledAsync(string pluginId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled) _disabledIds.Remove(pluginId);
            else _disabledIds.Add(pluginId);
            await SaveStateAsync(cancellationToken);
            Refresh();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Atomically replaces an installed package without changing its activation state.</summary>
    public async Task<VisualPluginRegistration> UpdateAsync(VisualPluginRegistration registration,
        VisualPluginPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(package);
        if (!string.Equals(registration.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An installed plugin cannot be replaced with a different plugin id.");
        if (registration.Kind != package.Kind)
            throw new InvalidOperationException("An installed plugin cannot change its package kind.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(registration.FilePath);
            var root = Path.GetFullPath(_directory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Plugin file is outside the managed directory.");

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException("The installed plugin has been removed. Import it again before editing.");
            }
            return await PublishPackageAsync(registration, package, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes one installed package and its activation state.</summary>
    public Task RemoveAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var matches = Registrations.Where(candidate =>
            string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("Select one installed plugin file to remove.");
        }
        return RemoveAsync(matches[0], cancellationToken);
    }

    /// <summary>Deletes the selected managed file, including invalid or duplicate-id packages.</summary>
    public async Task RemoveAsync(VisualPluginRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(registration.FilePath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_directory));
            if (!string.Equals(Path.GetDirectoryName(fullPath), root, comparison)
                || !Registrations.Any(candidate => string.Equals(
                    Path.GetFullPath(candidate.FilePath), fullPath, comparison)))
            {
                throw new InvalidOperationException("Plugin file is outside the managed directory.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(fullPath);
            try
            {
                if (!Registrations.Any(candidate => string.Equals(candidate.Id, registration.Id,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFullPath(candidate.FilePath), fullPath, comparison)))
                {
                    _disabledIds.Remove(registration.Id);
                }
                // Once the file is deleted, finish bookkeeping even if the caller cancels.
                await SaveStateAsync(CancellationToken.None);
            }
            finally
            {
                Refresh();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VisualPluginRegistration> PublishPackageAsync(VisualPluginRegistration? previous,
        VisualPluginPackage package, CancellationToken cancellationToken)
    {
        var oldPath = previous?.FilePath;
        var destination = oldPath ?? Path.Combine(_directory, package.Manifest.Id + ".uotanplugin.json");
        if (VisualPluginBundle.EnumerateScopes(package).Any(scope => scope.Assets.Count > 0)
            && !destination.EndsWith(".uotanplugin", StringComparison.OrdinalIgnoreCase))
        {
            destination = Path.Combine(_directory, package.Manifest.Id + ".uotanplugin");
        }
        var moved = oldPath is not null && !string.Equals(oldPath, destination, StringComparison.Ordinal);
        if ((oldPath is null || moved) && File.Exists(destination))
        {
            throw new IOException("The package destination already exists and is not the selected installation.");
        }
        var backup = oldPath is null ? null : oldPath + "." + Guid.NewGuid().ToString("N") + ".backup";
        var published = false;
        var recovered = false;
        try
        {
            if (backup is not null)
            {
                await using var input = new FileStream(oldPath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 131072, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            await VisualPluginFile.SaveAsync(destination, package, cancellationToken);
            published = true;
            if (moved)
            {
                File.Delete(oldPath!);
            }
            Refresh();
            var updated = Registrations.First(candidate =>
                string.Equals(candidate.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
            if (previous is { IsAvailable: true } && !updated.IsAvailable)
            {
                throw new InvalidDataException($"The update would make the plugin unavailable: {updated.ErrorText}");
            }
            recovered = true;
            return updated;
        }
        catch
        {
            if (published)
            {
                // Restore retained bytes, not descriptors whose lazy source now points at the replacement.
                // Once publication begins, cancellation must not interrupt recovery.
                if (backup is not null)
                {
                    File.Move(backup, oldPath!, overwrite: true);
                }
                if (oldPath is null || moved)
                {
                    File.Delete(destination);
                }
                Refresh();
            }
            recovered = true;
            throw;
        }
        finally
        {
            if ((!published || recovered) && backup is not null && File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
    }

    private void LoadState()
    {
        _disabledIds = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_statePath)) return;
        try
        {
            var info = new FileInfo(_statePath);
            if (info.Length > MaximumStateFileSize) return;
            using var stream = info.OpenRead();
            var state = JsonSerializer.Deserialize(stream, VisualPluginManagerJsonContext.Default.VisualPluginManagerState);
            if (state is not null) _disabledIds = state.DisabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Instance.Write(LogLevel.Warn, "VisualPlugins", ex.Message);
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var state = new VisualPluginManagerState { DisabledIds = [.. _disabledIds.Order()] };
                await JsonSerializer.SerializeAsync(stream, state,
                    VisualPluginManagerJsonContext.Default.VisualPluginManagerState, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _statePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IEnumerable<string> GetExternalNodeTypes(VisualPluginPackage package)
    {
        // Packages have already passed bounded structural validation before this traversal.
        var embedded = package.Graph.Nodes.Where(node => node.CompositePackage is not null)
            .ToLookup(node => node.TypeKey, node => node.CompositePackage!, StringComparer.OrdinalIgnoreCase);
        foreach (var type in package.Manifest.RequiredNodeTypes)
        {
            if (embedded.Contains(type))
            {
                if (embedded[type].Any(child => child.Assets.Count > 0 && child.BundlePath is null))
                {
                    yield return type;
                }
                foreach (var dependency in embedded[type].SelectMany(GetExternalNodeTypes))
                {
                    yield return dependency;
                }
            }
            else
            {
                yield return type;
            }
        }
    }

    private static bool SupportsCurrentPlatform(IReadOnlyCollection<string> platforms)
    {
        if (platforms.Count == 0) return true;
        var current = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "unknown";
        return platforms.Any(platform => string.Equals(platform, current, StringComparison.OrdinalIgnoreCase)
            || (current == "macos" && string.Equals(platform, "osx", StringComparison.OrdinalIgnoreCase))
            || string.Equals(platform, "any", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> FindDependencyCycles(
        IReadOnlyDictionary<string, VisualPluginPackage> packages)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();

        void Visit(string typeKey)
        {
            if (states.GetValueOrDefault(typeKey) == 2) return;
            if (states.GetValueOrDefault(typeKey) == 1)
            {
                var start = stack.FindIndex(item => string.Equals(item, typeKey, StringComparison.OrdinalIgnoreCase));
                if (start >= 0)
                    foreach (var item in stack.Skip(start)) result.Add(item);
                return;
            }

            states[typeKey] = 1;
            stack.Add(typeKey);
            foreach (var dependency in GetExternalNodeTypes(packages[typeKey]).Concat(
                         packages[typeKey].Manifest.Dependencies.Select(dependency => "VisualPlugin." + dependency.Id)))
                if (packages.ContainsKey(dependency)) Visit(dependency);
            stack.RemoveAt(stack.Count - 1);
            states[typeKey] = 2;
        }

        foreach (var typeKey in packages.Keys) Visit(typeKey);
        return result;
    }

    /// <summary>Checks an edited package against currently available installations without applying prerequisite patches.</summary>
    public IReadOnlyList<string> ValidateRequirements(VisualPluginPackage package)
    {
        var structural = VisualPluginValidation.Validate(package);
        if (structural.Count > 0)
        {
            return structural;
        }
        var errors = GetRequirementErrors(package, Registrations.Where(registration => registration.IsEnabled)
            .Select(registration => registration.Package).ToArray());
        foreach (var dependency in package.Manifest.Dependencies)
        {
            if (!Registrations.Any(registration => registration.IsEnabled && registration.IsAvailable
                    && string.Equals(registration.Id, dependency.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var message = LanguageService.Instance.GetString("Plugin_DependencyUnavailable", dependency.Id);
                if (!errors.Contains(message)) errors.Add(message);
            }
        }
        return errors;
    }

    private static List<string> GetRequirementErrors(VisualPluginPackage package, IReadOnlyList<VisualPluginPackage> enabled)
    {
        var errors = new List<string>();
        foreach (var dependency in package.Manifest.Dependencies)
        {
            var candidates = enabled.Where(candidate => string.Equals(candidate.Manifest.Id, dependency.Id,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1)
            {
                errors.Add(LanguageService.Instance.GetString("Plugin_DependencyUnavailable", dependency.Id));
            }
            else if (!dependency.MatchesVersion(candidates[0].Manifest.Version))
            {
                errors.Add(LanguageService.Instance.GetString("Plugin_DependencyVersionMismatch", dependency.Id,
                    candidates[0].Manifest.Version, dependency.MinimumVersion ?? "*", dependency.MaximumVersionExclusive ?? "*"));
            }
        }
        foreach (var other in enabled)
        {
            if (string.Equals(other.Manifest.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (package.Manifest.Conflicts.Contains(other.Manifest.Id, StringComparer.OrdinalIgnoreCase)
                || other.Manifest.Conflicts.Contains(package.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(LanguageService.Instance.GetString("Plugin_PackageConflict", other.Manifest.Id));
            }
        }
        return errors;
    }

    private IReadOnlyList<string> GetExecutionErrors(VisualPluginPackage package)
    {
        var installed = Registrations.Where(registration => string.Equals(registration.Id, package.Manifest.Id,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (installed.Length != 1 || !installed[0].IsEnabled || !installed[0].IsAvailable
            || !string.Equals(installed[0].Version, package.Manifest.Version, StringComparison.Ordinal))
        {
            return [LanguageService.Instance.GetString("Plugin_RuntimeUnavailable", package.Manifest.Id)];
        }
        return ValidateRequirements(package);
    }
}

/// <summary>Installed visual-plugin metadata and current host compatibility state.</summary>
public sealed class VisualPluginRegistration
{
    internal VisualPluginRegistration(string filePath, VisualPluginPackage package, bool enabled,
        IReadOnlyList<string> errors)
    {
        FilePath = filePath;
        Package = package;
        IsEnabled = enabled;
        Errors = errors;
    }

    internal static VisualPluginRegistration Invalid(string path, string error) => new(path,
        new VisualPluginPackage
        {
            Manifest = new VisualPluginManifest
            {
                Id = Path.GetFileNameWithoutExtension(path),
                Name = Path.GetFileName(path),
                Version = "?"
            }
        }, false, [error]);

    public string FilePath { get; }
    public VisualPluginPackage Package { get; }
    public string Id => Package.Manifest.Id;
    public string Name => Package.Manifest.Name;
    public string Version => Package.Manifest.Version;
    public string Author => Package.Manifest.Author;
    public string Description => Package.Manifest.Description;
    public VisualPluginKind Kind => Package.Kind;
    public bool IsEnabled { get; }
    public bool IsTemplate => Kind == VisualPluginKind.WorkflowTemplate;
    public bool ShowEnableAction => Kind == VisualPluginKind.CompositeNode && !IsEnabled;
    public bool ShowDisableAction => Kind == VisualPluginKind.CompositeNode && IsEnabled;
    public IReadOnlyList<string> Errors { get; }
    public bool IsAvailable => Errors.Count == 0;
    public string RequiredNodes => string.Join(", ", Package.Manifest.RequiredNodeTypes);
    public string Capabilities => string.Join(", ", Package.Manifest.Capabilities);
    public string Platforms => Package.Manifest.Platforms.Count == 0
        ? "Windows / Linux / macOS"
        : string.Join(", ", Package.Manifest.Platforms);
    public string ErrorText => string.Join(Environment.NewLine, Errors);
}

internal sealed class VisualPluginManagerState
{
    public List<string> DisabledIds { get; set; } = [];
}

[JsonSerializable(typeof(VisualPluginManagerState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class VisualPluginManagerJsonContext : JsonSerializerContext;