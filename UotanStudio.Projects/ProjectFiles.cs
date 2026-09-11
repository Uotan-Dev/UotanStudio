namespace UotanStudio.Services.Projects;

/// <summary>Filesystem operations shared by project hosts; format detection and UI/history updates remain with the caller.</summary>
public static class ProjectFiles
{
    /// <summary>Finds a filename using the desktop's existing underscore suffix convention.</summary>
    public static string GetUniqueFileName(string directory, string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A single filename is required.", nameof(fileName));
        var candidate = fileName;
        var index = 1;
        while (File.Exists(Path.Combine(directory, candidate)) || Directory.Exists(Path.Combine(directory, candidate)))
            candidate = $"{Path.GetFileNameWithoutExtension(fileName)}_{index++}{Path.GetExtension(fileName)}";
        return candidate;
    }

    /// <summary>Copies through a temporary file; cancellation or failure leaves the destination untouched.</summary>
    public static async Task CopyAsync(string sourcePath, string destinationPath, bool overwrite = false,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var destination = Path.GetFullPath(destinationPath);
        var temporary = destination + ".copy-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var length = source.Length;
                var buffer = new byte[65536];
                long copied = 0;
                var lastReport = Environment.TickCount64;
                progress?.Report(0);
                while (copied < length)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Source was truncated during copy.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    if (Environment.TickCount64 - lastReport >= 100)
                    {
                        progress?.Report(copied * 100.0 / length);
                        lastReport = Environment.TickCount64;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
            progress?.Report(100);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>Moves a file without overwriting another identity. A completed move is the commit point.</summary>
    public static Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(sourcePath, destinationPath, overwrite: false);
        }, cancellationToken);
}