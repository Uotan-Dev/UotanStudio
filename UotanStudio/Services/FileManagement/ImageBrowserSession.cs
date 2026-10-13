namespace UotanStudio.Services.FileManagement;

/// <summary>
/// Owns a parsed image, its entry index and any additional source stream. Closing the session
/// prevents new operations; an already leased extraction finishes before resources are released.
/// <para>拥有已解析镜像、其条目索引及额外的源流。关闭会话会阻止新操作；已获租约的解包在释放资源前完成。</para>
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

    /// <summary>The detected format name.<para>检测到的格式名称。</para></summary>
    /// <value>The format name for this session.<para>本会话的格式名称。</para></value>
    internal string FormatName { get; }
    /// <summary>Logical size for sparse images; source file size otherwise.<para>稀疏镜像取逻辑大小，其余取源文件大小。</para></summary>
    /// <value>The reported image size in bytes.<para>以字节计的上报镜像大小。</para></value>
    internal long ImageSize { get; }
    /// <summary>Parsed entries with no navigation state retained.<para>已解析条目，不保留导航状态。</para></summary>
    /// <value>The flat entry list for this session.<para>本会话的扁平条目列表。</para></value>
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

    /// <summary>Extracts a selection as one operation, serializing access to parser streams.<para>将选定内容作为一次操作解包，串行访问解析器流。</para></summary>
    /// <param name="paths">Entry paths to extract.<para>待解包的条目路径。</para></param>
    /// <param name="targetDirectory">Directory receiving the extracted files.<para>接收解包文件的目录。</para></param>
    internal void Extract(IReadOnlyList<string> paths, string targetDirectory)
    {
        using var lease = AcquireLease();
        lock (_extraction)
        {
            foreach (var path in paths) ExtractEntryCore(path, targetDirectory);
        }
    }

    /// <summary>Keeps resources alive until an accepted operation completes.<para>让资源存活直到被接受的操作完成。</para></summary>
    /// <returns>A lease released when the operation ends.<para>操作结束时释放的租约。</para></returns>
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

    /// <summary>Closes the session and releases resources once outstanding leases finish.<para>关闭会话，并在未决租约结束后释放资源。</para></summary>
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