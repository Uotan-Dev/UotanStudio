using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot.Utils;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Implements the fastboot protocol session over a device transport.
/// <para>在设备传输之上实现 fastboot 协议会话。</para>
/// </summary>
public sealed class FastbootSession : IProtocolSession
{
    /// <summary>
    /// Frame delimiters used to find the end of an INFO/TEXT payload.
    /// <para>用于查找 INFO/TEXT 负载结尾的帧分隔符。</para>
    /// </summary>
    private static readonly char[] ResponseDelimiters = ['\0', '\r', '\n'];
    private readonly int _timeoutMs;
    private readonly Dictionary<string, string> _varCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _hasSlotCache = new(StringComparer.Ordinal);
    private HashSet<string>? _logicalPartitionsFromMetadata;
    private long? _maxDownloadSize;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying USB session when the transport is USB-based; otherwise, <c>null</c>.
    /// <para>获取底层 USB 会话；传输非 USB 时返回 <c>null</c>。</para>
    /// </summary>
    private UsbDeviceTransport? Usb => Transport as UsbDeviceTransport;

    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "fastboot";

    /// <summary>Gets the underlying transport. 获取底层传输。</summary>
    public ICommTransport Transport { get; }

    /// <summary>Gets a value indicating whether the session is currently connected. 获取会话是否已连接。</summary>
    public bool IsConnected => !_disposed && Transport.IsConnected;

