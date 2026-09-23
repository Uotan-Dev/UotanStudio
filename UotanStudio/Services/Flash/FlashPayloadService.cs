using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;

namespace UotanStudio.Services.Flash;

/// <summary>Lists and extracts images from a local Payload container.</summary>
public static class FlashPayloadService
{
    /// <summary>Reads partition metadata without materializing their image data.</summary>
    public static async Task<IReadOnlyList<(string Name, ulong Size)>> ListAsync(string path, CancellationToken ct)
    {
        var info = await PayloadParser.GetPartitionInfoAsync(path, ct).ConfigureAwait(false);
        return info.Where(partition => partition.Name.Length is > 0 and <= 128 &&
                                       partition.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            .Select(partition => (partition.Name, partition.SizeBytes)).ToArray();
    }

    /// <summary>Publishes selected images as a new directory only after extraction succeeds.</summary>
    public static async Task<string> ExtractAsync(string path, IReadOnlyList<string> partitions, string parent, CancellationToken ct)
    {
        if (partitions.Count == 0 || partitions.Any(name => name.Length is < 1 or > 128 ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException("Select valid partitions before extraction.", nameof(partitions));
        }

        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(parent);
        }

        var output = Path.Combine(parent, Path.GetFileName(path) + "-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            Directory.CreateDirectory(staging);
            await PayloadParser.ExtractAsync(path, [.. partitions], staging, ct).ConfigureAwait(false);
            foreach (var name in partitions)
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
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>Builds a publish-on-success Fastbootd package with one matching TXT plan.</summary>
    public static async Task<string> BuildPackageAsync(string path, IReadOnlyList<string> partitions, string parent, CancellationToken ct)
    {
        if (partitions.Count == 0 || partitions.Any(name => !FlashManifestReader.IsPartitionName(name)) ||
            partitions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != partitions.Count)
        {
            throw new ArgumentException("The package needs distinct, valid partition names.", nameof(partitions));
        }
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, Path.GetFileName(path) + "-fastbootd-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            var images = Path.Combine(staging, "images");
            Directory.CreateDirectory(images);
            await PayloadParser.ExtractAsync(path, [.. partitions], images, ct).ConfigureAwait(false);
            foreach (var name in partitions)
            {
                if (!File.Exists(Path.Combine(images, name + ".img")))
                {
                    throw new InvalidDataException($"Package is missing {name}.img.");
                }
            }
            await File.WriteAllLinesAsync(Path.Combine(staging, "flash_fastbootd.txt"),
                ["mode:fastbootd", .. partitions], ct).ConfigureAwait(false);
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
