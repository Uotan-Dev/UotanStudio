namespace UotanStudio.Services.Projects;

/// <summary>Editable data for a host without an editor. Access it only when the owning session is idle.</summary>
public sealed class ProjectDataWorkspace : IProjectWorkspace
{
    private string? _documentRoot;
    /// <summary>Current workflow snapshot, including unknown node type keys.</summary>
    public NodeProjectSnapshot Workflow { get; set; } = new();
    /// <summary>Current image entries, relative to the solution data directory.</summary>
    public List<ImageEntryDto> Images { get; set; } = [];
    /// <summary>Current packing settings.</summary>
    public ProjectSettingsDto Settings { get; set; } = new();
    /// <summary>Document descriptors, treated as opaque data by this host.</summary>
    public List<OpenDocumentDto> Documents { get; set; } = [];
    /// <summary>Whether the host is performing an operation that prevents project changes.</summary>
    public bool IsImageOperationRunning { get; set; }
    /// <inheritdoc />
    public bool HasNodeEditor => true;
    /// <inheritdoc />
    public bool PreserveMissingImages => true;
    /// <inheritdoc />
    public void ChangeProjectRoot(string? rootDirectory, bool preserveDocuments)
    {
        if (rootDirectory is not null) ChangeRoot(rootDirectory, preserveDocuments);
        else { Documents.Clear(); Settings = new(); _documentRoot = null; }
    }
    /// <inheritdoc />
    public void ClearNodes() => Workflow = new();
    /// <inheritdoc />
    public void ClearImages() => Images.Clear();
    /// <inheritdoc />
    public void ApplySettings(ProjectSettingsDto settings) => Settings = settings;
    /// <inheritdoc />
    public void RestoreImages(IEnumerable<ImageEntryDto> images) => Images.AddRange(images);
    /// <inheritdoc />
    public Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory)
    {
        Documents = documents;
        _documentRoot = rootDirectory;
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task RestoreWorkflowAsync(NodeProjectSnapshot snapshot)
    {
        snapshot.EnsureSupportedVersion();
        Workflow = snapshot;
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public NodeProjectSnapshot CaptureWorkflow(string projectName) => new()
    {
        FormatVersion = Workflow.FormatVersion,
        ProjectName = projectName,
        Nodes = Workflow.Nodes.ToList(),
        Connections = Workflow.Connections.ToList()
    };
    /// <inheritdoc />
    public List<ImageEntryDto> CaptureImages() => Images.ToList();
    /// <inheritdoc />
    public ProjectSettingsDto CaptureSettings() => Settings;
    /// <inheritdoc />
    public List<OpenDocumentDto> CaptureDocuments(string rootDirectory) => Documents.Select(document => new OpenDocumentDto
    {
        Kind = document.Kind,
        OpenMode = document.OpenMode,
        IsActive = document.IsActive,
        RelativePath = Rebase(document.RelativePath, rootDirectory) ?? string.Empty,
        CompareLeftPath = Rebase(document.CompareLeftPath, rootDirectory),
        CompareRightPath = Rebase(document.CompareRightPath, rootDirectory)
    }).ToList();

    private string? Rebase(string? path, string destinationRoot)
        => string.IsNullOrEmpty(path) || _documentRoot is null ? path
            : Path.GetRelativePath(destinationRoot, Path.GetFullPath(Path.Combine(_documentRoot, path))).Replace(Path.DirectorySeparatorChar, '/');

    internal void ChangeRoot(string root, bool preserveDocuments)
    {
        Documents = preserveDocuments ? CaptureDocuments(root) : [];
        _documentRoot = root;
    }
    /// <inheritdoc />
    public Task SaveActiveDocumentAsync() => Task.CompletedTask;
    /// <inheritdoc />
    public Task AutoSaveDocumentsAsync() => Task.CompletedTask;
    /// <summary>File format detection and import are host responsibilities; edit Images directly in data-only hosts.</summary>
    public Task ImportFilesAsync(string[] paths) => throw new NotSupportedException("Supply image descriptors or a host import adapter.");
}