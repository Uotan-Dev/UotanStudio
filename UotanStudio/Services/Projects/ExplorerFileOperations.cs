namespace UotanStudio.Services.Projects;

/// <summary>Performs non-overwriting explorer transfers.<para>执行不覆盖已有内容的资源管理器传输。</para></summary>
internal static class ExplorerFileOperations
{
    internal static void Transfer(string source, string destinationDirectory, bool move)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(destinationDirectory)) throw new DirectoryNotFoundException(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The destination cannot be the source or a child of the source.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException($"Destination already exists: {destination}");
        var directory = Directory.Exists(source);
        if (!directory && !File.Exists(source)) throw new FileNotFoundException("Source not found.", source);
        if (move)
        {
            if (directory) Directory.Move(source, destination);
            else File.Move(source, destination);
            return;
        }

        var staging = Path.Combine(destinationDirectory, ".copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (directory)
            {
                CopyDirectory(source, staging);
                Directory.Move(staging, destination);
            }
            else
            {
                File.Copy(source, staging);
                File.Move(staging, destination);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            else if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Copying linked directories is unsupported: {source}");
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Copying linked entries is unsupported: {entry}");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target);
            else File.Copy(entry, target);
        }
    }
}