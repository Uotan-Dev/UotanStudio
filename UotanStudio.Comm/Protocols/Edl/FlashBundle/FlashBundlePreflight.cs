using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage.Sparse;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>Checks all selected source ranges and commands before starting destructive phases.</summary>
internal static class FlashBundlePreflight
{
    internal static long SelectSectorSize(IReadOnlyList<RawProgramEntry> programs, IReadOnlyList<PatchEntry>? patches,
        FlashBundleOptions options, long configuredSize)
    {
        if (options.OverrideSectorSize.HasValue)
        {
            ValidateSectorSize(options.OverrideSectorSize.Value);
            return options.OverrideSectorSize.Value;
        }

        var selected = configuredSize;
        foreach (var declared in DeclaredSectorSizes(programs, patches, options))
        {
            ArgumentOutOfRangeException.ThrowIfNegative(declared);
            if (declared == 0)
            {
                continue;
            }

            ValidateSectorSize(declared);
            if (selected != 0 && selected != declared)
            {
                throw new InvalidDataException("Manifest sector sizes disagree with each other or the configured sector size.");
            }

            selected = declared;
        }

        if (selected != 0)
        {
            ValidateSectorSize(selected);
        }

        return selected;
    }

    internal static List<EdlPhysicalRange> Validate(IReadOnlyList<RawProgramEntry> programs, IReadOnlyList<PatchEntry>? patches,
        FlashBundleOptions options, long sectorSize, CancellationToken cancellationToken, FlashBundleSources? sources = null,
        int? storageSlot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSectorSize(sectorSize);
        var ranges = new List<EdlPhysicalRange>();
        if (!options.SkipProgram)
        {
            foreach (var entry in programs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sectors = ValidateProgram(entry, sectorSize, cancellationToken, sources);
                if (sectors > 0)
                {
                    ranges.Add(EdlPhysicalRange.Parse(entry.StartSector, sectors, entry.PhysicalPartitionNumber,
                        sectorSize, entry.Slot ?? storageSlot));
                }
            }
        }

        var touchedPartitions = ranges.Select(range => range.PhysicalPartitionNumber).ToHashSet();
        if (!options.SkipPatch && patches is not null)
        {
            foreach (var entry in patches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FirehoseProtocol.ValidatePatch(sectorSize, entry.Filename, entry.PhysicalPartitionNumber,
                    entry.StartSector, entry.ByteOffset, entry.Value, entry.SizeInBytes);
                ranges.Add(EdlPhysicalRange.Parse(entry.StartSector, 1, entry.PhysicalPartitionNumber,
                    sectorSize, entry.Slot ?? storageSlot));
            }
        }

        if (!options.SkipWipe)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(options.WipeFirstSectors);
            _ = checked(options.WipeFirstSectors * sectorSize);
            foreach (var partition in options.WipePhysicalPartitions ?? [])
            {
                ArgumentOutOfRangeException.ThrowIfNegative(partition);
            }
            if (options.WipeFirstSectors > 0 && touchedPartitions.Count > 0)
            {
                var targets = options.WipePhysicalPartitions is { Length: > 0 }
                    ? options.WipePhysicalPartitions.Distinct()
                    : touchedPartitions;
                foreach (var partition in targets)
                {
                    ranges.Add(new EdlPhysicalRange(0, options.WipeFirstSectors, partition, sectorSize,
                        StorageSlot: storageSlot, StartSectorExpression: "0"));
                }
            }
        }
        return ranges;
    }

    /// <summary>Checks device ranges while the caller owns the Firehose operation lease.</summary>
    internal static async Task ValidateDeviceRangesOwnedAsync(FirehoseProtocol protocol,
        IReadOnlyList<EdlPhysicalRange> ranges, CancellationToken cancellationToken)
    {
        foreach (var region in ranges.GroupBy(range => range.PhysicalPartitionNumber).OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = await protocol.GetStorageGeometryOwnedAsync(region.Key, cancellationToken).ConfigureAwait(false);
            foreach (var range in region)
            {
                cancellationToken.ThrowIfCancellationRequested();
                range.ValidateAgainst(geometry);
            }
        }
    }

    private static long ValidateProgram(RawProgramEntry entry, long sectorSize, CancellationToken cancellationToken,
        FlashBundleSources? sources)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entry.PhysicalPartitionNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.NumPartitionSectors);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.FileSectorOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.UnsparseFileSize);
        var address = FirehoseSectorAddress.Parse(entry.StartSector);
        if (string.IsNullOrEmpty(entry.Filename))
        {
            if (entry.NumPartitionSectors > 0)
            {
                address.ValidateRange(entry.NumPartitionSectors);
                _ = checked(entry.NumPartitionSectors * sectorSize);
            }
            return entry.NumPartitionSectors;
        }

        using var owned = sources is null
            ? new FileStream(entry.SourceFile, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var source = sources?.Get(entry) ?? owned!;
        Span<byte> magic = stackalloc byte[4];
        var read = source.ReadAtLeast(magic, 4, throwOnEndOfStream: false);
        source.Position = 0;
        if (entry.IsSparse || (read == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseFormat.SparseHeaderMagic))
        {
            if (entry.FileSectorOffset != 0)
            {
                throw new InvalidDataException("Sparse program entries cannot use a raw source-sector offset.");
            }

            using var image = FirehoseSparseLayout.Read(source, sectorSize, address,
                entry.NumPartitionSectors > 0 ? entry.NumPartitionSectors : null, cancellationToken);
            var logicalLength = checked((long)image.Header.TotalBlocks * image.Header.BlockSize);
            if (entry.UnsparseFileSize != 0 && entry.UnsparseFileSize != logicalLength)
            {
                throw new InvalidDataException("Declared unsparse size does not match the image's logical length.");
            }
            return logicalLength / sectorSize;
        }

        var offset = checked(entry.FileSectorOffset * sectorSize);
        if (offset >= source.Length)
        {
            throw new InvalidDataException("The source-sector offset is outside the image or leaves no data.");
        }

        var available = source.Length - offset;
        var sectors = FlashProgramLength.ResolveSectors(available, entry.NumPartitionSectors, sectorSize);
        if (sectors == 0)
        {
            throw new InvalidDataException("The source holds no whole or partial sector to program.");
        }

        address.ValidateRange(sectors);
        return sectors;
    }

    private static IEnumerable<long> DeclaredSectorSizes(IReadOnlyList<RawProgramEntry> programs,
        IReadOnlyList<PatchEntry>? patches, FlashBundleOptions options)
    {
        if (!options.SkipProgram)
        {
            foreach (var entry in programs)
            {
                yield return entry.SectorSizeInBytes;
            }
        }
        if (!options.SkipPatch && patches is not null)
        {
            foreach (var entry in patches)
            {
                yield return entry.SectorSizeInBytes;
            }
        }
    }

    private static void ValidateSectorSize(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 65536);
    }
}