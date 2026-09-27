using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;

/// <summary>Owns a raw or bounded Android Sparse logical image and its backing file.
/// <para>持有原始或有界 Android Sparse 逻辑镜像及其后备文件。</para></summary>
internal sealed class AvbImageSource(FileStream file, Stream logical) : IAsyncDisposable
{
    internal Stream Stream { get; } = logical;

    internal static async Task<AvbImageSource> OpenAsync(string path, CancellationToken token)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            var magic = new byte[4];
            if (file.Length >= magic.Length)
            {
                await file.ReadExactlyAsync(magic, token).ConfigureAwait(false);
                file.Position = 0;
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(magic) != SparseFormat.SparseHeaderMagic)
            {
                return new AvbImageSource(file, file);
            }
            var sparse = SparseReader.FromStream(file, validateCrc: true, verbose: false, maximumChunks: 262144,
                cancellationToken: token, maximumExpandedLength: 1L << 40);
            return new AvbImageSource(file, new SparseReadStream(sparse, requireExactRawData: true));
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!ReferenceEquals(Stream, file))
            {
                await Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await file.DisposeAsync().ConfigureAwait(false);
        }
    }
}