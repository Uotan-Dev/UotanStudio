using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Services.Patching;

/// <summary>Bounded offline configuration I/O. Image streams must use the image adapter instead.</summary>
public static class PatchFileService
{
    private const int MaxBytes = TextPatch.MaxCharacters * 4;

    /// <summary>Finds files below a root without following links. Patterns are relative and case sensitive.</summary>
    public static IReadOnlyList<string> Find(string root, string pattern, int expectedMatches, CancellationToken token)
    {
        if (expectedMatches is < 0 or > 100_000)
        {
            throw new InvalidDataException("Invalid expected match count.");
        }
        var result = Discover(root, pattern, false, token);
        if (result.Count != expectedMatches)
        {
            throw new InvalidDataException($"Expected {expectedMatches} files; found {result.Count}.");
        }
        return result;
    }

    internal static IReadOnlyList<string> Discover(string root, string pattern, bool allowMissingRoot, CancellationToken token)
    {
        ValidatePattern(pattern);
        token.ThrowIfCancellationRequested();
        root = Path.GetFullPath(root);
        RejectLinks(root);
        if (!Directory.Exists(root))
        {
            if (allowMissingRoot && !File.Exists(root))
            {
                return [];
            }
            throw new DirectoryNotFoundException(root);
        }
        pattern = pattern.Replace('\\', '/');
        var pending = new Stack<string>();
        pending.Push(root);
        var result = new List<string>();
        var visited = 0;
        var pathCharacters = 0L;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                pathCharacters += entry.Length;
                if (++visited > 100_000 || pathCharacters > 16 * 1024 * 1024)
                {
                    throw new InvalidDataException("Search exceeds the entry or total path-length limit.");
                }
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (Matches(root, pattern, entry))
                {
                    result.Add(entry);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    internal static void ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)
            || pattern.Length > 4096 || Path.IsPathRooted(pattern)
            || pattern.Split('/', '\\').Any(part => part == ".."))
        {
            throw new InvalidDataException("Invalid relative search pattern or match count.");
        }
    }

    internal static bool Matches(string root, string pattern, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative != "." && relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
            && FileSystemName.MatchesSimpleExpression(pattern.Replace('\\', '/'), relative, false);
    }

    /// <summary>Reads strictly decoded UTF-8/UTF-16 with a fixed byte and character limit.</summary>
    public static async Task<PatchTextDocument> ReadAsync(string path, CancellationToken token)
    {
        RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, true);
        if (stream.Length > MaxBytes)
        {
            throw new InvalidDataException("Configuration exceeds the byte limit.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return Decode(bytes);
    }

    internal static PatchTextDocument Decode(ReadOnlySpan<byte> bytes)
    {
        var encoding = "utf8";
        var skip = 0;
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            encoding = "utf8-bom";
            skip = 3;
        }
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = "utf16-le";
            skip = 2;
        }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = "utf16-be";
            skip = 2;
        }
        var text = GetEncoding(encoding).GetString(bytes[skip..]);
        if (text.Length > TextPatch.MaxCharacters || text.Contains('\0'))
        {
            throw new InvalidDataException("Input is not a supported bounded text configuration.");
        }
        return new PatchTextDocument(text, encoding, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>Publishes to a new output file using a sibling temporary file; existing files are preserved.</summary>
    public static async Task WriteNewAsync(string path, string text, string encoding, CancellationToken token)
    {
        if (text.Length > TextPatch.MaxCharacters || text.Contains('\0'))
        {
            throw new InvalidDataException("Invalid configuration text.");
        }
        path = Path.GetFullPath(path);
        RejectLinks(path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Output already exists; choose a new path.");
        }
        var codec = GetEncoding(encoding);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, true))
            await using (var writer = new StreamWriter(output, codec))
            {
                await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static Encoding GetEncoding(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8-bom" => new UTF8Encoding(true, true),
        "utf16-le" => new UnicodeEncoding(false, true, true),
        "utf16-be" => new UnicodeEncoding(true, true, true),
        _ => throw new InvalidDataException("Unsupported text encoding.")
    };

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Patch paths must not traverse symbolic links or junctions.");
            }
        }
    }
}

/// <summary>Text together with its physical encoding and original file identity.</summary>
public sealed record PatchTextDocument(string Text, string Encoding, string Sha256);