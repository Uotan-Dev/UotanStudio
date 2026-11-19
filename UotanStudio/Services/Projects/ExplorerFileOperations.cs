namespace UotanStudio.Services.Projects;

/// <summary>Performs non-overwriting explorer transfers.<para>执行不覆盖已有内容的资源管理器传输。</para></summary>
internal static class ExplorerFileOperations
{
    private const int CopyBufferSize = 256 * 1024;

    internal static long MeasureBytes(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            total += MeasurePathBytes(path);
        }

        return total;
    }

    private static long MeasurePathBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (Directory.Exists(path))
            {
                long total = 0;
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                    }
                }

                return total;
            }
        }
        catch (IOException)
        {
        }

        return 0;
    }

    internal static Task TransferAsync(
        string source,
        string destinationDirectory,
        bool move,
        Action<long>? onBytesCopied = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Transfer(source, destinationDirectory, move, onBytesCopied, cancellationToken), cancellationToken);

    internal static void Transfer(string source, string destinationDirectory, bool move)
        => Transfer(source, destinationDirectory, move, onBytesCopied: null, CancellationToken.None);

    internal static void Transfer(
        string source,
        string destinationDirectory,
        bool move,
        Action<long>? onBytesCopied,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            var movedBytes = onBytesCopied is not null ? MeasurePathBytes(source) : 0;
            if (directory) Directory.Move(source, destination);
            else File.Move(source, destination);
            if (movedBytes > 0) onBytesCopied?.Invoke(movedBytes);
            return;
        }

        var staging = Path.Combine(destinationDirectory, ".copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (directory)
            {
                CopyDirectory(source, staging, onBytesCopied, cancellationToken);
                Directory.Move(staging, destination);
            }
            else
            {
                CopyFile(source, staging, onBytesCopied, cancellationToken);
                File.Move(staging, destination);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            else if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static void CopyFile(string source, string destination, Action<long>? onBytesCopied, CancellationToken cancellationToken)
    {
        if (onBytesCopied is null)
        {
            File.Copy(source, destination);
            return;
        }

        var buffer = new byte[CopyBufferSize];
        using var input = File.OpenRead(source);
        using var output = File.Create(destination);
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            onBytesCopied(read);
        }
    }

    private static void CopyDirectory(string source, string destination, Action<long>? onBytesCopied, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Copying linked directories is unsupported: {source}");
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Copying linked entries is unsupported: {entry}");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target, onBytesCopied, cancellationToken);
            else CopyFile(entry, target, onBytesCopied, cancellationToken);
        }
    }
}