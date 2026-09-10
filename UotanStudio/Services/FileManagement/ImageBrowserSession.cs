namespace UotanStudio.Services.FileManagement;

/// <summary>
/// Owns a parsed image, its entry index and any additional source stream. Closing the session
/// prevents new operations; an already leased extraction finishes before resources are released.
/// </summary>
internal sealed partial class ImageBrowserSession : IDisposable
{
    private readonly IDisposable _parser;
    private readonly IDisposable? _ownedSource;
    private readonly IReadOnlyList<ImageBrowserEntry> _flatEntries;
    private readonly Lock _lifetime = new();
    private readonly Lock _extraction = new();
    private int _leases;
    private bool _closed;

    /// <summary>The detected format name.</summary>
    internal string FormatName { get; }
    /// <summary>Logical size for sparse images; source file size otherwise.</summary>
    internal long ImageSize { get; }
    /// <summary>Parsed entries; no display nodes or navigation state are retained.</summary>
    internal IReadOnlyList<ImageBrowserEntry> Entries => _flatEntries;

    internal ImageBrowserSession(string formatName, long imageSize, IDisposable parser,
        IReadOnlyList<ImageBrowserEntry> entries, IDisposable? ownedSource = null)
    {
        FormatName = formatName;
        ImageSize = imageSize;
        _parser = parser;
        _flatEntries = entries;
        _ownedSource = ownedSource;
    }

    /// <summary>Extracts a selection as one operation, serializing access to parser streams.</summary>
    internal void Extract(IReadOnlyList<string> paths, string targetDirectory)
    {
        using var lease = AcquireLease();
        lock (_extraction)
        {
            foreach (var path in paths) ExtractEntryCore(path, targetDirectory);
        }
    }

    /// <summary>Keeps resources alive until an accepted operation completes.</summary>
    internal IDisposable AcquireLease()
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _leases++;
            return new Lease(this);
        }
    }

    private void ReleaseLease()
    {
        bool release;
        lock (_lifetime) release = --_leases == 0 && _closed;
        if (release) ReleaseResources();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        bool release;
        lock (_lifetime)
        {
            if (_closed) return;
            _closed = true;
            release = _leases == 0;
        }
        if (release) ReleaseResources();
    }

    private void ReleaseResources()
    {
        try { _parser.Dispose(); }
        finally { _ownedSource?.Dispose(); }
    }

    private sealed class Lease(ImageBrowserSession owner) : IDisposable
    {
        private ImageBrowserSession? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
    }
}
