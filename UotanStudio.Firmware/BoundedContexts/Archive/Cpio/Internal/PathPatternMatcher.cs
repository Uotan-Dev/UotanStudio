namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Internal;

internal static class PathPatternMatcher
{
    public static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('/');
    }

    public static bool MatchesAny(string path, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        var normalizedPath = NormalizeArchivePath(path);
        foreach (var pattern in patterns)
        {
            if (IsMatch(normalizedPath, NormalizeArchivePath(pattern)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMatch(string text, string pattern)
    {
        var textIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var textCheckpoint = -1;

        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?' || pattern[patternIndex] == text[textIndex]))
            {
                textIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                textCheckpoint = textIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                textIndex = ++textCheckpoint;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
