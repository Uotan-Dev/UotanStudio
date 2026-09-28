using UotanStudio.Firmware.BlockImage.Lp;

namespace UotanStudio.Services.Flash;

/// <summary>Reads and extracts selected partitions from raw or sparse Super images.</summary>
public static class FlashSuperService
{
    /// <summary>Returns partitions and their logical sizes from validated LP metadata.</summary>
    public static Task<IReadOnlyList<(string Name, long Size)>> ListAsync(string path, CancellationToken ct)
        => Task.Run<IReadOnlyList<(string Name, long Size)>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var metadata = MetadataReader.ReadFromImageFile(path);
            var entries = new List<(string Name, long Size)>();
            foreach (var partition in metadata.Partitions)
            {
                ct.ThrowIfCancellationRequested();
                var name = partition.NameString;
                if (!FlashManifestReader.IsPartitionName(name))
                {
                    throw new InvalidDataException("Super image has an unsafe partition name.");
                }

                long size = 0;
                foreach (var extent in metadata.Extents.GetRange(
                    checked((int)partition.FirstExtentIndex), checked((int)partition.NumExtents)))
                {
                    size = checked(size + checked((long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE));
                }
                entries.Add((name, size));
            }
            return entries;
        }, ct);

    /// <summary>Extracts only requested partitions and atomically publishes a new directory.</summary>
    public static async Task<string> ExtractAsync(string path, IReadOnlyList<string> names, string parent, CancellationToken ct)
    {
        if (names.Count == 0 || names.Any(name => !FlashManifestReader.IsPartitionName(name)))
        {
            throw new ArgumentException("Select valid partitions before extraction.", nameof(names));
        }
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, Path.GetFileName(path) + "-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            var result = await new LpFormatHandler().ExtractSelectedAsync(path, staging, names, ct).ConfigureAwait(false);
            if (!result.Success) throw new InvalidDataException(result.Summary);
            foreach (var name in names)
            {
                if (!File.Exists(Path.Combine(staging, name + ".img")))
                {
                    throw new InvalidDataException($"Partition extraction did not produce {name}.img.");
                }
            }
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, output);
            return output;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}