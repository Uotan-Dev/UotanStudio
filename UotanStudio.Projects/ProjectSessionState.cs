namespace UotanStudio.Services.Projects;

/// <summary>Immutable view of a session's committed directory context.</summary>
public sealed record ProjectSessionState(string? SolutionPath, string? RootDirectory,
    string? ActiveSubProjectName, IReadOnlyList<SubProjectContext> SubProjects);