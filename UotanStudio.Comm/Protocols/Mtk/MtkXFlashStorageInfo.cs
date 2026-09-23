using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Storage families supported by the XFlash block-storage query.</summary>
public enum MtkXFlashStorageKind
{
    /// <summary>Embedded MultiMediaCard storage.</summary>
    Emmc,
    /// <summary>Universal Flash Storage, using XFlash partition identifiers rather than XML LUN names.</summary>
    Ufs
}

/// <summary>One device-reported XFlash physical partition, not a GPT partition.</summary>
public sealed class MtkXFlashStorageRegion
{
    internal MtkXFlashStorageRegion(uint partitionId, long capacityBytes, bool authenticated)
    {
        PartitionId = partitionId;
        CapacityBytes = capacityBytes;
        RequiresAuthenticatedAccess = authenticated;
    }

    /// <summary>Gets the partition identifier used by XFlash storage commands.</summary>
    public uint PartitionId { get; }
    /// <summary>Gets the reported capacity in bytes.</summary>
    public long CapacityBytes { get; }
    /// <summary>Gets whether the region is RPMB and requires a separate authenticated protocol.</summary>
    public bool RequiresAuthenticatedAccess { get; }
}

/// <summary>Validated device-reported geometry and bounded packet sizes for one storage family.</summary>
public sealed class MtkXFlashStorageInfo
{
    private MtkXFlashStorageInfo(MtkXFlashStorageKind kind, uint deviceType, int blockSize,
        MtkXFlashStorageRegion[] regions, string cidHex, int writePacketLength, int readPacketLength)
    {
        Kind = kind;
        DeviceType = deviceType;
        BlockSize = blockSize;
        Regions = Array.AsReadOnly(regions);
        CidHex = cidHex;
        WritePacketLength = writePacketLength;
        ReadPacketLength = readPacketLength;
    }

    /// <summary>Gets the queried storage family.</summary>
    public MtkXFlashStorageKind Kind { get; }
    /// <summary>Gets the unmodified nonzero device type reported by the agent.</summary>
    public uint DeviceType { get; }
    /// <summary>Gets the storage block size in bytes.</summary>
    public int BlockSize { get; }
    /// <summary>Gets physical regions, including zero-capacity regions and any reported RPMB region.</summary>
    public IReadOnlyList<MtkXFlashStorageRegion> Regions { get; }
    /// <summary>Gets the reported 16-byte card identity encoded as hexadecimal.</summary>
    public string CidHex { get; }
    /// <summary>Gets the agent's bounded write packet length in bytes.</summary>
    public int WritePacketLength { get; }
    /// <summary>Gets the agent's bounded read packet length in bytes.</summary>
    public int ReadPacketLength { get; }

    /// <summary>Checks a byte range for ordinary block access; RPMB is deliberately excluded.</summary>
    /// <param name="partitionId">The XFlash physical partition identifier.</param>
    /// <param name="offset">The nonnegative block-aligned byte offset.</param>
    /// <param name="length">The positive block-aligned byte count.</param>
    public void ValidateRange(uint partitionId, long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var region = Regions.FirstOrDefault(region => region.PartitionId == partitionId)
            ?? throw new ArgumentOutOfRangeException(nameof(partitionId), "The physical partition was not reported.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException("RPMB is not an ordinary block-storage target.");
        }
        if (offset % BlockSize != 0 || length % BlockSize != 0
            || offset > region.CapacityBytes || length > region.CapacityBytes - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The requested range is unaligned or exceeds reported capacity.");
        }
    }

    internal static bool IsAbsent(ReadOnlySpan<byte> geometry)
    {
        if (geometry.Length < 4 || geometry.Length > 4096)
        {
            throw new MtkProtocolException("The storage query omitted its device type.");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(geometry) == 0;
    }

    internal static MtkXFlashStorageInfo Parse(MtkXFlashStorageKind kind, ReadOnlySpan<byte> geometry,
        ReadOnlySpan<byte> packetLengths)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var minimumLength = kind == MtkXFlashStorageKind.Emmc ? 96 : 48;
        if (geometry.Length < minimumLength || geometry.Length > 4096 || IsAbsent(geometry))
        {
            throw new MtkProtocolException("The selected storage geometry is missing, truncated or oversized.");
        }
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(geometry[4..]);
        if (blockSize < 512 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new MtkProtocolException("The storage block size is unsupported or invalid.");
        }
        if (packetLengths.Length != 8)
        {
            throw new MtkProtocolException("The packet-length reply must contain two 32-bit fields.");
        }
        var writeLength = BinaryPrimitives.ReadUInt32LittleEndian(packetLengths);
        var readLength = BinaryPrimitives.ReadUInt32LittleEndian(packetLengths[4..]);
        if (writeLength < blockSize || readLength < blockSize
            || writeLength > 1024 * 1024 || readLength > 1024 * 1024
            || writeLength % blockSize != 0 || readLength % blockSize != 0)
        {
            throw new MtkProtocolException("The storage packet sizes are unsupported, unaligned or unbounded.");
        }
        var count = kind == MtkXFlashStorageKind.Emmc ? 8 : 3;
        var regions = new MtkXFlashStorageRegion[count];
        for (var index = 0; index < count; index++)
        {
            // XFlash UFS wire sizes are ordered boot2, boot1, user, not partition-ID order.
            var wireIndex = kind == MtkXFlashStorageKind.Emmc ? index : index switch { 0 => 1, 1 => 0, _ => 2 };
            var size = BinaryPrimitives.ReadUInt64LittleEndian(geometry[(8 + wireIndex * 8)..]);
            if (size > long.MaxValue || size % blockSize != 0)
            {
                throw new MtkProtocolException("The reported physical capacity overflows or is not block-aligned.");
            }
            regions[index] = new MtkXFlashStorageRegion((uint)index + 1, (long)size,
                kind == MtkXFlashStorageKind.Emmc && index == 2);
        }
        if (!regions.Any(region => region.CapacityBytes > 0 && !region.RequiresAuthenticatedAccess))
        {
            throw new MtkProtocolException("The selected storage has no ordinary addressable region.");
        }
        var cidOffset = kind == MtkXFlashStorageKind.Emmc ? 72 : 32;
        return new MtkXFlashStorageInfo(kind, BinaryPrimitives.ReadUInt32LittleEndian(geometry), (int)blockSize,
            regions, Convert.ToHexString(geometry.Slice(cidOffset, 16)), (int)writeLength, (int)readLength);
    }
}