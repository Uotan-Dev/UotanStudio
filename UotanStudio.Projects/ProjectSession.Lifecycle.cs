namespace UotanStudio.Services.Projects;

public sealed partial class ProjectSession
{
    /// <summary>Context belonging to this session. Hosts should use session operations for mutations.</summary>
    public ProjectContext Context { get; }
    /// <summary>History belonging to this session; executable actions are supplied by the host.</summary>
    public ProjectHistory History { get; }
    /// <summary>Data adapter supplied at construction, or the default ProjectDataWorkspace.</summary>
    public IProjectWorkspace Workspace => _workspace;
    /// <summary>Captures directory state without exposing context mutation methods.</summary>
    public ProjectSessionState State => new(Context.CurrentSolutionPath, Context.RootDirectory,
        Context.ActiveSubProject?.Name, Array.AsReadOnly(Context.SubProjects.ToArray()));
    /// <summary>Raised synchronously after successful lifecycle operations, on the calling context.</summary>
    public event Action? Changed;

    /// <summary>Switches active sub-project under the operation gate. Optional persistence saves the old project's data first.</summary>
    public async Task<bool> SwitchSubProjectAsync(string name, bool persistCurrent = true, CancellationToken cancellationToken = default)
    {
        var switched = false;
        await RunOperationAsync(async () =>
        {
            var target = Context.SubProjects.FirstOrDefault(sp => string.Equals(sp.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;
            if (target == Context.ActiveSubProject) { switched = true; return; }
            var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(target.FwProjectPath, cancellationToken)
                ?? throw new InvalidDataException("Cannot read the target sub-project.");
            if (persistCurrent && CurrentProjectPath is { } path) await PersistCoreAsync(path, false, cancellationToken);
            var history = new ProjectHistory();
            await history.LoadAsync(Path.Combine(target.StateDirectory, "history.json"), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Context.SetActiveSubProject(name);
            History.CopyFrom(history);
            _workspace.ClearImages();
            _workspace.ApplySettings(firmware.Settings ?? new());
            _workspace.RestoreImages(firmware.Images.Where(dto => _workspace.PreserveMissingImages || Context.ResolvePath(dto.RelativePath) is { } absolute && File.Exists(absolute)));
            switched = true;
            Changed?.Invoke();
        }, changingProject: true, cancellationToken: cancellationToken);
        return switched;
    }

    /// <summary>Closes the project after the host's change confirmation. Optional persistence does not save editor contents.</summary>
    public async Task<bool> CloseAsync(bool persistProject = false, CancellationToken cancellationToken = default, bool confirmChange = true)
    {
        var closed = false;
        await RunOperationAsync(async () =>
        {
            if (_workspace.IsImageOperationRunning) return;
            if (persistProject && CurrentProjectPath is { } path) await PersistCoreAsync(path, true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _workspace.ClearImages();
            _workspace.ClearNodes();
            _workspace.ChangeProjectRoot(null, preserveDocuments: false);
            History.Clear();
            Context.CloseProject();
            CurrentProjectPath = null;
            SolutionState = null;
            closed = true;
            Changed?.Invoke();
        }, changingProject: confirmChange, cancellationToken: cancellationToken);
        return closed;
    }

    /// <summary>Exports the current on-disk project under the same operation gate as persistence.</summary>
    public Task ExportAsync(string outputPath, CancellationToken cancellationToken = default)
        => RunOperationAsync(() => ProjectArchiveService.ExportAsync(
            Context.RootDirectory ?? throw new InvalidOperationException("No solution is open."), outputPath, cancellationToken),
            cancellationToken: cancellationToken);

    /// <summary>Persists current project data, then exports a reopenable package under the operation gate.</summary>
    public Task ExportPackageAsync(string outputPath, CancellationToken cancellationToken = default)
        => RunOperationAsync(async () =>
        {
            var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
            await PersistCoreAsync(path, true, cancellationToken);
            await ProjectArchiveService.ExportPackageAsync(path, outputPath, cancellationToken);
        }, cancellationToken: cancellationToken);

    /// <summary>Imports a package into a new directory and opens it after host confirmation.</summary>
    public async Task<bool> ImportPackageAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        var opened = false;
        await RunOperationAsync(async () =>
        {
            var path = await ProjectPackage.ImportAsync(archivePath, destinationDirectory, cancellationToken);
            opened = await OpenProjectFromPathCoreAsync(path, CancellationToken.None);
        }, changingProject: true, cancellationToken: cancellationToken);
        return opened;
    }
}