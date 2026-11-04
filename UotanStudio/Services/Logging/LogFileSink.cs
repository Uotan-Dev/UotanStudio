using System.Globalization;
using System.Text;

namespace UotanStudio.Services.Logging;

/// <summary>
/// Session log sink: appends every written entry as a JSON Lines record to one session file that grows without
/// rolling, so a run leaves a complete, openable trail on disk.
/// <para>会话日志接收端：把每条写入的条目按 JSON Lines 追加到单个会话文件，文件不滚动，使一次运行在磁盘上留下完整、
/// 可直接打开的踪迹。</para>
/// </summary>
internal sealed class LogFileSink : IDisposable
{
    private const int FlushThresholdBytes = 64 * 1024;
    private const string TruncationMarker = " ... [truncated]";

    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private long _fileBytes;
    private long _flushedBytes;
    private bool _disposed;

    /// <summary>
    /// Creates the sink and opens the session file for appending records.
    /// <para>创建接收端并打开会话文件以追加记录。</para>
    /// </summary>
    /// <param name="directory">Directory receiving the session file. <para>接收会话文件的目录。</para></param>
    internal LogFileSink(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var prefix = "UotanStudio-"
            + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, prefix + ".log");
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        _writer.WriteLine(LogFileService.Header);
        _fileBytes = Encoding.UTF8.GetByteCount(LogFileService.Header) + 1;
    }

    /// <summary>
    /// Appends one entry; failures disable the sink instead of propagating into the logging call.
    /// <para>追加一条条目；失败时关闭接收端，而不是把异常抛回日志调用。</para>
    /// </summary>
    /// <param name="entry">The entry to append. <para>要追加的条目。</para></param>
    internal void Append(LogEntry entry)
    {
        lock (_gate)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            try
            {
                var text = Format(entry);
                if (text.Length > LogFileService.MaximumEntryCharacters)
                {
                    text = string.Concat(text.AsSpan(0, LogFileService.MaximumEntryCharacters - TruncationMarker.Length),
                        TruncationMarker);
                }

                var json = LogFileService.Serialize(new LogFileRecord(text, entry.Level, []));
                _writer.WriteLine(json);
                _fileBytes += Encoding.UTF8.GetByteCount(json) + 1;
                if (_fileBytes - _flushedBytes >= FlushThresholdBytes)
                {
                    _writer.Flush();
                    _flushedBytes = _fileBytes;
                }
            }
            catch
            {
                _disposed = true;
                DisposeWriterLocked();
            }
        }
    }

    /// <summary>
    /// Flushes buffered records to the operating system; a no-op after the sink was disabled.
    /// <para>把缓冲记录冲刷到操作系统；接收端已关闭时为空操作。</para>
    /// </summary>
    internal void Flush()
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _flushedBytes = _fileBytes;
            }
            catch
            {
                _disposed = true;
                DisposeWriterLocked();
            }
        }
    }

    /// <summary>Flushes and closes the session file.<para>冲刷并关闭会话文件。</para></summary>
    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
            }

            DisposeWriterLocked();
            _disposed = true;
        }
    }

    private static string Format(LogEntry entry) =>
        $"{entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} "
        + $"[{entry.Level}] {entry.Source}{LogMetadataFormat.ScopeSuffix(entry.Scope)} - {entry.Message}"
        + LogMetadataFormat.FieldSuffix(entry.Fields);

    private void DisposeWriterLocked()
    {
        var writer = _writer;
        _writer = null;
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Dispose();
        }
        catch
        {
        }
    }
}