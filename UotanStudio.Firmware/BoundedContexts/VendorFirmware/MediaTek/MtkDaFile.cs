using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>Explicit region byte selection, without cryptographic signature validation.</summary>
public enum MtkDaRegionContent
{
    /// <summary>All declared bytes, including any signature suffix.</summary>
    Complete,
    /// <summary>Exclude exactly the signature suffix length recorded in the directory.</summary>
    WithoutSignatureSuffix
}

/// <summary>
/// Owns the file handle used for both metadata parsing and bounded region reads.
/// Region streams have independent cursors; the owner must outlive them.
/// </summary>
public sealed class MtkDaFile : IDisposable
{
    private readonly FileStream _source;
    private readonly object _gate = new();
    private bool _disposed;

    private MtkDaFile(FileStream source, MtkDaContainer metadata)
    {
        _source = source;
        Metadata = metadata;
    }

    /// <summary>Gets the validated directory from the retained file handle.</summary>
    public MtkDaContainer Metadata { get; }

    /// <summary>Opens and validates a container without reading its region payloads.</summary>
    /// <param name="path">The explicitly selected loader path.</param>
    /// <param name="layout">The expected layout, or unambiguous structural detection.</param>
    /// <param name="cancellationToken">The metadata parsing cancellation token.</param>
    /// <returns>An owner whose disposal closes the retained container handle.</returns>
    public static MtkDaFile Open(string path, MtkDaDirectoryLayout layout = MtkDaDirectoryLayout.Auto,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            return new MtkDaFile(source, MtkDaContainerReader.Read(source, layout, cancellationToken));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a bounded view of an explicitly selected region on the retained file.
    /// This does not validate its suitability for a device or interpret StartOffset.
    /// </summary>
    /// <param name="entryIndex">The zero-based directory entry index.</param>
    /// <param name="regionIndex">The zero-based region index within that entry.</param>
    /// <param name="content">Whether the declared signature suffix is included.</param>
    /// <returns>A caller-owned read-only stream; disposing it does not close this file owner.</returns>
    public Stream OpenRegion(int entryIndex, int regionIndex, MtkDaRegionContent content)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, Metadata.Entries.Count);
            var entry = Metadata.Entries[entryIndex];
            ArgumentOutOfRangeException.ThrowIfNegative(regionIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(regionIndex, entry.Regions.Count);
            if (!Enum.IsDefined(content))
            {
                throw new ArgumentOutOfRangeException(nameof(content));
            }
            var region = entry.Regions[regionIndex];
            var length = content == MtkDaRegionContent.Complete
                ? region.Length : region.Length - region.SignatureLength;
            var cursor = new IndependentReadStream(_source, _gate);
            try
            {
                return new BoundedReadStream(cursor, region.FileOffset, length);
            }
            catch
            {
                cursor.Dispose();
                throw;
            }
        }
    }

    /// <summary>Closes the retained handle; outstanding region streams must no longer be used.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _source.Dispose();
        }
    }
}