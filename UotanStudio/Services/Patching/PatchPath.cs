namespace UotanStudio.Services.Patching;

/// <summary>Path confinement shared by patch workspaces and publication.</summary>
internal static class PatchPath
{
    internal static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Resolve(string root, string relative)
    {
        var segments = ValidateRelative(relative);
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Patch target escapes its workspace.");
        }
        RejectLinks(full);
        return full;
    }

    /// <summary>Validates path syntax before layout mapping, without probing an unrelated host path.</summary>
    internal static string[] ValidateRelative(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        var segments = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || segments.Any(segment =>
            segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.AsSpan().ContainsAny(PathSearchValues.InvalidFileName)))
        {
            throw new InvalidDataException("A patch target must be a normalized relative path.");
        }
        foreach (var segment in segments)
        {
            var dot = segment.IndexOf('.');
            var stem = (dot < 0 ? segment : segment[..dot]).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9')
            {
                throw new InvalidDataException("A patch path cannot contain a reserved device name.");
            }
        }
        return segments;
    }

    internal static string NormalizeFile(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("Patch file has no filesystem root.");
        return Resolve(root, Path.GetRelativePath(root, full));
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("A patch file path must not traverse a link or junction.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}