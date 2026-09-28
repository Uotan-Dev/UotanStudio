using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot.Images;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot;

public sealed partial class FastbootSession
{
    /// <summary>Upper bound on accumulated INFO lines; a flood beyond this fails the command.</summary>
    private const int MaxInfoLines = 10000;

    /// <summary>
    /// Sends a raw command string to the device (UTF-8 encoded, max 4096 bytes).
    /// <para>向设备发送原始命令字符串（UTF-8 编码，最大 4096 字节）。</para>
    /// </summary>
    /// <param name="command">Command text to encode and write as a single frame. <para>要编码并按单帧写入的命令文本。</para></param>
    /// <exception cref="ArgumentException">The UTF-8 encoding exceeds <see cref="FastbootConstants.MaxCommandLength"/> bytes.
    /// <para>UTF-8 编码超过 <see cref="FastbootConstants.MaxCommandLength"/> 字节。</para></exception>
    /// <exception cref="FastbootException">The transport accepted fewer bytes than the command length (short write).
    /// <para>传输接收的字节数少于命令长度（短写）。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
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
    /// Incoming bytes are accumulated at byte level (never re-decoded per packet, so a multi-byte
    /// UTF-8 sequence split across packets survives) into a session-held buffer. Frames are
    /// consumed from that buffer one at a time, and whatever the transport delivered past a
    /// terminal frame is retained for the next call instead of being silently dropped, so a
    /// trailing INFO after OKAY desynchronizes neither the stream nor the next command's reply.
    /// INFO lines and TEXT content are accumulated onto the returned response; the call returns
    /// only at a terminal OKAY/FAIL/DATA frame (or an unrecoverable unknown prefix), with the
    /// timeout deadline fixed from the command start, matching upstream fastboot.
    /// <para>入站字节按字节级累积到会话持有的缓冲区（绝不按包重复解码，跨包拆分的 UTF-8 多字节
    /// 序列得以保留），帧从该缓冲区逐个消费；传输在终止帧之后送达的字节会保留给下一次调用而非
    /// 被静默丢弃，因此 OKAY 之后的尾随 INFO 既不会破坏流，也不会错误归因到下一条命令的应答。
    /// INFO 行与 TEXT 内容累积到返回的响应对象上；仅在到达终止 OKAY/FAIL/DATA 帧（或出现不可恢复
    /// 的未知前缀）时返回。超时截止时间自命令开始固定不变，与上游一致。</para>
    /// </summary>
    /// <param name="timeoutMs">
    /// Overall receive deadline in milliseconds; when <see langword="null"/> the session timeout is used. The deadline is fixed at call start.
    /// <para>整体接收截止时间（毫秒）；为 <see langword="null"/> 时使用会话超时。截止时间在调用开始时即固定。</para>
    /// </param>
    /// <returns>The parsed response; <see cref="FastbootResponse.State"/> reports Success/Fail/Data/Timeout/Unknown.
    /// <para>解析后的响应；<see cref="FastbootResponse.State"/> 报告 Success/Fail/Data/Timeout/Unknown。</para></returns>
    public FastbootResponse ReceiveResponse(int? timeoutMs = null)
    {
        ThrowIfDisposed();

        var response = new FastbootResponse();
        var timeout = timeoutMs ?? _timeoutMs;
        var startTimestamp = Stopwatch.GetTimestamp();
        var timeoutTicks = (long)(Stopwatch.Frequency * (timeout / 1000.0));
        var textBytes = 0;
        StringBuilder? textBuffer = null;

        var buffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.ResponseBufferSize);
        try
        {
            while (true)
            {
                var offset = 0;
                while (true)
                {
                    var consumed = ConsumeFrame(_pendingBytes, offset, response, ref textBuffer, ref textBytes);
                    if (consumed == 0)
                    {
                        break;
                    }

                    offset += consumed;
                    if (response.State is FastbootResponseState.Success
                        or FastbootResponseState.Fail
                        or FastbootResponseState.Data
                        or FastbootResponseState.Unknown)
                    {
                        _pendingBytes.RemoveRange(0, offset);
                        return response;
                    }
                }

                if (offset > 0)
                {
                    _pendingBytes.RemoveRange(0, offset);
                }

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

                for (var i = 0; i < readLen; i++)
                {
                    _pendingBytes.Add(buffer[i]);
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
    /// Consumes one wire frame from the pending byte buffer starting at <paramref name="offset"/>.
    /// Returns 0 when more bytes are needed. Terminal frames (OKAY/FAIL/DATA) and an unknown prefix
    /// complete the response; INFO/TEXT frames are accumulated and parsing continues.
    /// <para>从待解析字节缓冲区的 <paramref name="offset"/> 处消费一帧。字节不足时返回 0。
    /// 终止帧（OKAY/FAIL/DATA）与未知前缀会完成响应；INFO/TEXT 帧被累积后继续解析。</para>
    /// </summary>
    /// <param name="pending">Session-held pending byte buffer. 会话持有的待解析字节缓冲区。</param>
    /// <param name="offset">Parse position inside <paramref name="pending"/>. <paramref name="pending"/> 内的解析位置。</param>
    /// <param name="response">Response being assembled. 正在组装的响应。</param>
    /// <param name="textBuffer">Accumulated TEXT content. 累积的 TEXT 内容。</param>
    /// <param name="textBytes">Total TEXT bytes accumulated so far. 目前累积的 TEXT 字节总数。</param>
    /// <returns>The number of bytes consumed by the frame, or 0 when more input is required.
    /// 该帧消费的字节数；需要更多输入时为 0。</returns>
    private static int ConsumeFrame(List<byte> pending, int offset, FastbootResponse response,
        ref StringBuilder? textBuffer, ref int textBytes)
    {
        var available = pending.Count - offset;
        if (available < 4)
        {
            return 0;
        }

        if (!IsKnownPrefixAt(pending, offset))
        {
            var junk = DecodeUtf8(pending, offset, pending.Count);
            response.State = FastbootResponseState.Unknown;
            response.Info = "device sent unknown status code: " + junk;
            response.Text = junk;
            return available;
        }

        if (IsPrefixAt(pending, offset, 'O', 'K', 'A', 'Y') || IsPrefixAt(pending, offset, 'F', 'A', 'I', 'L'))
        {
            var isOkay = pending[offset] == 'O';
            response.State = isOkay ? FastbootResponseState.Success : FastbootResponseState.Fail;
            var content = ExtractFrameContent(pending, offset + 4, out var frameEnd);
            response.Info = content;
            response.Text = textBuffer?.ToString() ?? content;
            return frameEnd - offset;
        }

        if (IsPrefixAt(pending, offset, 'I', 'N', 'F', 'O') || IsPrefixAt(pending, offset, 'T', 'E', 'X', 'T'))
        {
            var isInfo = pending[offset] == 'I';
            if (isInfo && response.InfoLines.Count >= MaxInfoLines)
            {
                response.State = FastbootResponseState.Fail;
                response.Info = "device sent too many INFO frames";
                response.Text = textBuffer?.ToString() ?? string.Empty;
                return available;
            }

            var contentStart = offset + 4;
            var endIdx = FindInfoTextEnd(pending, contentStart);
            if (endIdx < 0)
            {
                return 0;
            }

            var content = DecodeUtf8(pending, contentStart, endIdx);
            if (isInfo)
            {
                response.InfoLines.Add(content);
            }
            else
            {
                textBytes += endIdx - contentStart;
                if (textBytes > MaxTextBytes)
                {
                    response.State = FastbootResponseState.Fail;
                    response.Info = "device sent too much TEXT payload";
                    response.Text = textBuffer?.ToString() ?? string.Empty;
                    return available;
                }

                textBuffer ??= new StringBuilder();
                textBuffer.Append(content);
            }

            var next = endIdx;
            while (next < pending.Count && (pending[next] == '\0' || pending[next] == '\r' || pending[next] == '\n'))
            {
                next++;
            }

            return next - offset;
        }

        var dataEnd = FindDataEnd(pending, offset + 4);
        if (dataEnd < 0)
        {
            return 0;
        }

        var dataHex = DecodeUtf8(pending, offset + 4, dataEnd).Trim();
        var dataSize = ParseDataSizeHex(dataHex);
        if (dataSize < 0)
        {
            response.State = FastbootResponseState.Fail;
            response.Info = dataHex;
            response.Text = dataSize == -2
                ? $"data size too large {dataHex}"
                : "data size malformed: " + dataHex;
            return available;
        }

        response.State = FastbootResponseState.Data;
        response.DataSize = dataSize;
        response.Text = textBuffer?.ToString() ?? string.Empty;
        return dataEnd < pending.Count && pending[dataEnd] == '\0' ? dataEnd + 1 - offset : available;
    }

    /// <summary>
    /// Extracts the content of a terminal frame: everything up to the first NUL (a padded USB
    /// packet), or to the end of the buffer when no NUL follows (an exact TCP frame), with a single
    /// trailing NUL trimmed only in the latter case. <paramref name="frameEnd"/> reports the byte
    /// position just past the frame so any bytes beyond it stay in the pending buffer for the next
    /// response instead of being silently dropped.
    /// <para>提取终止帧的内容：到第一个 NUL 为止（USB 补齐包），无 NUL 时取到缓冲区末尾（TCP 精确帧），
    /// 后者仅去除一个尾随 NUL。<paramref name="frameEnd"/> 报告帧之后的字节位置，超出部分保留在
    /// 待解析缓冲区中留给下一个响应，而不是被静默丢弃。</para>
    /// </summary>
    private static string ExtractFrameContent(List<byte> pending, int start, out int frameEnd)
    {
        frameEnd = pending.Count;
        if (start >= pending.Count)
        {
            return string.Empty;
        }

        var nulIdx = pending.IndexOf((byte)'\0', start);
        if (nulIdx >= 0)
        {
            frameEnd = nulIdx + 1;
            return nulIdx > start ? DecodeUtf8(pending, start, nulIdx) : string.Empty;
        }

        return DecodeUtf8(pending, start, pending.Count);
    }

    /// <summary>
    /// Finds the end of the DATA size field. AOSP writes exactly 8 hex digits after the DATA
    /// prefix (optionally NUL-padded to the packet), so the field ends at the first NUL/CR/LF
    /// within the next 8 bytes, or exactly 8 digits once that many bytes are buffered; anything
    /// shorter means more bytes are still required.
    /// <para>查找 DATA 尺寸字段结尾。AOSP 在 DATA 前缀后固定写 8 个十六进制数字（可按包补 NUL），
    /// 因此字段结束于随后 8 字节内的首个 NUL/CR/LF，或凑满 8 个数字时恰为 8 字节处；
    /// 更短则说明还需要更多字节。</para>
    /// </summary>
    private static int FindDataEnd(List<byte> pending, int start)
    {
        var limit = Math.Min(pending.Count, start + 8);
        for (var i = start; i < limit; i++)
        {
            if (pending[i] == '\0' || pending[i] == '\r' || pending[i] == '\n')
            {
                return i;
            }
        }

        return pending.Count >= start + 8 ? start + 8 : -1;
    }

    /// <summary>
    /// Finds the end of an INFO/TEXT payload: the first NUL/CR/LF delimiter. When the frame carries
    /// no delimiter at all (a TCP frame without padding), the next status prefix terminates it;
    /// until one of the two is observed more bytes are required.
    /// <para>查找 INFO/TEXT 负载的结尾：第一个 NUL/CR/LF 分隔符。帧完全无分隔符时（无填充的
    /// TCP 帧），下一个状态前缀即为其结尾；两者都未出现前需要更多字节。</para>
    /// </summary>
    private static int FindInfoTextEnd(List<byte> pending, int contentStart)
    {
        var delimiterIdx = IndexOfAny(pending, ResponseDelimiters, contentStart);
        if (delimiterIdx >= 0)
        {
            return delimiterIdx;
        }

        for (var i = contentStart + 1; i <= pending.Count - 4; i++)
        {
            if (IsTerminalPrefixAt(pending, i) || IsPrefixAt(pending, i, 'I', 'N', 'F', 'O') || IsPrefixAt(pending, i, 'T', 'E', 'X', 'T'))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfAny(List<byte> pending, char[] delimiters, int start)
    {
        for (var i = start; i < pending.Count; i++)
        {
            foreach (var delimiter in delimiters)
            {
                if (pending[i] == delimiter)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string DecodeUtf8(List<byte> pending, int start, int end)
    {
        if (end <= start)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(pending.ToArray(), start, end - start);
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

    private static bool IsPrefixAt(List<byte> s, int index, char a, char b, char c, char d)
        => index + 4 <= s.Count &&
           s[index] == a &&
           s[index + 1] == b &&
           s[index + 2] == c &&
           s[index + 3] == d;

    private static bool IsKnownPrefixAt(List<byte> s, int index)
        => IsPrefixAt(s, index, 'O', 'K', 'A', 'Y') ||
           IsPrefixAt(s, index, 'F', 'A', 'I', 'L') ||
           IsPrefixAt(s, index, 'I', 'N', 'F', 'O') ||
           IsPrefixAt(s, index, 'T', 'E', 'X', 'T') ||
           IsPrefixAt(s, index, 'D', 'A', 'T', 'A');

    private static bool IsTerminalPrefixAt(List<byte> s, int index)
        => IsPrefixAt(s, index, 'O', 'K', 'A', 'Y') ||
           IsPrefixAt(s, index, 'F', 'A', 'I', 'L') ||
           IsPrefixAt(s, index, 'D', 'A', 'T', 'A');

}