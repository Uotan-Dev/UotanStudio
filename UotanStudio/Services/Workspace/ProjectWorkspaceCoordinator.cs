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

    /// <summary>
    /// <para>工程根目录变更通知：<paramref name="preserveDocuments"/> 为 false 时（新建解决方案、
    /// 打开工程、关闭工程）关闭工作区全部文档型面板，避免上一工程的编辑器残留；为 true 时
    /// （另存为）保留已打开文档，页面记录按新根重算相对路径。</para>
    /// Root-change notification: with <paramref name="preserveDocuments"/> false (new solution, open,
    /// close) closes every document-kind panel so editors of the previous solution do not linger;
    /// with true (save-as) documents stay open and page records re-resolve against the new root.
    /// </summary>
    public void ChangeProjectRoot(string? rootDirectory, bool preserveDocuments)
    {
        if (preserveDocuments || rootDirectory is null) return;
        Workspace?.DockFactory?.CloseAllDocuments(Workspace.Layout);
    }

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
                // Use the same save contract as the Save command for every editor that exposes
                // ISaveableDocument (including Hex, ARSC, DEX, APK and future editors). Binary
                // text views remain excluded because their Save operation is intentionally a no-op.
                if (!DocumentSaveService.IsDirty(document)
                    || document is FileViewModel { IsBinary: true }) continue;
                await DocumentSaveService.SaveAsync(document);
            }
            catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "AutoSave", ex.Message); }
        }
    }

    public Task ImportFilesAsync(string[] paths) => ImageViewModel.EnsureInstance().ImportAsync(paths);
}