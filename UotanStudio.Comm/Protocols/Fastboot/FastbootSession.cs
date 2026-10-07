using System.Buffers;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Implements the fastboot protocol session over a device transport.
/// <para>在设备传输之上实现 fastboot 协议会话。</para>
/// </summary>
public sealed partial class FastbootSession : IProtocolSession
{
    /// <summary>
    /// Frame delimiters: they end an INFO/TEXT payload or a DATA size field, and a run of them after a
    /// frame is padding that belongs to that frame.
    /// <para>帧分隔符：用于结束 INFO/TEXT 负载或 DATA 尺寸字段，帧之后的连续分隔符属于该帧的填充。</para>
    /// </summary>
    private static readonly SearchValues<byte> ResponseDelimiters = SearchValues.Create([(byte)'\0', (byte)'\r', (byte)'\n']);
    private readonly int _timeoutMs;
    private readonly Dictionary<string, string> _varCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _hasSlotCache = new(StringComparer.Ordinal);
    private HashSet<string>? _logicalPartitionsFromMetadata;
    private long? _maxDownloadSize;

    /// <summary>
    /// Bytes the device accepted for the download currently in flight, or 0 while no download is
    /// pending. Set when a DATA frame is accepted and cleared at the terminal frame, so an abandoned
    /// download can be drained with exactly the byte count the bootloader still expects.
    /// <para>设备为当前进行中下载所接受的字节数；无下载待处理时为 0。在接受 DATA 帧时置位，在终止帧处
    /// 清零，使被弃单的下载能按 bootloader 仍在等待的确切字节数排空。</para>
    /// </summary>
    private long _pendingDownloadBytes;

    /// <summary>
    /// Wall-clock budget for the trailing terminal frame drained after an abandoned download. It is
    /// deliberately shorter than the command timeout: the zero padding that precedes it already told
    /// the bootloader the download is complete, so a device that stays silent is treated as lost and
    /// the transport is reset instead of blocking the caller for a full command timeout.
    /// <para>弃单下载后排空尾部终止帧的墙上时间预算。刻意短于命令超时：其前的零填充已经告诉 bootloader
    /// 下载完成，此时仍沉默的设备按丢失处理并重置传输，而不是让调用方阻塞整个命令超时。</para>
    /// </summary>
    private const int AbortDrainTimeoutMs = 5000;

    /// <summary>
    /// Payload bytes received after a terminal frame of the previous response and not yet
    /// consumed. Fastboot replies are packet-framed on the wire, so keeping the remainder
    /// resynchronizes the stream instead of attributing stale bytes to the next command.
    /// <para>上一次响应的终止帧之后收到且尚未消费的负载字节。fastboot 应答在线上是按包成帧的，
    /// 保留余量可以让流重新同步，而不是把过期字节算到下一条命令头上。</para>
    /// </summary>
    private readonly PendingBuffer _pendingBytes = new();

    /// <summary>
    /// Growable byte accumulator for wire framing. Appends copy a whole transport read at once and
    /// decoders slice the buffer directly, replacing per-byte list appends and the whole-buffer copy
    /// every UTF-8 decode previously paid.
    /// <para>线上组帧用的可增长字节累积器。追加按整次传输读取一次性拷贝，解码直接对缓冲区分片，
    /// 取代此前逐字节的列表追加以及每次 UTF-8 解码付出的整缓冲拷贝。</para>
    /// </summary>
    private sealed class PendingBuffer
    {
        private byte[] _buffer = new byte[FastbootConstants.ResponseBufferSize * 4];
        private int _length;

        /// <summary>Buffered byte count. <para>已缓冲的字节数。</para></summary>
        /// <value>Bytes currently buffered. <para>当前缓冲的字节数。</para></value>
        public int Count => _length;

        /// <summary>Buffered bytes as one contiguous span. <para>已缓冲字节构成的连续切片。</para></summary>
        /// <value>The buffered prefix of the backing buffer. <para>后备缓冲区的已缓冲前缀。</para></value>
        public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

