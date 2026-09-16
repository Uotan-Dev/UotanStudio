namespace UotanStudio.Services.Projects;

/// <summary>Host boundary for project operations. Captured data never exposes controls or view models.</summary>
/// <remarks>Methods run on the caller's synchronization context. Hosts own editor I/O, cancellation of
/// their own work, and the thread used to call the session. Confirmation callbacks must not recursively
/// start another project change; they may await persistence and document saves.</remarks>
public interface IProjectWorkspace : IProjectDataHost, IProjectDocumentHost, IProjectImportHost;

/// <summary>Project model capture and restoration, independent of editor persistence.</summary>
public interface IProjectDataHost
{
    /// <summary>Whether missing image files should remain as descriptors in this host.</summary>
    bool PreserveMissingImages => false;
    /// <summary>Notifies the host of a committed root change; existing document identities may be preserved.</summary>
    void ChangeProjectRoot(string? rootDirectory, bool preserveDocuments) { }
    /// <summary>Indicates host work that must prevent changing or closing the project.</summary>
    bool IsImageOperationRunning { get; }
    /// <summary>Host capability indicator; the library never requires an editor to persist data.</summary>
    bool HasNodeEditor { get; }
    /// <summary>Clears the host workflow representation.</summary>
    void ClearNodes();
    /// <summary>Clears the host image representation.</summary>
    void ClearImages();
    /// <summary>Applies packing settings to the host.</summary>
    void ApplySettings(ProjectSettingsDto settings);
    /// <summary>Restores image descriptors supplied by the project file.</summary>
    void RestoreImages(IEnumerable<ImageEntryDto> images);
    /// <summary>Restores document descriptors relative to the specified solution data root.</summary>
    Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory);
    /// <summary>Restores the workflow snapshot; the host decides unknown-node handling.</summary>
    Task RestoreWorkflowAsync(NodeProjectSnapshot snapshot);
    /// <summary>Captures a workflow snapshot without exposing editor objects.</summary>
    NodeProjectSnapshot CaptureWorkflow(string projectName);
    /// <summary>Captures the current image list.</summary>
    List<ImageEntryDto> CaptureImages();
    /// <summary>Captures current packing settings.</summary>
    ProjectSettingsDto CaptureSettings();
    /// <summary>Captures document identities relative to the requested root, including for Save As.</summary>
    List<OpenDocumentDto> CaptureDocuments(string rootDirectory);
}

/// <summary>Editor save policy provided by the host.</summary>
public interface IProjectDocumentHost
{
    /// <summary>Saves the active document using the host's existing editor policy.</summary>
    Task SaveActiveDocumentAsync();
    /// <summary>Saves the supported modified documents using the host's existing auto-save policy.</summary>
    Task AutoSaveDocumentsAsync();
}

/// <summary>Host-specific file recognition and import interaction.</summary>
public interface IProjectImportHost
{
    /// <summary>Imports files using host-specific detection and interaction.</summary>
    Task ImportFilesAsync(string[] paths);
}