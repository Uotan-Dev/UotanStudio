namespace UotanStudio.Services.Flash;

/// <summary>A validated ordered flash plan and an optional required device product.</summary>
public sealed record FlashManifest(string? CodeName, IReadOnlyList<FlashStep> Steps, bool RequiresFastbootd = false)
{
    /// <summary>Image-writing steps for consumers that only need their source files.</summary>
    public IReadOnlyList<(string Partition, string Path, long Length)> Images =>
        Steps.Where(step => step.Command == "flash")
            .Select(step => (step.Partition, step.ImagePath, step.ImageLength)).ToArray();
}

/// <summary>Parses the legacy fastboot TXT image list without executing its contents as commands.</summary>
public static class FlashManifestReader
{
    private const int MaxLines = 4096;
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>Reads an explicit list; unsupported commands cause the whole list to fail.</summary>
    public static FlashManifest Read(string manifestPath)
    {
        var file = new FileInfo(manifestPath);
        if (!file.Exists || file.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("Flash manifest is missing or too large.");
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var entries = new List<FlashStep>();
        string? codeName = null;
        var requiresFastbootd = false;
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(manifestPath))
        {
            lineNumber++;
            if (lineNumber > MaxLines)
            {
                throw new InvalidDataException("Flash manifest has too many lines.");
            }

            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("codename:", StringComparison.OrdinalIgnoreCase) && entries.Count == 0 && codeName is null)
            {
                codeName = line["codename:".Length..].Trim();
                if (!IsPartitionName(codeName))
                {
                    throw new InvalidDataException($"Invalid device codename on line {lineNumber}.");
                }
                continue;
            }
            if (line.Equals("mode:fastbootd", StringComparison.OrdinalIgnoreCase) && entries.Count == 0)
            {
                requiresFastbootd = true;
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length is < 1 or > 2 || !IsPartitionName(tokens[0]))
            {
                throw new InvalidDataException($"Invalid flash entry on line {lineNumber}.");
            }

            var relative = tokens.Length == 1 ? Path.Combine("images", tokens[0] + ".img") : tokens[1];
            if (tokens.Length == 2 && (tokens[1] is "delete" or "create"))
            {
                if (tokens[0].Equals("super_empty", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Invalid super_empty operation on line {lineNumber}.");
                entries.Add(new FlashStep(tokens[1], tokens[0]));
                continue;
            }

            if (Path.IsPathRooted(relative) || relative.Contains(':') ||
                relative.Split(['/', '\\']).Any(segment => segment is "." or ".." or ""))
            {
                throw new InvalidDataException($"Unsupported flash step or path on line {lineNumber}.");
            }

            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsupported image path on line {lineNumber}.");
            }

            var image = new FileInfo(path);
            if (!image.Exists || image.Length == 0)
            {
                throw new InvalidDataException($"Missing or empty image on line {lineNumber}: {relative}");
            }

            var isSuperEmpty = tokens[0].Equals("super_empty", StringComparison.OrdinalIgnoreCase);
            if (isSuperEmpty)
            {
                if (!path.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Compressed super_empty is not supported.");
                _ = FlashSuperEmptyService.Prepare(path);
            }
            entries.Add(new FlashStep(isSuperEmpty ? "wipe_super" : "flash", tokens[0], path, image.Length));
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("Flash manifest contains no steps.");
        }

        return new FlashManifest(codeName, entries, requiresFastbootd);
    }

    internal static bool IsPartitionName(string name)
        => name.Length is > 0 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
