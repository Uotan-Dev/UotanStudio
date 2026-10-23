using UotanStudio.Compress;
using UotanStudio.Firmware.Archive.Models;

namespace UotanStudio.Firmware.Archive;

public sealed partial class CpioFormatHandler
{
    private const int MaximumRememberedArchives = 4;
    private static readonly Lock SnapshotGate = new();
    private static readonly List<RememberedArchive> RememberedArchives = [];

    private sealed record ArchiveSnapshot(ArchiveFormat Format, CompressionFormat Compression,
        IReadOnlyList<ArchiveEntrySummary> Entries);

    private sealed record RememberedArchive(string Path, long Length, long WriteTimeUtcTicks, ArchiveSnapshot Snapshot);

    private static ArchiveSnapshot? TryGetArchiveSnapshot(string sourcePath)
    {
        string fullPath;
        long length;
        long writeTimeUtcTicks;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return null;
            }
            length = info.Length;
            writeTimeUtcTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return null;
        }

        lock (SnapshotGate)
        {
            foreach (var remembered in RememberedArchives)
            {
                if (remembered.Length == length && remembered.WriteTimeUtcTicks == writeTimeUtcTicks
                    && PathsEqual(remembered.Path, fullPath))
                {
                    return remembered.Snapshot;
                }
            }
        }
        return null;
    }

    private static void RememberArchiveSnapshot(string sourcePath, ArchiveFormat format,
        CompressionFormat compression, IReadOnlyList<ArchiveEntrySummary> entries)
    {
        string fullPath;
        long length;
        long writeTimeUtcTicks;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return;
            }
            length = info.Length;
            writeTimeUtcTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return;
        }

        lock (SnapshotGate)
        {
            for (var index = RememberedArchives.Count - 1; index >= 0; index--)
            {
                if (PathsEqual(RememberedArchives[index].Path, fullPath))
                {
                    RememberedArchives.RemoveAt(index);
                }
            }
            RememberedArchives.Insert(0,
                new RememberedArchive(fullPath, length, writeTimeUtcTicks,
                    new ArchiveSnapshot(format, compression, entries)));
            if (RememberedArchives.Count > MaximumRememberedArchives)
            {
                RememberedArchives.RemoveRange(MaximumRememberedArchives,
                    RememberedArchives.Count - MaximumRememberedArchives);
            }
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}