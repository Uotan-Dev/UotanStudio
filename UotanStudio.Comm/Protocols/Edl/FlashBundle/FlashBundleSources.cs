namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>Retains bounded source handles across bundle preflight and execution; not immutable content snapshots.</summary>
internal sealed class FlashBundleSources : IDisposable
{
    private readonly Dictionary<string, Source> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<RawProgramEntry, Source> _entries = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    internal int Count => _files.Count;

    internal static FlashBundleSources Open(IEnumerable<RawProgramEntry> entries, CancellationToken cancellationToken,
        int maximumFiles = 1024)
    {
        var sources = new FlashBundleSources();
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Filename))
                {
                    continue;
                }
                var path = Path.GetFullPath(entry.SourceFile);
                if (!sources._files.TryGetValue(path, out var source))
                {
                    if (sources._files.Count >= maximumFiles)
                    {
                        throw new InvalidDataException("The bundle exceeds the retained image file limit.");
                    }
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try
                    {
                        source = new Source(stream, stream.Length);
                        sources._files.Add(path, source);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                sources._entries.Add(entry, source);
            }
            return sources;
        }
        catch
        {
            sources.Dispose();
            throw;
        }
    }

    internal Stream Get(RawProgramEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(entry, out var source))
        {
            throw new InvalidOperationException("The program entry has no retained source.");
        }
        Validate(source);
        source.Stream.Position = 0;
        return source.Stream;
    }

    internal void ValidateLengths(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var source in _files.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(source);
        }
    }

    private static void Validate(Source source)
    {
        if (!source.Stream.CanRead || source.Stream.Length != source.Length)
        {
            throw new InvalidDataException("A retained bundle source changed length or became unreadable.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var source in _files.Values)
        {
            source.Stream.Dispose();
        }
        _files.Clear();
        _entries.Clear();
    }

    private sealed record Source(FileStream Stream, long Length);
}