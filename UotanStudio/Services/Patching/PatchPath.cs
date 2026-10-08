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

    /// <summary>
    /// Rejects reparse points between the deepest already existing ancestor of the path and the path itself. The walk
    /// stops at that existing ancestor instead of continuing to the filesystem root, because ancestors above a
    /// workspace root are chosen by the user or the operating system and are not archive-controlled. macOS resolves
    /// <c>Path.GetTempPath()</c> through the <c>/var</c> to <c>/private/var</c> symbolic link, so an unbounded walk
    /// rejects every legitimate workspace.
    /// </summary>
    internal static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var boundary = DeepestExistingAncestor(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = full;
            current is not null && !string.Equals(current, boundary, comparison);
            current = Path.GetDirectoryName(current))
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
        try
        {
            if ((File.Exists(boundary) || Directory.Exists(boundary))
                && (File.GetAttributes(boundary) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A patch file path must not traverse a link or junction.");
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>
    /// Returns the deepest ancestor that already exists as a directory or reparse point. Descendants of a dangling
    /// symbolic link report no attributes at all, so the walk skips them and keeps descending until it reaches the
    /// link itself, which <see cref="RejectLinks"/> then rejects.
    /// </summary>
    private static string DeepestExistingAncestor(string full)
    {
        for (var current = Path.GetDirectoryName(full); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException) { return current; }
            catch (DirectoryNotFoundException) { return current; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || !Directory.Exists(current))
            {
                return current;
            }
        }
        return Path.GetPathRoot(full) ?? full;
    }
}