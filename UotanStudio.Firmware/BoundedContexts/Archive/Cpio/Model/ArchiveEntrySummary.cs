namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;

public sealed class ArchiveEntrySummary
{
    public required string Name { get; init; }

    public string? LinkName { get; init; }

    public required ArchiveEntryMetadata Metadata { get; init; }

    public long DataLength { get; init; }
}
