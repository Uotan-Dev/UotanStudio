using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Syslog;

/// <summary>
/// One decoded syslog line from the device.
/// <para>从设备解码出的一行 syslog。</para>
/// </summary>
/// <param name="Timestamp">The parsed local timestamp, or <see langword="null"/> when the line does not carry one. <para>解析出的时间戳；行内没有时为 <see langword="null"/>。</para></param>
/// <param name="Process">The emitting process name. <para>发出进程名。</para></param>
/// <param name="ProcessId">The emitting process id, or <see langword="null"/> when absent. <para>发出进程 ID；缺失时为 <see langword="null"/>。</para></param>
/// <param name="Level">The reported log level, for example <c>Notice</c>. <para>上报的日志级别，例如 <c>Notice</c>。</para></param>
/// <param name="Message">The message text after the prefix. <para>前缀之后的消息文本。</para></param>
/// <param name="RawText">The original line. <para>原始行文本。</para></param>
public sealed record SyslogLine(
    DateTime? Timestamp,
    string? Process,
    int? ProcessId,
    string? Level,
    string Message,
    string RawText);

/// <summary>
/// Parses the line-oriented syslog format produced by the <c>syslog_relay</c> service
/// (for example <c>2026-10-26 06:00:00.123456 +0800 Process[123:456] &lt;Notice&gt;: text</c>).
/// <para>解析 <c>syslog_relay</c> 服务产生的行格式 syslog（例如
/// <c>2026-10-26 06:00:00.123456 +0800 Process[123:456] &lt;Notice&gt;: text</c>）。</para>
/// </summary>
public static class SyslogLineParser
{
    /// <summary>
    /// Parses one raw line into a <see cref="SyslogLine"/>; unparsable lines round-trip as raw text.
    /// <para>把一行原始文本解析为 <see cref="SyslogLine"/>；无法解析的行按原文往返。</para>
    /// </summary>
    /// <param name="line">The raw line without line ending. <para>不含换行符的原始行。</para></param>
    /// <returns>The parsed line. <para>解析结果。</para></returns>
    public static SyslogLine Parse(string line)
    {
        var text = line.Length > 0 && line[0] < ' ' ? line.TrimStart((char)0, '\x14', '\x11', '\x12', '\x13') : line;
        var parts = text.Split(' ', 4, StringSplitOptions.TrimEntries);
        if (parts.Length >= 4 && DateTime.TryParse(parts[0] + ' ' + parts[1],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var timestamp))
        {
            var rest = parts[3];
            var bracketStart = rest.IndexOf('[');
            var bracketEnd = rest.IndexOf(']', bracketStart + 1);
            var levelStart = rest.IndexOf('<');
            var levelEnd = rest.IndexOf('>', levelStart + 1);
            var messageSeparator = rest.IndexOf(':');
            if (bracketStart > 0 && bracketEnd > bracketStart && levelStart > bracketEnd &&
                levelEnd > levelStart && messageSeparator > levelEnd)
            {
                var process = rest[..bracketStart];
                var pidText = rest[(bracketStart + 1)..bracketEnd];
                var pidSeparator = pidText.IndexOf(':');
                var level = rest[(levelStart + 1)..levelEnd];
                var message = rest[(messageSeparator + 1)..].TrimStart();
                return new SyslogLine(
                    timestamp,
                    process,
                    int.TryParse(pidSeparator > 0 ? pidText[..pidSeparator] : pidText,
                        out var pid) ? pid : null,
                    level,
                    message,
                    line);
            }
        }

        return new SyslogLine(null, null, null, null, text, line);
    }
}

/// <summary>
/// Pumps lines from the syslog relay stream until the stream ends or cancellation is requested.
/// <para>从中继流按行泵取，直到流结束或请求取消。</para>
/// </summary>
public sealed class SyslogRelayClient
{
    /// <summary>Gets the maximum accepted line length; longer lines are truncated, never unbounded.
    /// <para>获取单行长度上限；超长行被截断而不是无界增长。</para></summary>
    public const int MaxLineBytes = 1 << 20;

    /// <summary>
    /// Reads lines from the connected relay stream and reports each one; returns when the stream ends.
    /// <para>从已连接的中继流读取行并逐条上报；流结束时返回。</para>
    /// </summary>
    /// <param name="stream">The TLS-ready relay stream. <para>就绪 TLS 的中继流。</para></param>
    /// <param name="onLine">The callback for every decoded line; may run on the pumping thread. <para>每行的回调；可能在中继线程上运行。</para></param>
    /// <param name="cancellationToken">Cancels the pump. <para>取消泵取。</para></param>
    /// <exception cref="IOException">The stream fails mid-line. <para>流中途失败时抛出。</para></exception>
    public void Pump(Stream stream, Action<SyslogLine> onLine, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var line = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = stream.ReadAsync(buffer, cancellationToken).AsTask().GetAwaiter().GetResult();
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var octet = buffer[i];
                if (octet is (byte)'\n' or (byte)'\r')
                {
                    if (line.Length > 0)
                    {
                        var text = Encoding.UTF8.GetString(line.ToArray());
                        line.SetLength(0);
                        onLine(SyslogLineParser.Parse(text));
                    }

                    continue;
                }

                if (line.Length >= MaxLineBytes)
                {
                    continue;
                }

                line.WriteByte(octet);
            }
        }
    }
}