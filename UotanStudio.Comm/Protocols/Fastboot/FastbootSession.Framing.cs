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
    /// Loops over the accumulated pending buffer, recognizing OKAY/FAIL/INFO/TEXT/DATA
    /// prefixes at each frame boundary. INFO lines and TEXT content are accumulated on the
    /// returned response; the call only returns when a terminal OKAY/FAIL/DATA frame (or an
    /// unrecoverable unknown prefix) is reached. The timeout deadline is fixed from the
    /// command start, matching upstream fastboot.
    /// <para>循环扫描累积的待解析缓冲区，在每个帧边界识别 OKAY/FAIL/INFO/TEXT/DATA 前缀。
    /// INFO 行与 TEXT 内容累积到返回的响应对象上；仅在到达终止 OKAY/FAIL/DATA 帧
    /// （或出现不可恢复的未知前缀）时返回。超时截止时间自命令开始固定不变，与上游一致。</para>
    /// </summary>
    /// <param name="timeoutMs">
    /// Overall receive deadline in milliseconds; when <see langword="null"/> the session timeout is used. The deadline is fixed at call start.
    /// <para>整体接收截止时间（毫秒）；为 <see langword="null"/> 时使用会话超时。截止时间在调用开始时即固定。</para>
    /// </param>
    /// <returns>The parsed response; <see cref="FastbootResponse.State"/> reports Success/Fail/Data/Timeout/Unknown.
    /// <para>解析后的响应；<see cref="FastbootResponse.State"/> 报告 Success/Fail/Data/Timeout/Unknown。</para>
    /// </returns>
    public FastbootResponse ReceiveResponse(int? timeoutMs = null)
    {
        ThrowIfDisposed();

        var response = new FastbootResponse();
        var timeout = timeoutMs ?? _timeoutMs;
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
                        if (isInfo && response.InfoLines.Count >= MaxInfoLines)
                        {
                            response.State = FastbootResponseState.Fail;
                            response.Info = "device sent too many INFO frames";
                            response.Text = textBuffer?.ToString() ?? string.Empty;
                            return response;
                        }

                        var contentStart = pendingOffset + 4;
                        var endIdx = FindInfoTextEnd(pending, contentStart);
                        if (endIdx < 0)
                        {
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
                        continue;
                    }

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
            if (IsTerminalPrefixAt(s, i))
            {
                return i;
            }

            var isInfoAt = IsPrefixAt(s, i, 'I', 'N', 'F', 'O');
            var isTextAt = IsPrefixAt(s, i, 'T', 'E', 'X', 'T');
            if ((isInfoAt || isTextAt) && firstTerminalIdx > i)
            {
                return i;
            }
        }

        return -1;
    }

}