    /// <summary>
    /// Creates a new fastboot session over the specified transport.
    /// <para>在指定传输上创建新的 fastboot 会话。</para>
    /// </summary>
    public FastbootSession(ICommTransport transport, int? timeoutMs = null)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var defaultTimeoutMs = Usb?.Session.DefaultTimeoutMs ?? 0;
        _timeoutMs = timeoutMs ?? (defaultTimeoutMs > 0 ? defaultTimeoutMs : FastbootConstants.DefaultResponseTimeoutSeconds * 1000);
    }

    /// <summary>
    /// Sends a raw fastboot command and returns the parsed response.
    /// <para>发送原始 fastboot 命令并返回解析后的响应。</para>
    /// </summary>
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
    /// Sends a raw command string to the device (UTF-8 encoded, max 4096 bytes).
    /// <para>向设备发送原始命令字符串（UTF-8 编码，最大 4096 字节）。</para>
    /// </summary>
    public void SendCommand(string command)
    {
        ThrowIfDisposed();
        var cmdBytes = Encoding.UTF8.GetBytes(command);
        if (cmdBytes.Length > FastbootConstants.MaxCommandLength)
        {
            throw new ArgumentException($"Command too long ({cmdBytes.Length} > {FastbootConstants.MaxCommandLength})", nameof(command));
        }

        var written = Transport.Write(cmdBytes, 0, cmdBytes.Length, _timeoutMs);
        if (written != cmdBytes.Length)
        {
            throw new FastbootException(FastbootResponseState.Fail, $"Short write: {written}/{cmdBytes.Length}");
        }
    }

    /// <summary>
    /// Receives and parses a fastboot protocol response.
    /// <para>接收并解析 fastboot 协议响应。</para>
    /// Loops over the accumulated pending buffer, recognizing OKAY/FAIL/INFO/TEXT/DATA
    /// prefixes at each frame boundary. INFO lines and TEXT content are accumulated on the
    /// returned response; the call only returns when a terminal OKAY/FAIL/DATA frame (or an
    /// unrecoverable unknown prefix) is reached. The timeout deadline is reset on each
    /// INFO/TEXT frame so long-running commands with periodic progress output do not abort.
    /// <para>循环扫描累积的待解析缓冲区，在每个帧边界识别 OKAY/FAIL/INFO/TEXT/DATA 前缀。
    /// INFO 行与 TEXT 内容累积到返回的响应对象上；仅在到达终止 OKAY/FAIL/DATA 帧
    /// （或出现不可恢复的未知前缀）时返回。每收到一个 INFO/TEXT 帧会重置超时截止时间，
    /// 使周期性输出进度的长时间命令不会中途超时。</para>
    /// </summary>
    public FastbootResponse ReceiveResponse(int? timeoutMs = null)
    {
        ThrowIfDisposed();

        var response = new FastbootResponse();
        var timeout = timeoutMs ?? _timeoutMs;
        // Use the monotonic clock (Stopwatch) for the response timeout so that system clock
        // adjustments cannot corrupt the deadline, matching AOSP's steady_clock.
        var startTimestamp = Stopwatch.GetTimestamp();
        var timeoutTicks = (long)(Stopwatch.Frequency * (timeout / 1000.0));
        var pending = string.Empty;
        var pendingOffset = 0;
        StringBuilder? textBuffer = null;

        var buffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.ResponseBufferSize);
        try
        {
            while (true)
            {
                // Per-read timeout is bounded by the global budget only (no per-iteration
                // floor that would let the total wait overshoot the requested timeout).
                var remainingTicks = timeoutTicks - (Stopwatch.GetTimestamp() - startTimestamp);
                if (remainingTicks <= 0)
                {
                    break;
                }

                var readTimeoutMs = Math.Max(1, (int)Math.Min(int.MaxValue,
                    ((remainingTicks * 1000) + Stopwatch.Frequency - 1) / Stopwatch.Frequency));
                var readLen = Transport.Read(buffer, 0, FastbootConstants.ResponseBufferSize, readTimeoutMs);
                if (readLen <= 0)
                {
                    continue;
                }

                pending += Encoding.UTF8.GetString(buffer, 0, readLen);

                while (true)
                {
                    CompactPendingIfNeeded(ref pending, ref pendingOffset);

                    if (pending.Length - pendingOffset < 4)
                    {
                        break;
                    }

                    if (!IsKnownPrefixAt(pending, pendingOffset))
                    {
                        // Scan forward for the next known status prefix; if none exists the
                        // stream is unrecoverable (device sent an unknown status code).
                        var nextPrefix = -1;
                        for (var i = pendingOffset + 1; i <= pending.Length - 4; i++)
                        {
                            if (IsKnownPrefixAt(pending, i))
                            {
                                nextPrefix = i;
                                break;
                            }
                        }

                        if (nextPrefix > pendingOffset)
                        {
                            pendingOffset = nextPrefix;
                            continue;
                        }

                        var junk = pending[pendingOffset..];
                        response.State = FastbootResponseState.Unknown;
                        response.Info = "device sent unknown status code: " + junk;
                        response.Text = junk;
                        return response;
                    }

                    var isOkay = IsPrefixAt(pending, pendingOffset, 'O', 'K', 'A', 'Y');
                    var isFail = IsPrefixAt(pending, pendingOffset, 'F', 'A', 'I', 'L');
                    var isInfo = IsPrefixAt(pending, pendingOffset, 'I', 'N', 'F', 'O');
                    var isText = IsPrefixAt(pending, pendingOffset, 'T', 'E', 'X', 'T');
                    // DATA is the remaining known prefix at this point.

                    if (isOkay || isFail)
                    {
                        var content = ExtractContent(pending, pendingOffset + 4);
                        response.State = isOkay ? FastbootResponseState.Success : FastbootResponseState.Fail;
                        response.Info = content;
                        response.Text = textBuffer?.ToString() ?? content;
                        return response;
                    }

                    if (isInfo || isText)
                    {
                        var contentStart = pendingOffset + 4;
                        var endIdx = FindInfoTextEnd(pending, contentStart);
                        if (endIdx < 0)
                        {
                            // Most transports deliver one status frame per read packet. If no
                            // boundary marker exists, treat the current chunk as one complete
                            // INFO/TEXT frame to avoid merging with the next status frame.
                            endIdx = pending.Length;
                        }

                        var cleanContent = pending[contentStart..endIdx];
                        if (isInfo)
                        {
                            response.InfoLines.Add(cleanContent);
                        }
                        else
                        {
                            textBuffer ??= new StringBuilder();
                            textBuffer.Append(cleanContent);
                        }

                        var next = endIdx;
                        while (next < pending.Length &&
                               (pending[next] == '\0' || pending[next] == '\r' || pending[next] == '\n'))
                        {
                            next++;
                        }
                        pendingOffset = next;
                        // Reset the deadline on progress output so long-running commands
                        // (erase, flash, getvar:all, ...) with periodic INFO frames keep going.
                        startTimestamp = Stopwatch.GetTimestamp();
                        continue;
                    }

                    // DATA: expected to carry a hex size field and no extra payload.
                    var dataHex = ExtractContent(pending, pendingOffset + 4).Trim();
                    var dataSize = ParseDataSizeHex(dataHex);
                    if (dataSize < 0)
                    {
                        response.State = FastbootResponseState.Fail;
                        response.Info = dataHex;
                        response.Text = dataSize == -2
                            ? $"data size too large {dataHex}"
                            : "data size malformed: " + dataHex;
                        return response;
                    }

                    response.State = FastbootResponseState.Data;
                    response.DataSize = dataSize;
                    response.Text = textBuffer?.ToString() ?? string.Empty;
                    return response;
                }
            }

            response.State = FastbootResponseState.Timeout;
            response.Text = "response timeout";
            return response;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Parses the DATA frame hex size field. The field must be at most 8 hex digits and
    /// must not exceed <see cref="FastbootConstants.SparseMaxDownloadSize"/>.
    /// <para>解析 DATA 帧的 16 进制尺寸字段。该字段最多 8 个 16 进制数字，
    /// 且不得超过 <see cref="FastbootConstants.SparseMaxDownloadSize"/>。</para>
    /// </summary>
    /// <returns>The parsed size, or -1 when malformed, or -2 when too large.
    /// <para>解析出的尺寸；格式错误返回 -1，过大返回 -2。</para></returns>
    private static long ParseDataSizeHex(string dataHex)
    {
        if (dataHex.Length is 0 or > 8)
        {
            return -1;
        }

        for (var i = 0; i < dataHex.Length; i++)
        {
            if (!Uri.IsHexDigit(dataHex[i]))
            {
                return -1;
            }
        }

        return !long.TryParse(dataHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size)
            ? -1
            : size > FastbootConstants.SparseMaxDownloadSize ? -2 : size;
    }

    /// <summary>
    /// Compacts the pending buffer when the consumed prefix grows large enough to
    /// make re-allocating the remainder worthwhile.
    /// <para>当已消费前缀足够大、值得重新分配剩余部分时压缩待解析缓冲区。</para>
    /// </summary>
    private static void CompactPendingIfNeeded(ref string pending, ref int offset)
    {
        if (offset <= 0)
        {
            return;
        }

        if (offset > 1024 || offset > pending.Length / 2)
        {
            pending = pending[offset..];
            offset = 0;
        }
    }

    private static bool IsPrefixAt(string s, int index, char a, char b, char c, char d)
        => index + 4 <= s.Length &&
           s[index] == a &&
           s[index + 1] == b &&
           s[index + 2] == c &&
           s[index + 3] == d;

    private static bool IsKnownPrefixAt(string s, int index)
        => IsPrefixAt(s, index, 'O', 'K', 'A', 'Y') ||
           IsPrefixAt(s, index, 'F', 'A', 'I', 'L') ||
           IsPrefixAt(s, index, 'I', 'N', 'F', 'O') ||
           IsPrefixAt(s, index, 'T', 'E', 'X', 'T') ||
           IsPrefixAt(s, index, 'D', 'A', 'T', 'A');

    private static bool IsTerminalPrefixAt(string s, int index)
        => IsPrefixAt(s, index, 'O', 'K', 'A', 'Y') ||
           IsPrefixAt(s, index, 'F', 'A', 'I', 'L') ||
           IsPrefixAt(s, index, 'D', 'A', 'T', 'A');

    /// <summary>
    /// Extracts the content following a 4-char status prefix at <paramref name="start"/>,
    /// trimming a single trailing NUL.
    /// <para>提取 <paramref name="start"/> 处 4 字符状态前缀之后的内容，去除单个尾随 NUL。</para>
    /// </summary>
    private static string ExtractContent(string s, int start)
    {
        var len = s.Length - start;
        if (len <= 0)
        {
            return string.Empty;
        }

        if (s[^1] == '\0')
        {
            len--;
        }

        return len == 0 ? string.Empty : s.Substring(start, len);
    }

    /// <summary>
    /// Finds the end of an INFO/TEXT payload: a NUL/CR/LF delimiter, or the next status
    /// prefix when the frame carries no delimiter.
    /// <para>查找 INFO/TEXT 负载的结尾：NUL/CR/LF 分隔符，或帧无分隔符时的下一个状态前缀。</para>
    /// </summary>
    private static int FindInfoTextEnd(string s, int contentStart)
    {
        var delimiterIdx = s.IndexOfAny(ResponseDelimiters, contentStart);
        if (delimiterIdx >= 0)
        {
            return delimiterIdx;
        }

        var firstTerminalIdx = -1;
        for (var i = contentStart + 1; i <= s.Length - 4; i++)
        {
            if (IsTerminalPrefixAt(s, i))
            {
                firstTerminalIdx = i;
                break;
            }
        }

        for (var i = contentStart + 1; i <= s.Length - 4; i++)
        {
            // For INFO/TEXT payloads without delimiters, treat any known status prefix
            // (OKAY/FAIL/DATA/INFO/TEXT) as a boundary. This splits consecutive INFO/TEXT
            // frames in a single packet while protecting against malformed streams.
            if (IsTerminalPrefixAt(s, i))
            {
                return i;
            }

            // Avoid splitting plain payload text that happens to contain INFO/TEXT tokens:
            // only split mid-payload when the packet also contains a terminal status marker.
            var isInfoAt = IsPrefixAt(s, i, 'I', 'N', 'F', 'O');
            var isTextAt = IsPrefixAt(s, i, 'T', 'E', 'X', 'T');
            if ((isInfoAt || isTextAt) && firstTerminalIdx > i)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Negotiates and caches the maximum download size supported by the device.
    /// Queries <c>max-download-size</c> via getvar, clamps against the wire-level ceiling
    /// (<see cref="FastbootConstants.SparseMaxDownloadSize"/>) and falls back to
    /// <see cref="FastbootConstants.FallbackMaxDownloadSize"/> when the device does not
    /// report a usable value.
    /// <para>协商并缓存设备支持的最大下载大小。通过 getvar 查询 <c>max-download-size</c>，
    /// 按线协议上限（<see cref="FastbootConstants.SparseMaxDownloadSize"/>）钳制；
    /// 设备未上报可用值时回退到 <see cref="FastbootConstants.FallbackMaxDownloadSize"/>。</para>
    /// </summary>
    public long GetMaxDownloadSize()
    {
        if (_maxDownloadSize is long cached)
        {
            return cached;
        }

        string? sizeStr = null;
        try { sizeStr = GetVariable(FastbootConstants.VarMaxDownloadSize); }
        catch { /* transport errors fall back to the conservative default */ }

        long negotiated;
        if (string.IsNullOrEmpty(sizeStr))
        {
            negotiated = Math.Min(FastbootConstants.SparseMaxDownloadSize, FastbootConstants.FallbackMaxDownloadSize);
        }
        else
        {
            var trimmed = sizeStr.Trim();
            var parsedSize = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : -1
                : long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : -1;

            negotiated = parsedSize <= 0
                ? Math.Min(FastbootConstants.SparseMaxDownloadSize, FastbootConstants.FallbackMaxDownloadSize)
                : Math.Min(Math.Min(parsedSize, FastbootConstants.SparseMaxDownloadSize), uint.MaxValue);
        }

        _maxDownloadSize = negotiated;
        return negotiated;
    }

    /// <summary>
    /// Downloads data to the device: sends download:&lt;hex-size&gt;, streams data, and awaits OKAY/FAIL.
    /// Payloads exceeding the negotiated max download size are rejected.
    /// <para>下载数据到设备：发送 download:&lt;hex-size&gt;，流式传输数据，等待 OKAY/FAIL。
    /// 超过协商的最大下载大小的负载会被拒绝。</para>
    /// </summary>
    public FastbootResponse DownloadData(Stream stream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (length is <= 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var maxDownloadSize = GetMaxDownloadSize();
        if (length > maxDownloadSize)
        {
            throw new FastbootException(FastbootResponseState.Fail,
                $"download payload {length} exceeds max-download-size {maxDownloadSize}");
        }

        SendCommand(FastbootConstants.CommandDownload + length.ToString("x8"));

        var rawResponse = ReceiveResponse();
        if (rawResponse.State != FastbootResponseState.Data || rawResponse.DataSize != length)
        {
            rawResponse.Text = $"download size mismatch: requested {length}, device accepted {rawResponse.DataSize}";
            return rawResponse;
        }

        var chunkBuffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.TransferChunkSize);
        try
        {
            long bytesWritten = 0;
            while (bytesWritten < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(FastbootConstants.TransferChunkSize, length - bytesWritten);
                var read = stream.Read(chunkBuffer, 0, toRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Stream ended early at {bytesWritten}/{length}");
                }

                var written = Transport.Write(chunkBuffer, 0, read, _timeoutMs);
                if (written != read)
                {
                    throw new FastbootException(FastbootResponseState.Fail, $"Short write: {written}/{read}");
                }

                bytesWritten += written;
                progress?.Report(bytesWritten);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }

        return ReceiveResponse();
    }

    /// <summary>
    /// Downloads a byte array to the device in chunks.
    /// <para>将字节数组分块下载到设备。</para>
    /// </summary>
    public FastbootResponse DownloadData(byte[] data, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(data);
        return DownloadData(ms, data.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Downloads a file to the device from disk.
    /// <para>从磁盘下载文件到设备。</para>
    /// </summary>
    public FastbootResponse DownloadFile(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.OpenRead(filePath);
        return DownloadData(fs, fs.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Executes a command that yields a DATA response, then receives the announced payload
    /// into the specified stream and awaits the trailing OKAY/FAIL.
    /// Used by fetch/upload/get_staged Style commands.
    /// <para>执行会产生 DATA 响应的命令，将声明的负载接收到指定流，并等待随后的 OKAY/FAIL。
    /// 供 fetch/upload/get_staged 类命令使用。</para>
    /// </summary>
    public FastbootResponse UploadData(string command, Stream output, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var response = ExecuteCommand(command);
        if (response.State != FastbootResponseState.Data)
        {
            throw new FastbootException(response.State, $"Unexpected response for upload: expected DATA, got {response.State}");
        }

        ReadDataPayload(response.DataSize, output, progress, cancellationToken);
        return ReceiveResponse();
    }

    /// <summary>
    /// Uploads data from the device following an already-received DATA response.
    /// <para>在已收到的 DATA 响应之后从设备接收数据负载。</para>
    /// </summary>
    public FastbootResponse UploadData(Stream output, long size, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ReadDataPayload(size, output, progress, cancellationToken);
        return ReceiveResponse();
    }

    /// <summary>
    /// Reads the exact payload size announced by a DATA frame into the output stream.
    /// <para>将 DATA 帧声明的精确负载读取到输出流。</para>
    /// </summary>
    private void ReadDataPayload(long size, Stream output, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var chunkBuffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.TransferChunkSize);
        try
        {
            long bytesRead = 0;
            while (bytesRead < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(FastbootConstants.TransferChunkSize, size - bytesRead);
                var read = Transport.Read(chunkBuffer, 0, toRead, _timeoutMs);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Device EOF at {bytesRead}/{size}");
                }

                output.Write(chunkBuffer, 0, read);
                bytesRead += read;
                progress?.Report(bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }
    }

    /// <summary>
    /// Uploads a file from the device following an upload: command and writes it to the
    /// specified stream.
    /// <para>执行 upload: 命令从设备上传文件并写入指定流。</para>
    /// </summary>
    public FastbootResponse UploadToStream(string filename, Stream output, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => UploadData(FastbootConstants.CommandUpload + filename, output, progress, cancellationToken);

    /// <summary>
    /// Uploads a file from the device and saves it to the specified output path.
    /// <para>从设备上传文件并保存到指定输出路径。</para>
    /// </summary>
    public FastbootResponse Upload(string filename, string outputPath)
    {
        using var fs = File.Create(outputPath);
        return UploadToStream(filename, fs);
    }

    /// <summary>
    /// Uploads a file from the device and returns its contents as a byte array.
    /// <para>从设备上传文件并以字节数组形式返回其内容。</para>
    /// </summary>
    public byte[] UploadToBytes(string filename)
    {
        using var ms = new MemoryStream();
        var response = UploadData(FastbootConstants.CommandUpload + filename, ms);
        return response.State != FastbootResponseState.Success
            ? throw new FastbootException(response.State, "upload failed: " + response.Text)
            : ms.ToArray();
    }

    /// <summary>
    /// Fetches a partition image from the device and writes it to a stream, automatically
    /// fetching in chunks bounded by max-fetch-size when the partition is larger.
    /// <para>从设备获取分区镜像并写入流；分区超过 max-fetch-size 时自动分块获取。</para>
    /// </summary>
    /// <param name="partition">The partition to fetch (e.g. "boot", "system"). <para>要获取的分区（如 "boot"、"system"）。</para></param>
    /// <param name="output">The stream to write the fetched data to. <para>写入获取数据的流。</para></param>
    /// <param name="offset">Byte offset within the partition to start fetching. <para>分区内开始获取的字节偏移。</para></param>
    /// <param name="size">Number of bytes to fetch, or -1 for the entire partition. <para>要获取的字节数，-1 表示整个分区。</para></param>
    /// <param name="progress">Receives the cumulative fetched byte count. <para>接收累计已获取字节数。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    public FastbootResponse FetchToStream(string partition, Stream output, long offset = 0, long size = -1, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(partition))
        {
            throw new ArgumentException("Partition name must not be empty.", nameof(partition));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var targetPartition = ResolvePartitionWithSlot(partition);

        if (size == -1)
        {
            size = GetPartitionSizeLong(targetPartition);
        }

        var maxFetchSize = GetMaxFetchSize();
        if (size > 0 && maxFetchSize > 0 && size > maxFetchSize)
        {
            long fetched = 0;
            while (fetched < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = Math.Min(maxFetchSize, size - fetched);
                var res = UploadData($"{FastbootConstants.CommandFetch}{targetPartition}:{offset + fetched:x}:{chunk:x}", output,
                    cancellationToken: cancellationToken);
                if (res.State != FastbootResponseState.Success)
                {
                    return res;
                }

                fetched += chunk;
                progress?.Report(fetched);
            }
            return new FastbootResponse { State = FastbootResponseState.Success };
        }

        var finalCommand = $"{FastbootConstants.CommandFetch}{targetPartition}:{offset:x}";
        if (size >= 0)
        {
            finalCommand += $":{size:x}";
        }

        return UploadData(finalCommand, output, progress, cancellationToken);
    }

    /// <summary>
    /// Fetches a partition image (optionally a portion of it) from the device and saves it
    /// to a file.
    /// <para>从设备获取分区镜像（可选其中一部分）并保存到文件。</para>
    /// </summary>
    /// <param name="partition">The partition to fetch. <para>要获取的分区。</para></param>
    /// <param name="outputPath">Path to save the fetched image. <para>保存获取镜像的路径。</para></param>
    /// <param name="offset">Byte offset within the partition to start fetching. <para>分区内开始获取的字节偏移。</para></param>
    /// <param name="size">Number of bytes to fetch, or -1 for the entire partition. <para>要获取的字节数，-1 表示整个分区。</para></param>
    public FastbootResponse Fetch(string partition, string outputPath, long offset = 0, long size = -1)
    {
        using var fs = File.Create(outputPath);
        return FetchToStream(partition, fs, offset, size);
    }

    /// <summary>
    /// Updates the super partition metadata: downloads the metadata image and sends
    /// update-super:&lt;partition&gt;[:wipe].
    /// <para>更新 super 分区元数据：下载元数据镜像并发送 update-super:&lt;分区&gt;[:wipe]。</para>
    /// </summary>
    public FastbootResponse UpdateSuper(string partition, Stream metadataImage, bool wipe = false, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(partition))
        {
            throw new ArgumentException("Partition name must not be empty.", nameof(partition));
        }

        DownloadData(metadataImage, metadataImage.Length, progress, cancellationToken).ThrowIfFailed();
        return ExecuteCommand(wipe
            ? FastbootConstants.CommandUpdateSuper + partition + ":wipe"
            : FastbootConstants.CommandUpdateSuper + partition);
    }

    /// <summary>
    /// Updates the super partition metadata from a metadata image file.
    /// <para>从元数据镜像文件更新 super 分区元数据。</para>
    /// </summary>
    public FastbootResponse UpdateSuper(string partition, string metadataImagePath, bool wipe = false, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.OpenRead(metadataImagePath);
        return UpdateSuper(partition, fs, wipe, progress, cancellationToken);
    }

    /// <summary>
    /// Creates a logical partition with the specified size. Requires userspace fastboot mode.
    /// <para>创建指定大小的逻辑分区。需要用户空间 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse CreateLogicalPartition(string partition, long size)
        => ExecuteCommand($"{FastbootConstants.CommandCreateLogicalPartition}{partition}:{size}");

    /// <summary>
    /// Deletes a logical partition. Requires userspace fastboot mode.
    /// <para>删除逻辑分区。需要用户空间 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse DeleteLogicalPartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandDeleteLogicalPartition + partition);

    /// <summary>
    /// Resizes a logical partition to the specified size. Requires userspace fastboot mode.
    /// <para>将逻辑分区调整为指定大小。需要用户空间 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse ResizeLogicalPartition(string partition, long size)
        => ExecuteCommand($"{FastbootConstants.CommandResizeLogicalPartition}{partition}:{size}");

    /// <summary>
    /// Wipes the super partition via the device-side wipe-super command.
    /// <para>通过设备侧 wipe-super 命令清除 super 分区。</para>
    /// </summary>
    public FastbootResponse WipeSuper(string partition)
        => ExecuteCommand(FastbootConstants.CommandWipeSuper + partition);

    /// <summary>
    /// Sends a flashing sub-command to the device (e.g. unlock, lock, get_unlock_ability).
    /// <para>向设备发送 flashing 子命令（如 unlock、lock、get_unlock_ability）。</para>
    /// </summary>
    public FastbootResponse FlashingCommand(string subCommand)
        => ExecuteCommand(FastbootConstants.CommandFlashing + subCommand);

    /// <summary>
    /// Gets the flashing unlock ability value. Returns 0 if unavailable or on failure.
    /// <para>获取 flashing 解锁能力值。不可用或失败时返回 0。</para>
    /// </summary>
    public int FlashingGetUnlockAbility()
    {
        var response = FlashingCommand("get_unlock_ability");
        return response.State == FastbootResponseState.Success &&
               int.TryParse(response.Info.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ability)
            ? ability
            : 0;
    }

    /// <summary>
    /// Returns whether the bootloader allows unlocking (get_unlock_ability == 1).
    /// <para>返回 bootloader 是否允许解锁（get_unlock_ability == 1）。</para>
    /// </summary>
    public bool IsUnlockAbilityEnabled() => FlashingGetUnlockAbility() == 1;

    /// <summary>
    /// Unlocks the device via flashing command.
    /// <para>通过 flashing 命令解锁设备。</para>
    /// </summary>
    public FastbootResponse FlashingUnlock() => FlashingCommand("unlock");

    /// <summary>
    /// Locks the device via flashing command.
    /// <para>通过 flashing 命令锁定设备。</para>
    /// </summary>
    public FastbootResponse FlashingLock() => FlashingCommand("lock");

    /// <summary>
    /// Unlocks critical partitions via flashing command.
    /// <para>通过 flashing 命令解锁关键分区。</para>
    /// </summary>
    public FastbootResponse FlashingUnlockCritical() => FlashingCommand("unlock_critical");

    /// <summary>
    /// Locks critical partitions via flashing command.
    /// <para>通过 flashing 命令锁定关键分区。</para>
    /// </summary>
    public FastbootResponse FlashingLockCritical() => FlashingCommand("lock_critical");

    /// <summary>
    /// Sends a GSI (Generic System Image) sub-command to the device.
    /// <para>向设备发送 GSI（通用系统镜像）子命令。</para>
    /// </summary>
    public FastbootResponse GsiCommand(string subCommand)
        => ExecuteCommand(FastbootConstants.CommandGsi + subCommand);

    /// <summary>
    /// Enables the GSI on the device.
    /// <para>启用设备上的 GSI。</para>
    /// </summary>
    public FastbootResponse GsiEnable() => GsiCommand("enable");

    /// <summary>
    /// Disables the GSI on the device.
    /// <para>禁用设备上的 GSI。</para>
    /// </summary>
    public FastbootResponse GsiDisable() => GsiCommand("disable");

    /// <summary>
    /// Queries the GSI installation status on the device.
    /// <para>查询设备上的 GSI 安装状态。</para>
    /// </summary>
    public FastbootResponse GsiStatus() => GsiCommand("status");

    /// <summary>
    /// Wipes the GSI installation on the device.
    /// <para>清除设备上的 GSI 安装。</para>
    /// </summary>
    public FastbootResponse GsiWipe() => GsiCommand("wipe");

    /// <summary>
    /// Performs a snapshot update action on the device. Common actions include "cancel" to
    /// abort an in-progress update. If the device requests a reboot to fastbootd to complete
    /// the action, a reboot-fastboot is issued.
    /// <para>在设备上执行快照更新操作。常见操作包括 "cancel" 以中止正在进行的更新。
    /// 如果设备请求重启到 fastbootd 以完成操作，则发送 reboot-fastboot。</para>
    /// </summary>
    public FastbootResponse SnapshotUpdate(string action = "cancel")
    {
        var response = ExecuteCommand(FastbootConstants.CommandSnapshotUpdate + action);
        var requestsReboot =
            response.Info.Contains("reboot fastboot", StringComparison.OrdinalIgnoreCase) ||
            response.Text.Contains("reboot fastboot", StringComparison.OrdinalIgnoreCase) ||
            response.InfoLines.Any(line => line.Contains("reboot fastboot", StringComparison.OrdinalIgnoreCase));
        if (requestsReboot)
        {
            Reboot("fastboot");
        }

        return response;
    }

    /// <summary>
    /// Sends an OEM-specific command to the device. INFO/TEXT frames emitted by the command
    /// are accumulated on the returned response (InfoLines/Text).
    /// <para>向设备发送 OEM 特定命令。命令产生的 INFO/TEXT 帧累积到返回响应的
    /// InfoLines/Text 中。</para>
    /// </summary>
    public FastbootResponse OemCommand(string oemCommand)
        => ExecuteCommand(FastbootConstants.CommandOem + oemCommand);

    /// <summary>
    /// Boots the device using a boot image from a stream: downloads the image and sends
    /// the boot command.
    /// <para>使用来自流的启动镜像启动设备：下载镜像并发送 boot 命令。</para>
    /// </summary>
    public FastbootResponse Boot(Stream stream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DownloadData(stream, length, progress, cancellationToken).ThrowIfFailed();
        return ExecuteCommand(FastbootConstants.CommandBoot);
    }

    /// <summary>
    /// Boots the device using the specified boot image data.
    /// <para>使用指定的启动镜像数据启动设备。</para>
    /// </summary>
    public FastbootResponse Boot(byte[] data, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(data);
        return Boot(ms, data.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Boots the device using a boot image file.
    /// <para>使用启动镜像文件启动设备。</para>
    /// </summary>
    public FastbootResponse BootFromFile(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.OpenRead(filePath);
        return Boot(fs, fs.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Shuts down the device.
    /// <para>关闭设备。</para>
    /// </summary>
    public FastbootResponse Shutdown()
        => ExecuteCommand(FastbootConstants.CommandShutdown);

    /// <summary>
    /// Flashes a partition with data from a stream.
    /// <para>用流中的数据刷写分区。</para>
    /// </summary>
    public FastbootResponse FlashPartition(string partition, Stream stream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default, bool resolveSlot = true)
    {
        ThrowIfDisposed();
        DownloadData(stream, length, progress, cancellationToken).ThrowIfFailed();
        return ExecuteCommand(FastbootConstants.CommandFlash + (resolveSlot ? ResolvePartitionWithSlot(partition) : partition));
    }

    /// <summary>
    /// Erases the specified partition.
    /// <para>擦除指定分区。</para>
    /// </summary>
    public FastbootResponse ErasePartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandErase + partition);

    /// <summary>
    /// Formats the specified partition.
    /// <para>格式化指定分区。</para>
    /// </summary>
    public FastbootResponse FormatPartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandFormat + partition);

    /// <summary>
    /// Reboots the device. Optionally into a target (recovery, bootloader, etc.).
    /// <para>重启设备，可指定目标（recovery、bootloader 等）。</para>
    /// </summary>
    public FastbootResponse Reboot(string? target = null)
        => ExecuteCommand(string.IsNullOrEmpty(target) ? FastbootConstants.CommandReboot : FastbootConstants.CommandReboot + "-" + target);

    /// <summary>
    /// Continues boot, exiting fastboot mode.
    /// <para>继续启动，退出 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse ContinueBoot()
        => ExecuteCommand(FastbootConstants.CommandContinue);

    /// <summary>
    /// Sets the active A/B slot.
    /// <para>设置活跃的 A/B 槽位。</para>
    /// </summary>
    public FastbootResponse SetActiveSlot(string slot)
        => ExecuteCommand(FastbootConstants.CommandSetActive + slot);

    /// <summary>
    /// Checks whether the specified partition has A/B slots.
    /// <para>检查指定分区是否具有 A/B 槽位。</para>
    /// </summary>
    public bool HasSlot(string partition)
    {
        if (string.IsNullOrEmpty(partition))
        {
            return false;
        }

        if (_hasSlotCache.TryGetValue(partition, out var has))
        {
            return has;
        }

        has = GetVariable(FastbootConstants.VarHasSlotPrefix + partition) == "yes";
        _hasSlotCache[partition] = has;
        return has;
    }

    /// <summary>
    /// Gets the current active slot suffix (e.g. "a" or "b").
    /// <para>获取当前活跃槽位后缀（如 "a" 或 "b"）。</para>
    /// </summary>
    public string GetCurrentSlot()
    {
        var slot = GetVariable(FastbootConstants.VarCurrentSlot);
        return slot.StartsWith('_') ? slot[1..] : slot;
    }

    /// <summary>
    /// Resolves the target partition name against A/B slots: slot-capable partitions that do
    /// not already carry a slot suffix get the current active slot appended, matching upstream
    /// fastboot. Non-slot partitions and already-slotted names are returned unchanged.
    /// <para>按 A/B 槽位解析目标分区名：支持槽位且尚未携带槽位后缀的分区追加当前活跃槽位，
    /// 与上游 fastboot 一致；非槽位分区与已带后缀的名称原样返回。</para>
    /// </summary>
    private string ResolvePartitionWithSlot(string partition)
    {
        if (string.IsNullOrEmpty(partition) || !HasSlot(partition) || HasSlotSuffix(partition))
        {
            return partition;
        }

        var suffix = GetCurrentSlot();
        return string.IsNullOrEmpty(suffix) ? partition : partition + "_" + suffix;
    }

    /// <summary>
    /// Determines whether a partition name already ends with an A/B slot suffix (e.g. "boot_a").
    /// <para>判断分区名是否已带 A/B 槽位后缀（如 "boot_a"）。</para>
    /// </summary>
    private static bool HasSlotSuffix(string partition)
    {
        var underscore = partition.LastIndexOf('_');
        if (underscore <= 0 || underscore == partition.Length - 1)
        {
            return false;
        }

        var suffix = partition[(underscore + 1)..];
        return suffix.Length == 1 && (suffix[0] == 'a' || suffix[0] == 'b');
    }

    /// <summary>
    /// Gets the size of the specified partition in bytes (decimal or 0x-prefixed hex).
    /// Returns 0 when the device does not report the partition size.
    /// <para>以字节获取指定分区大小（十进制或 0x 前缀十六进制）。设备未报告时返回 0。</para>
    /// </summary>
    public long GetPartitionSizeLong(string partition)
    {
        var value = GetVariable(FastbootConstants.VarPartitionSizePrefix + partition);
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(value, 16)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gets the maximum fetch size reported by the device (fastbootd max-fetch-size variable),
    /// or -1 when unavailable.
    /// <para>获取设备报告的最大 fetch 大小（fastbootd 的 max-fetch-size 变量），不可用时返回 -1。</para>
    /// </summary>
    private long GetMaxFetchSize()
    {
        var value = GetVariable(FastbootConstants.VarMaxFetchSize);
        if (string.IsNullOrEmpty(value))
        {
            return -1;
        }

        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : -1
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : -1;
    }

    private static readonly HashSet<string> VbmetaPartitionSet = new(
        ["vbmeta", "vbmeta_system", "vbmeta_vendor", "vbmeta_custom"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Queries a single device variable and returns a detailed result including error information.
    /// <para>按键查询单个设备变量并返回包含错误信息的详细结果。</para>
    /// </summary>
    /// <param name="key">The variable key. 变量名。</param>
    /// <param name="useCache">Whether to use the cached value. 是否使用缓存值。</param>
    /// <returns>A tuple of value, success flag, and error message. 值、成功标志与错误信息的三元组。</returns>
    public (string Value, bool Success, string? ErrorMessage) GetVarWithResult(string key, bool useCache = true)
    {
        if (useCache && _varCache.TryGetValue(key, out var cached))
        {
            return (cached, true, null);
        }

        var response = ExecuteCommand(FastbootConstants.CommandGetVar + key);
        if (response.State is FastbootResponseState.Fail)
        {
            return (string.Empty, false, response.Info);
        }

        if (response.State is FastbootResponseState.Timeout)
        {
            return (string.Empty, false, "Timeout waiting for device response");
        }

        if (useCache)
        {
            _varCache[key] = response.Info;
        }

        return (response.Info, true, null);
    }

    /// <summary>
    /// Gets the number of A/B slots on the device.
    /// <para>获取设备上的 A/B 槽位数量。</para>
    /// </summary>
    public int GetSlotCount()
        => int.TryParse(GetVariable(FastbootConstants.VarSlotCount), out var count) ? count : 1;

    /// <summary>
    /// Checks whether the specified partition exists on the device.
    /// <para>检查指定分区在设备上是否存在。</para>
    /// </summary>
    public bool PartitionExists(string partition)
        => TryGetVar(FastbootConstants.VarPartitionSizePrefix + partition, out var res)
           && !string.IsNullOrEmpty(res) && res != "0" && res != "0x0";

    /// <summary>
    /// Gets the raw partition size string reported by the device (e.g. "0x4000000").
    /// <para>获取设备上报的分区大小原始字符串（如 "0x4000000"）。</para>
    /// </summary>
    public string GetPartitionSize(string partition)
        => TryGetVar(FastbootConstants.VarPartitionSizePrefix + partition, out var v) ? v.Trim() : string.Empty;

    /// <summary>
    /// Gets the filesystem type of the specified partition.
    /// <para>获取指定分区的文件系统类型。</para>
    /// </summary>
    public string GetPartitionType(string partition)
        => TryGetVar(FastbootConstants.VarPartitionTypePrefix + partition, out var v) ? v.Trim() : string.Empty;

    /// <summary>
    /// Checks whether the device is in userspace fastboot mode (fastbootd).
    /// <para>检查设备是否处于用户空间 fastboot 模式（fastbootd）。</para>
    /// </summary>
    public bool IsUserspace()
    {
        try
        {
            return GetVariable(FastbootConstants.VarIsUserspace, useCache: false) == "yes";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether a partition is a logical partition on the device.
    /// <para>检查指定分区是否为设备上的逻辑分区。</para>
    /// </summary>
    public bool IsLogical(string partition)
        => TryGetVar(FastbootConstants.VarIsLogicalPrefix + partition, out var v) && v == "yes";

    /// <summary>
    /// Checks whether a partition is logical, using preloaded metadata if available,
    /// otherwise querying the device.
    /// <para>检查分区是否为逻辑分区，优先使用预加载的元数据，否则查询设备。</para>
    /// </summary>
    public bool IsLogicalOptimized(string partition)
        => _logicalPartitionsFromMetadata?.Contains(partition) ?? IsLogical(partition);

    /// <summary>
    /// Loads logical partition names from a super image metadata file for optimized flashing.
    /// <para>从 super 镜像元数据文件加载逻辑分区名称，用于优化刷写。</para>
    /// </summary>
    public void LoadLogicalPartitionsFromMetadata(string superImagePath)
    {
        if (!File.Exists(superImagePath))
        {
            _logicalPartitionsFromMetadata = null;
            return;
        }

        try
        {
            var partitions = new HashSet<string>(
                new SuperImageBuilder().GetLogicalPartitions(superImagePath)
                    .Select(p => p.Name)
                    .Where(n => !string.IsNullOrEmpty(n)),
                StringComparer.Ordinal);
            _logicalPartitionsFromMetadata = partitions;
        }
        catch
        {
            _logicalPartitionsFromMetadata = null;
        }
    }

    /// <summary>
    /// Retrieves staged data from the device and writes it to the specified stream.
    /// <para>从设备获取暂存数据并写入指定流。</para>
    /// </summary>
    public FastbootResponse GetStagedToStream(Stream output)
        => UploadData(FastbootConstants.CommandGetStaged, output);

    /// <summary>
    /// Retrieves staged data from the device and writes it to a file.
    /// <para>从设备获取暂存数据并写入文件。</para>
    /// </summary>
    public void GetStaged(string outputPath)
    {
        using var fs = File.Create(outputPath);
        GetStagedToStream(fs);
    }

    /// <summary>
    /// Waits for a snapshot merge operation to complete within the specified timeout.
    /// <para>在指定超时时间内等待快照合并操作完成。</para>
    /// </summary>
    public void WaitForSnapshotMerge(int timeoutSeconds = 600)
    {
        var start = Stopwatch.GetTimestamp();
        while ((Stopwatch.GetTimestamp() - start) < Stopwatch.Frequency * timeoutSeconds)
        {
            var res = GetVariable(FastbootConstants.VarSnapshotUpdateStatus);
            if (res == "merging")
            {
                Thread.Sleep(2000);
                continue;
            }

            if (res is "none" or "completed")
            {
                return;
            }

            break;
        }
    }

    /// <summary>
    /// Cancels a pending snapshot update if one is in progress.
    /// <para>如果正在进行快照更新，则取消挂起的快照更新。</para>
    /// </summary>
    public void CancelSnapshotIfNeeded()
    {
        try
        {
            var status = GetVariable(FastbootConstants.VarSnapshotUpdateStatus);
            if (!string.IsNullOrEmpty(status) && status != "none")
            {
                SnapshotUpdate("cancel");
            }
        }
        catch
        {
            // Ignore transient failures; cancellation is best-effort.
            // 忽略瞬时失败；取消是尽力而为。
        }
    }

    /// <summary>
    /// Checks whether the fastboot-info.txt version is supported (version ≤ 2).
    /// <para>检查 fastboot-info.txt 版本是否受支持（版本 ≤ 2）。</para>
    /// </summary>
    public bool CheckFastbootInfoRequirements(string version)
        => uint.TryParse(version, out var v) && v <= 2;

    /// <summary>
    /// Checks whether the specified partition name is a known vbmeta partition.
    /// <para>检查指定分区名称是否为已知的 vbmeta 分区。</para>
    /// </summary>
    public bool IsVbmetaPartition(string partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
        {
            return false;
        }

        var partName = partition;
        var underscoreIndex = partition.LastIndexOf('_');
        if (underscoreIndex > 0)
        {
            var suffix = partition[(underscoreIndex + 1)..];
            if (suffix.Equals("a", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals("b", StringComparison.OrdinalIgnoreCase))
            {
                partName = partition[..underscoreIndex];
            }
        }

        return VbmetaPartitionSet.Contains(partName);
    }

    /// <summary>
    /// Wipes user data by erasing and formatting userdata, cache, and metadata partitions.
    /// <para>通过擦除并格式化 userdata、cache 和 metadata 分区来清除用户数据。</para>
    /// </summary>
    public void WipeUserData()
    {
        foreach (var partition in new[] { "userdata", "cache", "metadata" })
        {
            try
            {
                var partitionType = GetPartitionType(partition);
                if (string.IsNullOrEmpty(partitionType))
                {
                    continue;
                }

                ErasePartition(partition);
                FormatPartition(partition);
            }
            catch
            {
                // Best-effort: a partition may not exist on all devices.
                // 尽力而为：某些设备可能不存在对应分区。
            }
        }
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