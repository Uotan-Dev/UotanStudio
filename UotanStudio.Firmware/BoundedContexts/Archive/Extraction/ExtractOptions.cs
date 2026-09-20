namespace UotanStudio.Firmware.BoundedContexts.Archive.Extraction;

public sealed class ExtractOptions
{
    public bool Overwrite { get; init; } = true;

    public bool MakeDirectories { get; init; } = true;

    public AbsolutePathPolicy AbsolutePathPolicy { get; init; } = AbsolutePathPolicy.StripRoot;

    public bool RestoreModificationTime { get; init; } = true;

    public bool RestorePermissions { get; init; } = true;

    public bool RestoreOwnership { get; init; }

    public IReadOnlyList<string> IncludePatterns { get; init; } = [];

    public string? PatternFilePath { get; init; }

    public bool NonMatching { get; init; }

    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    public IReadOnlyDictionary<string, string> RenameMap { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool DeviceIndependentHardlinks { get; init; }
}