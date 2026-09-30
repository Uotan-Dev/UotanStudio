namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>Resolves untrusted image members inside an explicitly selected firmware directory.</summary>
internal static class FlashBundlePath
{
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

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

            if (segment.Any(static value => value < ' ') || segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new EdlException(EdlErrorCode.InvalidParameter, "An image member contains a name that is not portable across hosts.");
            }

            if (Array.Exists(ReservedDeviceNames, name => string.Equals(segment.Split('.')[0], name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new EdlException(EdlErrorCode.InvalidParameter, "An image member uses a reserved device name.");
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