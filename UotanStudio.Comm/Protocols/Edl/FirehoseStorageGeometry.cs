namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Validated geometry reported for one explicitly queried physical storage region.</summary>
public sealed record FirehoseStorageGeometry
{
    /// <summary>Creates a geometry with checked sector and byte capacities.</summary>
    /// <param name="physicalPartitionNumber">The queried physical region or LUN.</param>
    /// <param name="sectorSize">The reported block size in bytes.</param>
    /// <param name="sectorCount">The reported total block count.</param>
    public FirehoseStorageGeometry(int physicalPartitionNumber, long sectorSize, long sectorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartitionNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sectorSize, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorCount);
        PhysicalPartitionNumber = physicalPartitionNumber;
        SectorSize = sectorSize;
        SectorCount = sectorCount;
        CapacityBytes = checked(sectorSize * sectorCount);
    }

    /// <summary>Gets the physical region whose command produced this report.</summary>
    public int PhysicalPartitionNumber { get; }
    /// <summary>Gets the block size in bytes.</summary>
    public long SectorSize { get; }
    /// <summary>Gets the number of addressable blocks.</summary>
    public long SectorCount { get; }
    /// <summary>Gets the checked byte capacity.</summary>
    public long CapacityBytes { get; }

    /// <summary>Resolves a constant or disk-relative range and rejects any address beyond storage.</summary>
    /// <param name="startSector">A constant or NUM_DISK_SECTORS-minus-offset expression.</param>
    /// <param name="numSectors">The positive range length in blocks.</param>
    /// <returns>The resolved absolute starting sector.</returns>
    public long ResolveRange(string startSector, long numSectors)
    {
        var address = FirehoseSectorAddress.Parse(startSector);
        address.ValidateRange(numSectors);
        var start = address.RelativeToEnd ? checked(SectorCount - address.Value) : address.Value;
        if (start < 0 || start > SectorCount || numSectors > SectorCount - start)
        {
            throw new ArgumentOutOfRangeException(nameof(startSector), "The range exceeds the reported physical storage capacity.");
        }
        return start;
    }
}