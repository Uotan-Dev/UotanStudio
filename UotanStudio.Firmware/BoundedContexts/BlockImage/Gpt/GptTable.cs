namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Gpt;

/// <summary>A partition from a validated pair of GPT arrays. End LBA is inclusive.</summary>
public sealed record GptPartition(int Index, Guid TypeId, Guid Id, ulong FirstLba, ulong LastLba,
    ulong Attributes, string Name, long ByteOffset, long ByteLength);

/// <summary>A matching primary/backup GPT snapshot, not a guarantee of immutable device contents.</summary>
public sealed class GptTable
{
    internal GptTable(Guid diskId, int blockSize, ulong blockCount, List<GptPartition> partitions, string metadataSha256)
    {
        DiskId = diskId;
        BlockSize = blockSize;
        BlockCount = blockCount;
        Partitions = partitions.AsReadOnly();
        MetadataSha256 = metadataSha256;
    }

    /// <summary>Gets the disk identifier shared by both headers.</summary>
    public Guid DiskId { get; }
    /// <summary>Gets the explicitly supplied logical block size.</summary>
    public int BlockSize { get; }
    /// <summary>Gets the explicitly supplied device block count.</summary>
    public ulong BlockCount { get; }
    /// <summary>Gets used entries in original array order.</summary>
    public IReadOnlyList<GptPartition> Partitions { get; }
    /// <summary>Gets SHA-256 of the two header-block SHA-256 digests followed by the exact entry array, excluding sector padding.</summary>
    public string MetadataSha256 { get; }

    /// <summary>Resolves an exact, case-sensitive name, rejecting missing or ambiguous names.</summary>
    public GptPartition FindPartition(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        GptPartition? match = null;
        foreach (var partition in Partitions)
        {
            if (!string.Equals(partition.Name, name, StringComparison.Ordinal))
            {
                continue;
            }
            if (match is not null)
            {
                throw new InvalidDataException("The GPT partition name is ambiguous.");
            }
            match = partition;
        }
        return match ?? throw new KeyNotFoundException("The GPT partition name was not found.");
    }
}
