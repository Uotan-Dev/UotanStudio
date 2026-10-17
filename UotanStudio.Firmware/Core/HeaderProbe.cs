using System.Buffers.Binary;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Optional header-based capability a format handler can publish so detection can confirm the format from
/// bytes the registry already read instead of opening the file again. The default implementation forwards to
/// <see cref="IReadonlyFormatHandler.CanHandle"/> and therefore keeps every handler that does not opt in working
/// through the path overload as a streaming fallback.
/// <para>格式处理器可选发布的头部探测能力，使检测可以依据注册表已读取的字节确认格式，而不必再次打开文件。
/// 默认实现转发到 <see cref="IReadonlyFormatHandler.CanHandle"/>，因此未选择加入的处理器仍通过路径重载这一流式兜底工作。</para>
/// </summary>
internal interface IHeaderFormatHandler : IReadonlyFormatHandler
{
    /// <summary>
    /// Determines whether the handler accepts a file from an already-read header prefix.
    /// <para>依据已读取的头部前缀判断处理器是否接受该文件。</para>
    /// </summary>
    /// <param name="header">
    /// Leading file bytes, possibly shorter than the header the handler would read on its own.
    /// <para>文件起始字节，可能短于处理器自行读取的头部。</para>
    /// </param>
    /// <param name="filePath">
    /// Path the header came from, forwarded for cheap name-based disambiguation; never opened by the default
    /// implementation beyond what the path overload already does.
    /// <para>头部来源路径，用于廉价的名字消歧；默认实现除了路径重载本身之外不会再打开它。</para>
    /// </param>
    /// <returns><see langword="true"/> when the header is sufficient and accepted; otherwise <see langword="false"/>.
    /// <para>头部足够且被接受时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></returns>
    bool CanHandle(ReadOnlySpan<byte> header, string filePath) => CanHandle(filePath);
}

/// <summary>
/// Ambient snapshot of one detection pass: the file length plus the header prefix read once by the registry.
/// Fixed-offset probes served from this snapshot avoid reopening the file; windows beyond the prefix fall back to
/// the streaming path so results stay identical for every handler.
/// <para>单次检测过程的周边快照：注册表一次读入的文件长度与头部前缀。固定偏移探测由该快照直接回答而不再打开文件；
/// 超出前缀的窗口回退到流式路径，从而对每个处理器保持结果不变。</para>
/// </summary>
internal sealed class HeaderProbeSession : IDisposable
{
    [ThreadStatic]
    private static HeaderProbeSession? _current;

    private readonly HeaderProbeSession? _previous;
    private bool _disposed;

    private HeaderProbeSession(string filePath, byte[] header, long totalLength, HeaderProbeSession? previous)
    {
        FilePath = filePath;
        Header = header;
        TotalLength = totalLength;
        _previous = previous;
    }

    /// <summary>
    /// Path this snapshot belongs to; probes for any other path always take the streaming path.
    /// <para>本快照对应的路径；其它路径的探测一律走流式路径。</para>
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Bytes read from the start of the file; may be shorter than <see cref="TotalLength"/>.
    /// <para>从文件起始处读取的字节；可能短于 <see cref="TotalLength"/>。</para>
    /// </summary>
    public byte[] Header { get; }

    /// <summary>
    /// Full file length observed when the snapshot was taken.
    /// <para>建立快照时观察到的文件总长度。</para>
    /// </summary>
    public long TotalLength { get; }

    /// <summary>
    /// The snapshot active on the current thread, or <see langword="null"/> outside a detection pass.
    /// <para>当前线程上活动的快照；检测过程之外为 <see langword="null"/>。</para>
    /// </summary>
    public static HeaderProbeSession? Current => _current;

