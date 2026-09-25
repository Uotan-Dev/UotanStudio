namespace UotanStudio.Comm.Protocols.Common;

/// <summary>Publishes a completed storage read through a unique staging file beside its destination.</summary>
internal static class StorageFilePublisher
{
    internal static async Task PublishAsync(string destinationPath, long length, Func<Stream, Task> read,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(destinationPath);
        var staging = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await read(output).ConfigureAwait(false);
                if (output.Length != length)
                {
                    throw new IOException("The staged storage dump has an unexpected length.");
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}