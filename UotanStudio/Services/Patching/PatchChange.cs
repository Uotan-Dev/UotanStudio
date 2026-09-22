namespace UotanStudio.Services.Patching;

/// <summary>Net file effect, compared with the original filesystem state.</summary>
public enum PatchChangeKind
{
    Added,
    Modified,
    Deleted,
    Unchanged
}

/// <summary>Publication state of the patch-owned effects of an execution.</summary>
public enum PatchRunOutcome
{
    None,
    Staged,
    Previewed,
    Applied,
    AlreadyApplied,
    Discarded,
    Conflict,
    RolledBack,
    RecoveryRequired,
    NotApplicable,
    NoChanges
}

/// <summary>Immutable review data; hashes identify complete files, previews are deliberately bounded.</summary>
public sealed record PatchChange(
    string Path, PatchChangeKind Kind, long BeforeBytes, long AfterBytes,
    string? BeforeSha256, string? AfterSha256, string? BeforePreview = null, string? AfterPreview = null)
{
    /// <summary>Direct composite invocations that staged this path; zero denotes the root graph.
    /// Includes no-op staging and does not imply that an invocation's bytes survived later edits.</summary>
    public IReadOnlyList<int> StagingInvocations { get; init; } = [];

    /// <summary>Bounded chronological replacement-window snapshots, not the final net byte diff.</summary>
    public IReadOnlyList<PatchBinaryPreview> BinaryPreviews { get; init; } = [];

    /// <summary>Whether additional replacement windows were omitted from this review.</summary>
    public bool BinaryPreviewTruncated { get; init; }
}

/// <summary>First bytes of one replacement window; invocation zero denotes the root workflow.</summary>
public sealed record PatchBinaryPreview(long Offset, int Length, int Invocation, string BeforeHex, string AfterHex);

/// <summary>A file no longer matches the state against which the patch was prepared.</summary>
public sealed class PatchConflictException(string path) : IOException($"Patch input changed: {path}")
{
    /// <summary>Conflicting target or source.</summary>
    public string Path { get; } = path;
}

/// <summary>A compatible target has an ambiguous or unrecognized complete patch state.</summary>
public sealed class PatchApplicabilityConflictException(string message) : IOException(message);

/// <summary>Explicit overwrite policy for a staged file operation.</summary>
public enum PatchFileMode
{
    Add,
    Replace,
    Upsert
}