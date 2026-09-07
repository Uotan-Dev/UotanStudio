namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;

public sealed class ArchiveEntry
{
    public required string Name { get; init; }

    public string? LinkName { get; init; }

    public required ArchiveEntryMetadata Metadata { get; init; }

    public required byte[] Data { get; init; }

    public Stream? DataStream { get; init; }

    public Func<Stream>? DataStreamFactory { get; init; }

    public long? DeclaredDataLength { get; init; }

    public long DataLength
    {
        get
        {
            if (DeclaredDataLength is < 0)
            {
                throw new InvalidDataException($"Entry '{Name}' has a negative declared payload length.");
            }

            return DeclaredDataLength ?? (DataStream is { CanSeek: true } stream
                ? stream.Length
                : DataStream is not null || DataStreamFactory is not null
                    ? throw new InvalidOperationException($"Entry '{Name}' has a streaming payload with unknown length.")
                    : Data.LongLength);
        }
    }
}
