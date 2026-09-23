namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>Resolves untrusted image members inside an explicitly selected firmware directory.</summary>
internal static class FlashBundlePath
{
    internal static string ResolveImage(string directory, string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        var normalized = filename.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "An image member must be relative to its firmware directory.");
        }

        var root = Path.GetFullPath(directory);
        var segments = normalized.Split('/');
        var current = root;
        foreach (var segment in segments)
        {
            if (segment is "" or "." or "..")
            {
                throw new EdlException(EdlErrorCode.InvalidParameter, "An image member contains an invalid path component.");
            }

            current = Path.Combine(current, segment);
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new EdlException(EdlErrorCode.InvalidParameter, "Image members must not traverse symbolic links.");
            }
        }

        return current;
    }
}