    /// <summary>
    /// Activates a snapshot for the current thread until the returned scope is disposed.
    /// <para>为当前线程激活快照，直到返回的作用域被释放。</para>
    /// </summary>
    /// <param name="filePath">Path the header belongs to. <para>头部所属的路径。</para></param>
    /// <param name="header">Header prefix read by the caller. <para>调用方读取的头部前缀。</para></param>
    /// <param name="totalLength">Full file length observed by the caller. <para>调用方观察到的文件总长度。</para></param>
    /// <returns>The active scope; disposing it restores the previously active snapshot.
    /// <para>活动作用域；释放后恢复此前活动的快照。</para></returns>
    public static HeaderProbeSession Enter(string filePath, byte[] header, long totalLength)
    {
        var session = new HeaderProbeSession(filePath, header, totalLength, _current);
        _current = session;
        return session;
    }

    /// <summary>
    /// Deactivates this snapshot when it is the one currently active on the thread.
    /// <para>当本快照是线程上当前活动的快照时将其停用。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReferenceEquals(_current, this))
        {
            _current = _previous;
        }
    }

    /// <summary>
    /// Reports whether the snapshot belongs to the given path.
    /// <para>判断快照是否属于给定路径。</para>
    /// </summary>
    /// <param name="filePath">Path a handler passed to a fixed-offset probe. <para>处理器传给固定偏移探测的路径。</para></param>
    /// <returns><see langword="true"/> for the exact path string the snapshot was created with.
    /// <para>与建立快照时完全相同的路径字符串返回 <see langword="true"/>。</para></returns>
    public bool Matches(string filePath) => string.Equals(FilePath, filePath, StringComparison.Ordinal);

    /// <summary>
    /// Answers a fixed-offset magic probe from the active snapshot when the required window is cached.
    /// <para>所需窗口已缓存时，用活动快照回答固定偏移魔数探测。</para>
    /// </summary>
    /// <param name="filePath">Path the handler probes. <para>处理器探测的路径。</para></param>
    /// <param name="minimumLength">Minimum file length the handler requires. <para>处理器要求的最小文件长度。</para></param>
    /// <param name="offset">Offset of the magic. <para>魔数的偏移。</para></param>
    /// <param name="magic">Magic bytes to compare. <para>待比较的魔数字节。</para></param>
    /// <param name="result">The exact probe answer when the method returns <see langword="true"/>.
    /// <para>方法返回 <see langword="true"/> 时的精确探测结果。</para></param>
    /// <returns><see langword="true"/> when the snapshot can answer exactly; <see langword="false"/> when the
    /// caller must fall back to the stream. <para>快照能精确回答时为 <see langword="true"/>；调用方必须回退到流时为
    /// <see langword="false"/>。</para></returns>
    public static bool TryProbe(string filePath, int minimumLength, int offset, ReadOnlySpan<byte> magic, out bool result)
    {
        result = false;
        var session = _current;
        if (session is null || !session.Matches(filePath))
        {
            return false;
        }

        if (session.TotalLength < minimumLength)
        {
            return true;
        }

        if (offset < 0 || session.TotalLength - offset < magic.Length)
        {
            return true;
        }

        if (offset > session.Header.Length - magic.Length)
        {
            return false;
        }

        result = session.Header.AsSpan(offset, magic.Length).SequenceEqual(magic);
        return true;
    }

    /// <summary>
    /// Serves <see cref="FormatDetector.ReadHeader"/> requests from the active snapshot when it covers them.
    /// <para>活动快照覆盖请求时，为 <see cref="FormatDetector.ReadHeader"/> 请求提供数据。</para>
    /// </summary>
    /// <param name="filePath">Path the caller wants header bytes from. <para>调用方要读取头部的路径。</para></param>
    /// <param name="count">Requested byte count. <para>请求的字节数。</para></param>
    /// <param name="header">The returned copy when the method returns <see langword="true"/>.
    /// <para>方法返回 <see langword="true"/> 时返回的副本。</para></param>
    /// <returns><see langword="true"/> when the cached prefix determines the exact result; otherwise
    /// <see langword="false"/>. <para>缓存前缀可以确定精确结果时为 <see langword="true"/>；否则为
    /// <see langword="false"/>。</para></returns>
    public static bool TryReadHeader(string filePath, int count, out byte[] header)
    {
        header = [];
        var session = _current;
        if (session is null || count < 0 || !session.Matches(filePath))
        {
            return false;
        }

        if (count <= session.Header.Length)
        {
            header = session.Header.AsSpan(0, count).ToArray();
            return true;
        }

        if (session.TotalLength <= session.Header.Length)
        {
            header = session.Header.ToArray();
            return true;
        }

        return false;
    }
}

