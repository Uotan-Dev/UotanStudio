using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Services.Firmware;

namespace UotanStudio.Services.Flash;

/// <summary>Reads and stages selected partitions from an HTTP range-backed raw Payload.</summary>
public sealed class FlashRemotePayloadService(HttpClient client)
{
    private readonly RemoteRomService _remote = new(client);

    /// <summary>Rejects non-Payload URLs before offering any partitions for extraction.</summary>
    public async Task<IReadOnlyList<(string Name, ulong Size)>> ListAsync(string address, CancellationToken ct)
    {
        var analysis = await _remote.AnalyzeAsync(address, ct).ConfigureAwait(false);
        if (analysis.Kind != ContainerKind.Payload)
            throw new InvalidDataException("The URL does not contain a raw Payload.");
        if (analysis.Entries.Any(entry => !FlashManifestReader.IsPartitionName(entry.Name) || entry.SizeBytes < 0) ||
            analysis.Entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != analysis.Entries.Count)
            throw new InvalidDataException("The remote Payload contains invalid or duplicate partition names.");
        return [.. analysis.Entries.Select(entry => (entry.Name, checked((ulong)entry.SizeBytes)))];
    }

    /// <summary>Publishes a new directory only when all requested images have been extracted.</summary>
    public async Task<string> ExtractAsync(string address, IReadOnlyList<string> partitions, string parent, CancellationToken ct)
    {
        var available = await ListAsync(address, ct).ConfigureAwait(false);
        if (partitions.Count == 0 || partitions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != partitions.Count ||
            partitions.Any(name => !available.Any(entry => entry.Name.Equals(name, StringComparison.Ordinal))))
            throw new ArgumentException("Select distinct partitions from the remote Payload.", nameof(partitions));
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, "payload-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            Directory.CreateDirectory(staging);
            await _remote.ExtractPayloadPartitionsAsync(new Uri(address), partitions, staging,
                cancellationToken: ct).ConfigureAwait(false);
            foreach (var name in partitions)
            {
                if (!File.Exists(Path.Combine(staging, name + ".img")))
                    throw new InvalidDataException($"Partition extraction did not produce {name}.img.");
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