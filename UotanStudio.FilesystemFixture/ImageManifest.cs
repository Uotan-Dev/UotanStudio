using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Hash.Digests;

namespace UotanStudio.FilesystemFixture;

internal sealed record ManifestEntry(
    string Path,
    string Type,
    ushort Mode,
    uint Uid,
    uint Gid,
    long Size,
    string Sha256,
    string? LinkTarget,
    string? HardlinkOf,
    IReadOnlyDictionary<string, string> Xattrs,
    ulong Inode)
{
    public string ToManifestText()
    {
        return string.Join('|',
            Type,
            Escape(Path),
            Convert.ToString(Mode & 0xFFF, 8),
            Uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Gid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sha256,
            Escape(LinkTarget ?? string.Empty),
            Escape(HardlinkOf ?? string.Empty),
            string.Join(';', Xattrs.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value)));
    }

    private static string Escape(string value)
    {
        if (value.Contains('|') || value.Contains('\n') || value.Contains('\r'))
        {
            throw new InvalidDataException("Manifest paths and link targets must not contain pipes or newlines.");
        }

        return value;
    }
}

internal sealed class ImageManifest
{
    private ImageManifest(string format, IReadOnlyList<ManifestEntry> entries)
    {
        Format = format;
        Entries = entries;
    }

    public string Format { get; }

    public IReadOnlyList<ManifestEntry> Entries { get; }

    public static ImageManifest Read(string imagePath, string format, CancellationToken cancellationToken)
    {
        var entries = format switch
        {
            "EXT4" => ReadExt4(imagePath, cancellationToken),
            "EROFS" => ReadErofs(imagePath, cancellationToken),
            "F2FS" => ReadF2fs(imagePath, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported filesystem format.")
        };
        return new ImageManifest(format, AssignHardlinks(entries));
    }

    public string ToManifestText() => string.Join('\n', Entries.Select(entry => entry.ToManifestText())) + "\n";

    public static IReadOnlyList<string> Compare(ImageManifest expected, ImageManifest actual)
    {
        var differences = new List<string>();
        var expectedByPath = expected.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var actualByPath = actual.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        foreach (var path in expectedByPath.Keys.Union(actualByPath.Keys, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!expectedByPath.TryGetValue(path, out var left))
            {
                differences.Add($"unexpected:{path}");
                continue;
            }

            if (!actualByPath.TryGetValue(path, out var right))
            {
                differences.Add($"missing:{path}");
                continue;
            }

            if (left.Type != right.Type
                || left.Mode != right.Mode
                || left.Uid != right.Uid
                || left.Gid != right.Gid
                || left.Size != right.Size
                || left.Sha256 != right.Sha256
                || left.LinkTarget != right.LinkTarget
                || left.HardlinkOf != right.HardlinkOf
                || left.Xattrs.Count != right.Xattrs.Count
                || left.Xattrs.Any(pair => !right.Xattrs.TryGetValue(pair.Key, out var value) || value != pair.Value))
            {
                differences.Add($"changed:{path}");
            }
        }

        return differences;
    }

    private static List<ManifestEntry> ReadExt4(string imagePath, CancellationToken cancellationToken)
    {
        using var parser = new Ext4Parser(imagePath);
        var result = new List<ManifestEntry>();
        foreach (var entry in parser.ListFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Path is "." or "")
            {
                continue;
            }

            var type = entry.Kind switch
            {
                Ext4Parser.Ext4EntryKind.Directory => "d",
                Ext4Parser.Ext4EntryKind.Symlink => "l",
                _ => "f"
            };
            var content = type == "f" ? Read(parser, entry.Path, cancellationToken) : [];
            result.Add(new ManifestEntry(
                entry.Path, type, entry.Mode, entry.Uid, entry.Gid,
                type == "f" ? entry.Size : 0,
                type == "f" ? Hash(content) : string.Empty,
                entry.SymlinkTarget,
                null,
                Normalize(entry.Xattrs),
                entry.Inode));
        }

        return result;
    }

    private static List<ManifestEntry> ReadErofs(string imagePath, CancellationToken cancellationToken)
    {
        using var parser = new ErofsParser(imagePath);
        var result = new List<ManifestEntry>();
        foreach (var entry in parser.EnumerateEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Path is "." or "")
            {
                continue;
            }

            var type = entry.IsDirectory ? "d" : entry.IsSymlink ? "l" : "f";
            var content = type == "f" ? parser.ReadFileBytes(entry.Path) : [];
            result.Add(new ManifestEntry(
                entry.Path, type, entry.Mode, entry.Uid, entry.Gid,
                type == "f" ? entry.Size : 0,
                type == "f" ? Hash(content) : string.Empty,
                entry.SymlinkTarget,
                null,
                Normalize(entry.Xattrs),
                entry.Nid));
        }

        return result;
    }

    private static List<ManifestEntry> ReadF2fs(string imagePath, CancellationToken cancellationToken)
    {
        using var parser = new F2fsParser(imagePath);
        var result = new List<ManifestEntry>();
        foreach (var entry in parser.ListFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Path is "." or "")
            {
                continue;
            }

            var type = entry.Kind switch
            {
                F2fsParser.F2fsEntryKind.Directory => "d",
                F2fsParser.F2fsEntryKind.Symlink => "l",
                _ => "f"
            };
            var content = type == "f" ? Read(parser, entry.Path, cancellationToken) : [];
            result.Add(new ManifestEntry(
                entry.Path, type, entry.Mode, entry.Uid, entry.Gid,
                type == "f" ? entry.Size : 0,
                type == "f" ? Hash(content) : string.Empty,
                entry.SymlinkTarget,
                null,
                Normalize(entry.Xattrs),
                entry.Inode));
        }

        return result;
    }

    private static byte[] Read(Ext4Parser parser, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        parser.ReadFile(path, output);
        return output.ToArray();
    }

    private static byte[] Read(F2fsParser parser, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        parser.ReadFile(path, output);
        return output.ToArray();
    }

    private static IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, byte[]>? xattrs)
    {
        return xattrs is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : xattrs.Where(pair => pair.Key != "system.data")
                .ToDictionary(pair => pair.Key, pair => Convert.ToHexStringLower(pair.Value), StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> Normalize(IReadOnlyList<ErofsXattrEntry>? xattrs)
    {
        return xattrs is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : xattrs.Where(entry => entry.Name != "system.data")
                .ToDictionary(entry => entry.Name, entry => Convert.ToHexStringLower(entry.Value), StringComparer.Ordinal);
    }

    private static IReadOnlyList<ManifestEntry> AssignHardlinks(List<ManifestEntry> entries)
    {
        var groups = entries.Where(entry => entry.Type == "f" && entry.Inode != 0)
            .GroupBy(entry => entry.Inode)
            .Where(group => group.Count() > 1);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var canonical = group.Select(entry => entry.Path).OrderBy(path => path, StringComparer.Ordinal).First();
            foreach (var entry in group)
            {
                if (!string.Equals(entry.Path, canonical, StringComparison.Ordinal))
                {
                    aliases[entry.Path] = canonical;
                }
            }
        }

        return entries.Select(entry => aliases.TryGetValue(entry.Path, out var canonical)
            ? entry with { HardlinkOf = canonical }
            : entry).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    private static string Hash(byte[] content) => Convert.ToHexStringLower(Sha256.ComputeHash(content));
}