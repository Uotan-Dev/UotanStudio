namespace UotanStudio.Services.Projects;

/// <summary>UI boundary for project operations. Captured data never exposes controls or view models.</summary>
internal interface IProjectWorkspace
{
    bool IsImageOperationRunning { get; }
    bool HasNodeEditor { get; }
    void ClearNodes();
    void ClearImages();
    void ApplySettings(ProjectSettingsDto settings);
    void RestoreImages(IEnumerable<ImageEntryDto> images);
    Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory);
    Task RestoreWorkflowAsync(NodeProjectSnapshot snapshot);
    NodeProjectSnapshot CaptureWorkflow(string projectName);
    List<ImageEntryDto> CaptureImages();
    ProjectSettingsDto CaptureSettings();
    List<OpenDocumentDto> CaptureDocuments(string rootDirectory);
    Task SaveActiveDocumentAsync();
    Task AutoSaveDocumentsAsync();
    Task ImportFilesAsync(string[] paths);
}