/// <summary>
/// Shared skeleton for format-handler sniffing: open the file, require a minimum length, read a magic at a fixed
/// offset and compare it, swallowing every error as a negative probe result.
/// <para>格式嗅探的公共骨架：打开文件、要求最小长度、读取固定偏移处的魔数并比较，任何错误都按探测失败处理。</para>
/// </summary>
internal static class HeaderProbe
{
    /// <summary>
    /// Probes a file for an exact byte magic at the given offset.
    /// <para>探测文件在给定偏移处是否为精确字节魔数。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to test; <see langword="null"/>, blank, missing or unreadable paths yield <see langword="false"/>.
    /// <para>待测试路径；<see langword="null"/>、空白、不存在或不可读均返回 <see langword="false"/>。</para>
    /// </param>
    /// <param name="minimumLength">
    /// Minimum total file length in bytes; use <c>0</c> when only the magic window matters.
    /// <para>文件最小总长度（字节）；只关心魔数窗口时用 <c>0</c>。</para>
    /// </param>
    /// <param name="offset">
    /// Byte offset of the magic inside the file.
    /// <para>魔数在文件内的字节偏移。</para>
    /// </param>
    /// <param name="magic">Exact byte sequence compared at <paramref name="offset"/>. <para>在 <paramref name="offset"/> 处比较的精确字节序列。</para></param>
    /// <returns>
    /// <see langword="true"/> only when the file is long enough and carries the magic; any read error yields
    /// <see langword="false"/>. <para>仅当文件足够长且携带魔数时为 <see langword="true"/>；任何读取错误返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool ProbeMagic(string? filePath, int minimumLength, int offset, ReadOnlySpan<byte> magic)
    {
        if (string.IsNullOrWhiteSpace(filePath) || magic.IsEmpty)
        {
            return false;
        }

        if (HeaderProbeSession.TryProbe(filePath, minimumLength, offset, magic, out var cachedResult))
        {
            return cachedResult;
        }

        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return Probe(stream, minimumLength, offset, magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Probes a file for a little-endian unsigned integer magic at the given offset.
    /// <para>探测文件在给定偏移处是否为小端无符号整数魔数。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to test; <see langword="null"/>, blank, missing or unreadable paths yield <see langword="false"/>.
    /// <para>待测试路径；<see langword="null"/>、空白、不存在或不可读均返回 <see langword="false"/>。</para>
    /// </param>
    /// <param name="minimumLength">
    /// Minimum total file length in bytes; use <c>0</c> when only the magic window matters.
    /// <para>文件最小总长度（字节）；只关心魔数窗口时用 <c>0</c>。</para>
    /// </param>
    /// <param name="offset">
    /// Byte offset of the magic inside the file.
    /// <para>魔数在文件内的字节偏移。</para>
    /// </param>
    /// <param name="magic">Value compared in little-endian encoding at <paramref name="offset"/>. <para>在 <paramref name="offset"/> 处按小端编码比较的值。</para></param>
    /// <returns>
    /// <see langword="true"/> only when the file is long enough and carries the magic; any read error yields
    /// <see langword="false"/>. <para>仅当文件足够长且携带魔数时为 <see langword="true"/>；任何读取错误返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool ProbeMagic(string? filePath, int minimumLength, int offset, uint magic)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic);
        return ProbeMagic(filePath, minimumLength, offset, bytes);
    }

    private static bool Probe(Stream stream, int minimumLength, int offset, ReadOnlySpan<byte> magic)
    {
        if (stream.Length < minimumLength || stream.Length - offset < magic.Length)
        {
            return false;
        }

        stream.Position = offset;
        Span<byte> bytes = stackalloc byte[magic.Length];
        stream.ReadExactly(bytes);
        return bytes.SequenceEqual(magic);
    }
}