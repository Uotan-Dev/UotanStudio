using System.IO.Compression;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Services.Delivery;

namespace UotanStudio.Services.Projects;

/// <summary>Read-only peak-space estimates for the selected creation stages, without extracting images.</summary>
internal static class ProjectCreationSpaceEstimator
{
    internal const long MetadataReserve = 16 * 1024 * 1024;

    /// <summary>Estimates retained copies, selected expanded images and workspace for further unpacking.</summary>
    internal static CreationSpaceEstimate Estimate(IReadOnlyList<string> copyPaths,
        IReadOnlyList<ContainerEntry> extractedEntries, bool unpackCopiedImages, bool unpackFully,
        string? payloadZipPath = null)
    {
        long target = MetadataReserve, temporary = 0;
        var approximate = false;
        foreach (var path in copyPaths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            try
            {
                var size = new FileInfo(path).Length;
                target = Add(target, size);
                if (!unpackCopiedImages) continue;
                var expanded = size;
                if (size >= 28)
                {
                    var header = SparseReader.PeekHeader(path);
                    if (header.IsValid()) expanded = Math.Max(size, Multiply(header.TotalBlocks, header.BlockSize));
                }
                // Retain the copied image and reserve unpacked output plus intermediate workspace.
                target = Add(target, Multiply(expanded, unpackFully ? 3 : 2));
                approximate = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                approximate = true;
            }
        }
        foreach (var entry in extractedEntries)
        {
            var size = Math.Max(0, entry.SizeBytes);
            if (size == 0) approximate = true;
            target = Add(target, Multiply(size, unpackFully ? 3 : 1));
        }
        approximate |= unpackFully;
        if (payloadZipPath is not null)
        {
            try
            {
                using var zip = ZipFile.OpenRead(payloadZipPath);
                var member = zip.Entries.FirstOrDefault(e =>
                    Path.GetFileName(e.FullName).Equals("payload.bin", StringComparison.OrdinalIgnoreCase));
                if (member is not null) temporary = member.Length;
                else approximate = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                approximate = true;
            }
        }
        return new(target, temporary, approximate);
    }

    /// <summary>Combines target and temporary requirements when they share a volume.</summary>
    internal static IReadOnlyList<CreationSpaceShortage> FindShortages(string targetDirectory,
        CreationSpaceEstimate estimate, Func<string, long>? availableBytes = null,
        Func<string, string?>? volumePath = null)
    {
        availableBytes ??= DiskSpaceEstimator.GetFreeBytes;
        volumePath ??= DiskSpaceEstimator.GetVolumePath;
        var demands = new Dictionary<string, (string Path, long Required)>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        AddDemand(targetDirectory, estimate.TargetBytes);
        if (estimate.TemporaryBytes > 0) AddDemand(Path.GetTempPath(), estimate.TemporaryBytes);
        var shortages = new List<CreationSpaceShortage>();
        foreach (var (_, (path, required)) in demands)
        {
            var free = availableBytes(path);
            if (free >= 0 && free < required) shortages.Add(new(path, required, free));
        }
        return shortages;

        void AddDemand(string path, long required)
        {
            var volume = volumePath(path) ?? Path.GetFullPath(path);
            demands[volume] = demands.TryGetValue(volume, out var existing)
                ? (existing.Path, Add(existing.Required, required)) : (path, required);
        }
    }

    internal static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
    internal static long Multiply(long size, long count) => count > 0 && size > long.MaxValue / count ? long.MaxValue : size * count;
}

/// <summary>Estimated peak demand; recursive unpacking is a heuristic rather than a size guarantee.</summary>
internal sealed record CreationSpaceEstimate(long TargetBytes, long TemporaryBytes, bool IsApproximate);

/// <summary>A destination with less available space than the estimated demand.</summary>
internal sealed record CreationSpaceShortage(string Directory, long RequiredBytes, long AvailableBytes);