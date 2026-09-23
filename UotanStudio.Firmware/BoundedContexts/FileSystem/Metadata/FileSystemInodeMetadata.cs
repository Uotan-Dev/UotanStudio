namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;

/// <summary>A filesystem timestamp independent of the host clock range and precision.</summary>
public sealed record FileSystemTimestamp(long Seconds, uint Nanoseconds)
{
    internal void Validate()
    {
        if (Nanoseconds >= 1_000_000_000)
        {
            throw new InvalidDataException("Filesystem timestamp nanoseconds exceed one second.");
        }
    }
}

/// <summary>Source inode attributes that must not be inferred from the extracted host files.</summary>
public sealed record FileSystemInodeMetadata
{
    /// <summary>Gets the source access time, when represented by the format.</summary>
    public FileSystemTimestamp? AccessTime { get; init; }
    /// <summary>Gets the source inode change time, not the host creation time.</summary>
    public FileSystemTimestamp? ChangeTime { get; init; }
    /// <summary>Gets the source modification time.</summary>
    public FileSystemTimestamp? ModificationTime { get; init; }
    /// <summary>Gets the source birth time, when represented by the inode.</summary>
    public FileSystemTimestamp? BirthTime { get; init; }
    /// <summary>Gets the inode generation.</summary>
    public uint Generation { get; init; }
    /// <summary>Gets the project identifier, when present.</summary>
    public uint? ProjectId { get; init; }
    /// <summary>Gets the EXT4 inode version counter.</summary>
    public ulong? Version { get; init; }
    /// <summary>Gets the original link count; allocation-dependent counts are recomputed on editing.</summary>
    public uint LinkCount { get; init; }
    /// <summary>Gets the F2FS inode advice byte.</summary>
    public byte F2fsAdvise { get; init; }
    /// <summary>Gets the F2FS per-inode compression contract.</summary>
    public F2fsInodeCompression? F2fsCompression { get; init; }

    internal void Validate()
    {
        AccessTime?.Validate();
        ChangeTime?.Validate();
        ModificationTime?.Validate();
        BirthTime?.Validate();
    }
}

/// <summary>Kernel algorithm identifier, logical cluster size and flags for an F2FS inode.</summary>
public sealed record F2fsInodeCompression(byte Algorithm, byte LogClusterSize, ushort Flags);