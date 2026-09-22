namespace UotanStudio.Services.Projects;

/// <summary>A file-backed workflow value. The path is explicit and never inferred from ordinary text.</summary>
public sealed record FileArtifact(string Path);