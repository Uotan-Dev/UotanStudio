namespace UotanStudio.Services.Projects;

internal static class ProjectStreamCopy
{
    internal static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (length > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Project file changed while copying.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                length -= read;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}
