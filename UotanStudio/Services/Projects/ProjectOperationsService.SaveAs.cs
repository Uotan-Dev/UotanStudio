using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;

namespace UotanStudio.Services.Projects;

public sealed partial class ProjectOperationsService
{
    /// <summary>Publishes a prepared copy before changing the live project context.</summary>
    private async Task SaveAsCoreAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!_workspace.HasNodeEditor) throw new InvalidOperationException("No node editor is bound.");
        var context = ProjectContextService.Instance;
        var sourceRoot = context.RootDirectory;
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (context.CurrentSolutionPath is { } current && string.Equals(current, fullPath, comparison))
        {
            await PersistCoreAsync(fullPath);
            return;
        }

        using var transaction = new ProjectSaveAsTransaction(fullPath);
        if (sourceRoot is not null && (ProjectSaveAsTransaction.IsWithin(sourceRoot, transaction.RootDirectory)
            || ProjectSaveAsTransaction.IsWithin(transaction.RootDirectory, sourceRoot)))
            throw new IOException("The source and destination project directories must not overlap.");

        var subProjects = context.SubProjects.ToList();
        // Do not silently rewrite or modify shared projects outside the source root.
        var references = subProjects.Select(sp => new SubProjectDto
        {
            Name = sp.Name,
            RelativePath = ProjectSaveAsTransaction.RelativePathWithin(sourceRoot!, sp.FwProjectPath)
        }).ToList();

        var name = Path.GetFileName(transaction.RootDirectory);
        var activeName = context.ActiveSubProject?.Name;
        var node = _workspace.CaptureWorkflow(name);
        var images = _workspace.CaptureImages();
        var packing = _workspace.CaptureSettings();
        // Existing editors keep their original file identity. Store those paths relative to the new root.
        var documents = _workspace.CaptureDocuments(transaction.RootDirectory);
        var history = OperationHistoryService.Instance.CaptureSnapshot();
        var sourceSettingsPath = context.StateDirectory is { } state ? Path.Combine(state, "settings.json") : null;
        var settings = sourceSettingsPath is null ? new SolutionSettings()
            : await SolutionSettingsService.LoadAsync(sourceSettingsPath)
                ?? (File.Exists(sourceSettingsPath) ? throw new InvalidDataException("Cannot read the source project settings.") : new SolutionSettings());
        settings.RecentDocuments = documents.Select(d => d.RelativePath).Where(p => p.Length > 0).ToList();

        await transaction.PrepareAsync(sourceRoot);
        if (references.Count == 0)
        {
            activeName = name;
            references.Add(new() { Name = name, RelativePath = $"{name}/{name}.uotanfw" });
        }
        var active = references.FirstOrDefault(sp => sp.Name == activeName) ?? references[0];
        var activePath = Path.Combine(transaction.StagingRoot, active.RelativePath);
        var activeRoot = Path.GetDirectoryName(activePath)!;
        Directory.CreateDirectory(Path.Combine(activeRoot, "images"));
        Directory.CreateDirectory(Path.Combine(activeRoot, "output"));
        await AtomicJsonFile.WriteIfChangedAsync(activePath, new FirmwareProjectSnapshot
        {
            ProjectName = active.Name,
            Images = images,
            Settings = packing,
            OpenDocuments = documents.Count > 0 ? documents : null
        }, SolutionJsonContext.Default.FirmwareProjectSnapshot);
        await AtomicJsonFile.WriteIfChangedAsync(Path.Combine(transaction.StagingRoot, name + ".uotannode"), node,
            SolutionJsonContext.Default.NodeProjectSnapshot);
        await AtomicJsonFile.WriteIfChangedAsync(Path.Combine(transaction.StagingRoot, ".uotan", "settings.json"), settings,
            SolutionSettingsJsonContext.Default.SolutionSettings);
        await AtomicJsonFile.WriteIfChangedAsync(Path.Combine(transaction.StagingRoot, ".uotan", "history.json"), history,
            HistoryJsonContext.Default.ListHistoryOperationDto);
        var solution = new SolutionSnapshot
        {
            SolutionName = name,
            NodeProjectPath = $"{name}/{name}.uotannode",
            SubProjects = references
        };
        await AtomicJsonFile.WriteIfChangedAsync(transaction.StagingSolution, solution, SolutionJsonContext.Default.SolutionSnapshot);
        transaction.Commit();

        context.OpenSolution(fullPath, solution);
        context.SetActiveSubProject(active.Name);
        CurrentProjectPath = fullPath;
        WeakReferenceMessenger.Default.Send(new ProjectContextChangedMessage());
    }
}