using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Workspace;

/// <summary>Adapts the current desktop workspace to project data and document operations.</summary>
internal sealed class ProjectWorkspaceCoordinator : IProjectWorkspace
{
    internal NodeEditorViewModel? NodeEditor { get; set; }
    internal WorkspaceViewModel? Workspace { get; set; }

    public bool IsImageOperationRunning => ImageViewModel.Instance.IsOperating;
    public bool HasNodeEditor => NodeEditor is not null;
    public void ClearNodes() => NodeEditor?.Clear();
    public void ClearImages() => ImageViewModel.EnsureInstance().Images.Clear();
    public void ApplySettings(ProjectSettingsDto settings) => ProjectSettingViewModel.EnsureInstance().ApplyFromDto(settings);

    public void RestoreImages(IEnumerable<ImageEntryDto> images)
    {
        var target = ImageViewModel.EnsureInstance().Images;
        foreach (var dto in images)
            target.Add(new ImageEntryViewModel
            {
                DisplayName = Path.GetFileName(dto.RelativePath),
                RelativePath = dto.RelativePath,
                SizeBytes = dto.SizeBytes,
                Format = dto.Format
            });
    }

    public Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory)
        => Workspace?.RestoreDocumentsAsync(documents, rootDirectory) ?? Task.CompletedTask;

    public Task RestoreWorkflowAsync(NodeProjectSnapshot snapshot)
        => NodeEditor?.LoadFromAsync(new WorkflowSnapshot
        {
            FormatVersion = 1,
            Nodes = snapshot.Nodes,
            Connections = snapshot.Connections
        }) ?? Task.CompletedTask;

    public NodeProjectSnapshot CaptureWorkflow(string projectName)
    {
        var nodes = NodeEditor ?? throw new InvalidOperationException("No node editor is bound.");
        return new NodeProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = projectName,
            Nodes = WorkflowPersistenceService.BuildNodeDtos(nodes.Nodes),
            Connections = WorkflowPersistenceService.BuildConnectionDtos(nodes.Nodes, nodes.Connections)
        };
    }

    public List<ImageEntryDto> CaptureImages() => ImageViewModel.EnsureInstance().Images
        .Select(i => new ImageEntryDto { RelativePath = i.RelativePath, SizeBytes = i.SizeBytes, Format = i.Format }).ToList();

    public ProjectSettingsDto CaptureSettings() => ProjectSettingViewModel.EnsureInstance().ToDto();

    public List<OpenDocumentDto> CaptureDocuments(string rootDirectory)
        => WorkspaceSessionCapture.Documents(Workspace?.Layout, rootDirectory);

    public async Task SaveActiveDocumentAsync()
    {
        if (Workspace?.Layout is null) return;
        if (Workspace.FindDocumentsPanePublic()?.ActiveDockable is not { } active) return;
        if (DocumentSaveService.CanSave(active)) await DocumentSaveService.SaveAsync(active);
    }

    public async Task AutoSaveDocumentsAsync()
    {
        foreach (var document in WorkspaceSessionCapture.Enumerate(Workspace?.Layout).ToList())
        {
            try
            {
                switch (document)
                {
                    case FileViewModel { IsDirty: true, IsBinary: false } text:
                        await text.SaveAsync();
                        break;
                    case DtbEditorViewModel { IsDirty: true } dtb when !dtb.SaveCommand.IsRunning:
                        await dtb.SaveCommand.ExecuteAsync(null);
                        break;
                    case ManifestEditorViewModel { IsDirty: true, IsBusy: false } manifest when !manifest.SaveCommand.IsRunning:
                        await manifest.SaveCommand.ExecuteAsync(null);
                        break;
                    case PartitionEditorViewModel { IsDirty: true, IsBusy: false } partition when !partition.SaveCommand.IsRunning:
                        await partition.SaveCommand.ExecuteAsync(null);
                        break;
                }
            }
            catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "AutoSave", ex.Message); }
        }
    }

    public Task ImportFilesAsync(string[] paths) => ImageViewModel.EnsureInstance().ImportAsync(paths);
}