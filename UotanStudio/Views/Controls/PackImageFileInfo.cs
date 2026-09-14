namespace UotanStudio.Views.Controls;

internal sealed record PackImageFileInfo(string FilePath, long? SizeBytes)
{
    internal static PackImageFileInfo Read(string path)
    {
        long? size = null;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new PackImageFileInfo(path, size);
    }

    internal static string FormatSize(decimal bytes)
    {
        ReadOnlySpan<string> units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return $"{bytes:0.##} {units[unit]}";
    }
}