        /// <summary>Discards all buffered bytes. <para>丢弃全部已缓冲字节。</para></summary>
        public void Clear() => _length = 0;

        /// <summary>Appends a whole read to the buffer, growing it when needed.
        /// <para>把一次读取整体追加进缓冲区，必要时扩容。</para></summary>
        /// <param name="data">Bytes delivered by one transport read. <para>一次传输读取送达的字节。</para></param>
        public void Append(ReadOnlySpan<byte> data)
        {
            if (_buffer.Length < _length + data.Length)
            {
                var grown = new byte[Math.Max(_buffer.Length * 2, _length + data.Length)];
                _buffer.AsSpan(0, _length).CopyTo(grown);
                _buffer = grown;
            }

            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }

        /// <summary>Removes <paramref name="count"/> leading bytes and shifts the remainder forward.
        /// <para>移除起始 <paramref name="count"/> 个字节并把剩余部分前移。</para></summary>
        /// <param name="count">Number of leading bytes to drop; at most <see cref="Count"/>.
        /// <para>要丢弃的起始字节数；至多 <see cref="Count"/>。</para></param>
        public void RemoveFromStart(int count)
        {
            if (count < 0 || count > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _buffer.AsSpan(count, _length - count).CopyTo(_buffer);
            _length -= count;
        }
    }

    /// <summary>Upper bound on bytes accumulated for one command's TEXT payload; a device flooding
    /// TEXT frames beyond this fails the command instead of growing the buffer unboundedly.
    /// <para>单条命令 TEXT 负载的累计字节上限；设备发送超过此上限的 TEXT 帧时命令失败，
    /// 而不是无界增长缓冲。</para></summary>
    private const int MaxTextBytes = 4 * 1024 * 1024;

    private bool _disposed;

    /// <summary>
    /// Gets the underlying USB session when the transport is USB-based; otherwise, <c>null</c>.
    /// <para>获取底层 USB 会话；传输非 USB 时返回 <c>null</c>。</para>
    /// </summary>
    private UsbDeviceTransport? Usb => Transport as UsbDeviceTransport;

    /// <summary>Protocol identifier reported to the host layer. <para>上报给宿主层的协议标识符。</para></summary>
    /// <value>Always the literal <c>fastboot</c>. <para>始终为字面量 <c>fastboot</c>。</para></value>
    public string ProtocolName => "fastboot";

    /// <summary>Transport carrying every fastboot command, response and payload exchange. <para>承载所有 fastboot 命令、响应与负载交换的传输。</para></summary>
    /// <value>The transport supplied at construction; the session disposes it together with itself. <para>构造时传入的传输；会话释放时会一并处置它。</para></value>
    public ICommTransport Transport { get; }

    /// <summary>Connection liveness reported to the host layer. <para>上报给宿主层的连接活性。</para></summary>
    /// <value><see langword="true"/> only while the session is not disposed and the underlying transport still reports a live connection. <para>仅在会话未释放且底层传输仍报告连接存活时为 <see langword="true"/>。</para></value>
    public bool IsConnected => !_disposed && Transport.IsConnected;

    /// <summary>
    /// Creates a new fastboot session over the specified transport.
    /// <para>在指定传输上创建新的 fastboot 会话。</para>
    /// </summary>
    internal FastbootSession(ICommTransport transport, int? timeoutMs = null)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var defaultTimeoutMs = Usb?.Session.DefaultTimeoutMs ?? 0;
        _timeoutMs = timeoutMs ?? (defaultTimeoutMs > 0 ? defaultTimeoutMs : FastbootConstants.DefaultResponseTimeoutSeconds * 1000);
    }

