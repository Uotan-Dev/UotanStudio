using UotanStudio.Firmware.VendorFirmware.Nb0;
using UotanStudio.Firmware.VendorFirmware.NTPi;

namespace UotanStudio.Services.Flash;

/// <summary>Lists and selectively extracts NB0 and NTPI image entries.</summary>
public static class FlashVendorContainerService
{
    /// <summary>Reads image entries with unambiguous partition-like filenames.</summary>
    public static async Task<IReadOnlyList<(string Name, string Entry, long Size)>> ListAsync(
        string path, CancellationToken ct)
    {
        IEnumerable<(string Entry, long Size)> entries;
        if (Path.GetExtension(path).Equals(".nb0", StringComparison.OrdinalIgnoreCase))
        {
            var metadata = await Nb0Parser.ParseAsync(path, ct).ConfigureAwait(false);
            entries = [.. metadata.Entries.Select(entry => (entry.Name, entry.Size))];
        }
        else
        {
            entries = await Task.Run(() =>
            {
                using var reader = NtpiReader.CreateDefault();
                return reader.ReadInfo(path).FileEntries.Select(entry => (entry.Name, entry.OriginalLength)).ToArray();
            }, ct).ConfigureAwait(false);
        }

        var images = entries.Where(entry => entry.Entry.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
            entry.Size >= 0 && FlashManifestReader.IsPartitionName(Path.GetFileNameWithoutExtension(entry.Entry)) &&
            Path.GetFileName(entry.Entry) == entry.Entry)
            .Select(entry => (Name: Path.GetFileNameWithoutExtension(entry.Entry), entry.Entry, entry.Size)).ToArray();
        if (images.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Length)
        {
            throw new InvalidDataException("Container has duplicate partition image entries.");
        }
        return images;
    }

    /// <summary>Publishes selected original entries only when every requested image exists.</summary>
    public static async Task<string> ExtractAsync(string path, IReadOnlyList<string> entryNames, string parent, CancellationToken ct)
    {
        if (entryNames.Count == 0 || entryNames.Any(name => !name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(name) != name || !FlashManifestReader.IsPartitionName(Path.GetFileNameWithoutExtension(name))))
        {
            throw new ArgumentException("Select valid image entries.", nameof(entryNames));
        }
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, Path.GetFileName(path) + "-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        var selected = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Path.GetExtension(path).Equals(".nb0", StringComparison.OrdinalIgnoreCase))
            {
                var result = await new Nb0Extractor().ExtractAsync(path, staging,
                    new ExtractionOptions { Overwrite = false, FileFilter = selected.Contains },
                    cancellationToken: ct).ConfigureAwait(false);
                if (!result.Success) throw new InvalidDataException(string.Join("; ", result.Errors));
            }
            else
            {
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    using var reader = NtpiReader.CreateDefault();
                    reader.Unpack(path, staging, fileFilter: selected.Contains);
                }, ct).ConfigureAwait(false);
            }
            foreach (var name in selected)
            {
                if (!File.Exists(Path.Combine(staging, name)))
                {
                    throw new InvalidDataException($"Container did not produce {name}.");
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