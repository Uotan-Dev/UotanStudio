using System.Diagnostics.CodeAnalysis;
using UotanStudio.Projects;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Ui.Workspace;

/// <summary>
/// Adapts the current desktop workspace to project data and document operations.
/// <para>把当前桌面工作区适配到工程数据与文档操作。</para>
/// </summary>
internal sealed class ProjectWorkspaceCoordinator : IProjectWorkspace
{
    internal NodeEditorViewModel? NodeEditor { get; set; }
    internal WorkspaceViewModel? Workspace { get; set; }

    public bool IsImageOperationRunning => ImageViewModel.Instance.IsOperating;
    public bool HasNodeEditor => NodeEditor is not null;
    public void ClearNodes() => NodeEditor?.CloseAllDocuments();
    public void ActivateWorkflow(string name) => NodeEditor?.ActivateDocument(name);
    public void CloseWorkflow(string name) => NodeEditor?.CloseDocument(name);
    public IReadOnlyList<string> OpenWorkflowNames
        => NodeEditor?.OpenDocuments.Select(document => document.Name).ToArray() ?? [];
    public string? ActiveWorkflowName
        => NodeEditor is { } editor && editor.ActiveDocumentName.Length > 0 ? editor.ActiveDocumentName : null;
    public void ClearImages() => ImageViewModel.EnsureInstance().Images.Clear();
    public void ApplySettings(ProjectSettingsDto settings) => ProjectSettingViewModel.EnsureInstance().ApplyFromDto(settings);

    /// <summary>Root-change notification: with <paramref name="preserveDocuments"/> false all open documents close; with true they stay and their records re-resolve against the new root.
    /// <para>工程根目录变更通知：为 false 时关闭全部已打开文档，为 true 时保留并让记录按新根重算。</para></summary>
    /// <param name="rootDirectory">The new project root; null leaves open documents untouched.
    /// <para>新的工程根目录；为 null 时不改动已打开文档。</para></param>
    /// <param name="preserveDocuments">True to keep open documents through the change.
    /// <para>为真时变更过程中保留已打开文档。</para></param>
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
                Format = RecoverUnknownFormat(dto)
            });
    }

    /// <summary>
    /// Re-probes an entry whose persisted label is <c>unknown</c>, so projects saved before a format gained a
    /// detector show the current label instead of the stale placeholder. Missing or unreadable files keep the
    /// persisted label.
    /// <para>对持久化标签为 <c>unknown</c> 的条目重新探测，使在某个格式获得探测器之前保存的工程显示当前标签
    /// 而不是过期占位符；文件缺失或不可读时保留持久化标签。</para>
    /// </summary>
    /// <param name="dto">Persisted image entry. <para>持久化的镜像条目。</para></param>
    /// <returns>The persisted label, or the freshly probed label when re-probing succeeds.
    /// <para>持久化标签；重新探测成功时为最新探测出的标签。</para></returns>
    private static string RecoverUnknownFormat(ImageEntryDto dto)
    {
        if (!string.Equals(dto.Format, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return dto.Format;
        }

        try
        {
            var absolute = ProjectContextService.Instance.ResolvePath(dto.RelativePath);
            return absolute is not null && File.Exists(absolute)
                ? ImageInputService.DetectFormat(absolute)
                : dto.Format;
        }
        catch
        {
            return dto.Format;
        }
    }

    public Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory)
        => Workspace?.RestoreDocumentsAsync(documents, rootDirectory) ?? Task.CompletedTask;

    public Task RestoreWorkflowAsync(string name, NodeProjectSnapshot snapshot)
    {
        var editor = NodeEditor ?? throw new InvalidOperationException("No node editor is bound.");
        editor.OpenDocument(new NodeProjectDocument(name));
        return editor.LoadFromAsync(new WorkflowSnapshot
        {
            FormatVersion = snapshot.FormatVersion,
            Nodes = snapshot.Nodes,
            Connections = snapshot.Connections
        });
    }

    public bool TryCaptureWorkflow(string name, [NotNullWhen(true)] out NodeProjectSnapshot? snapshot)
    {
        if (NodeEditor?.FindDocument(name) is not { } document)
        {
            snapshot = null;
            return false;
        }
        snapshot = new NodeProjectSnapshot
        {
            FormatVersion = NodeProjectSnapshot.CurrentFormatVersion,
            ProjectName = name,
            Nodes = WorkflowPersistenceService.BuildNodeDtos(document.Nodes),
            Connections = WorkflowPersistenceService.BuildConnectionDtos(document.Nodes, document.Connections)
        };
        return true;
    }

    public List<ImageEntryDto> CaptureImages() => [.. ImageViewModel.EnsureInstance().Images.Select(i => new ImageEntryDto { RelativePath = i.RelativePath, SizeBytes = i.SizeBytes, Format = i.Format })];

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
                if (!DocumentSaveService.IsDirty(document)
                    || document is FileViewModel { IsBinary: true }) continue;
                await DocumentSaveService.SaveAsync(document);
            }
            catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "AutoSave", ex.Message); }
        }
    }

    public Task ImportFilesAsync(string[] paths) => ImageViewModel.EnsureInstance().ImportAsync(paths);
}