    /// <summary>
    /// Sends a raw fastboot command and returns the parsed response.
    /// <para>发送原始 fastboot 命令并返回解析后的响应。</para>
    /// </summary>
    /// <param name="command">Raw command text as sent on the wire (e.g. <c>getvar:product</c>); no validation or framing is added. <para>线上原样发送的命令文本（如 <c>getvar:product</c>）；不追加校验或成帧。</para></param>
    /// <returns>The parsed terminal response; INFO frames encountered while waiting are consumed into its status lines. <para>解析后的终态响应；等待期间遇到的 INFO 帧会被消费进其状态行。</para></returns>
    /// <exception cref="ArgumentException">The encoded command exceeds the fastboot frame length limit. <para>编码后的命令超过 fastboot 帧长度上限。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public FastbootResponse ExecuteCommand(string command)
    {
        ThrowIfDisposed();
        SendCommand(command);
        return ReceiveResponse();
    }

    /// <summary>
    /// Sends a getvar query and returns the variable value, or empty on FAIL/timeout
    /// (matching AOSP behavior where an unknown variable yields no value).
    /// <para>发送 getvar 查询并返回变量值，FAIL/超时时返回空（与 AOSP 未知变量无值的行为一致）。</para>
    /// </summary>
    /// <param name="key">Variable name without the <c>getvar:</c> prefix, matched case-sensitively in the cache. <para>不含 <c>getvar:</c> 前缀的变量名，在缓存中按大小写敏感匹配。</para></param>
    /// <param name="useCache">When <see langword="true"/>, consults and updates the session getvar cache; pass <see langword="false"/> to force a fresh query. <para>为 <see langword="true"/> 时查询并更新会话 getvar 缓存；传 <see langword="false"/> 强制重新查询。</para></param>
    /// <returns>The reported value, or <see cref="string.Empty"/> when the device answers FAIL or the exchange times out. <para>设备上报的值；设备回答 FAIL 或交换超时时返回 <see cref="string.Empty"/>。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public string GetVariable(string key, bool useCache = true)
    {
        if (useCache && _varCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var response = ExecuteCommand(FastbootConstants.CommandGetVar + key);
        if (response.State is FastbootResponseState.Fail or FastbootResponseState.Timeout)
        {
            return string.Empty;
        }

        var value = response.Info;
        if (useCache)
        {
            _varCache[key] = value;
        }

        return value;
    }

    /// <summary>
    /// Queries all device variables via getvar:all and returns them as a dictionary.
    /// Variables are reported as INFO lines of the form "key: value"; the parsed values
    /// are also stored in the getvar cache.
    /// <para>通过 getvar:all 查询所有设备变量并以字典形式返回。变量以 "key: value" 形式的
    /// INFO 行上报；解析结果同时写入 getvar 缓存。</para>
    /// </summary>
    /// <returns>Case-sensitive dictionary of every reported variable; on duplicate keys the first occurrence wins.
    /// <para>大小写敏感的变量字典；重复键以首次出现的值为准。</para>
    /// </returns>
    /// <exception cref="FastbootException">The device answered FAIL or the exchange timed out. <para>设备回答 FAIL 或交换超时。</para></exception>
    public Dictionary<string, string> GetAllVariables()
    {
        var response = ExecuteCommand(FastbootConstants.CommandGetVarAll);
        response.ThrowIfFailed();

        _varCache.Clear();
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in response.InfoLines)
        {
            var colonIdx = line.LastIndexOf(':');
            if (colonIdx <= 0)
            {
                continue;
            }

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].TrimStart();
            if (!dict.ContainsKey(key))
            {
                dict[key] = value;
                _varCache[key] = value;
            }
        }
        return dict;
    }

    /// <summary>
    /// Tries to query a device variable, returning <c>false</c> on any transport failure.
    /// <para>尝试查询设备变量，传输失败时返回 <c>false</c>。</para>
    /// </summary>
    private bool TryGetVar(string key, out string value)
    {
        try
        {
            value = GetVariable(key, useCache: false);
            return true;
        }
        catch
        {
            value = string.Empty;
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FastbootSession));
        }
    }

    /// <summary>
    /// Disposes the session and the underlying transport.
    /// <para>释放会话和底层传输。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Transport.Dispose();
    }
}