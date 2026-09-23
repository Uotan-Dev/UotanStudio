namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Supplies caller-authorized resources for the explicit boot byte-stream profile.</summary>
public interface IMtkBootResourceProvider
{
    /// <summary>Opens a new caller-owned resource for a verified loader/device identity match.</summary>
    /// <param name="device">Identity queried from the selected target; not proof of cryptographic identity.</param>
    /// <param name="kind">The resource explicitly selected by the caller, in caller-selected order.</param>
    /// <param name="cancellationToken">Cancellation for external resource acquisition.</param>
    /// <returns>A new owned resource; bootstrap disposes it after upload or any later failure.</returns>
    Task<MtkBootResource> OpenAsync(MtkDeviceInfo device, MtkBootResourceKind kind,
        CancellationToken cancellationToken);
}

/// <summary>One owned resource stream and an explicit selected byte count; no resource bytes are copied.</summary>
public sealed class MtkBootResource : IDisposable
{
    private Stream? _owned;

    /// <summary>Takes ownership of a non-null stream; bootstrap validates its declared selection before upload.</summary>
    /// <param name="stream">New readable stream, exclusively owned by this lease.</param>
    /// <param name="length">Selected byte count, validated before any resource upload.</param>
    public MtkBootResource(Stream stream, int length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _owned = stream;
        Length = length;
    }

    /// <summary>Gets the owned stream, or throws after disposal.</summary>
    public Stream Stream => _owned ?? throw new ObjectDisposedException(nameof(MtkBootResource));

    /// <summary>Gets the selected byte count; bytes after this selection are not sent.</summary>
    public int Length { get; }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _owned, null)?.Dispose();
}