using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Logging;

internal sealed record LogFileRecord(string Text,
    [property: JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))] LogLevel? Level,
    LogTextColumn[] Columns) : ILogLine
{
    IReadOnlyList<LogTextColumn> ILogLine.Columns => Columns;
}

/// <summary>Reads and writes complete offline logs.<para>读取和写入完整的离线日志。</para></summary>
public static class LogFileService
{
    /// <summary>Version marker for lossless JSON Lines exports.<para>无损 JSON Lines 导出的版本标记。</para></summary>
    public const string Header = "# UotanStudio Log 1";
    /// <summary>Maximum input file size: 64 MiB.<para>最大输入文件大小：64 MiB。</para></summary>
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    /// <summary>Maximum number of offline entries, independent of live caches.<para>离线条目上限，与实时缓存相互独立。</para></summary>
    public const int MaximumEntries = 250000;
    internal const int MaximumEntryCharacters = 1024 * 1024;
    private static readonly AppSettingsJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
    internal static readonly byte[] HeaderBytes = Encoding.UTF8.GetBytes(Header);
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    internal static JsonWriterOptions JsonWriterOptions => WriterOptions;

    /// <summary>Recognizes logs from a bounded text-file header.<para>从有界文本文件头识别日志。</para></summary>
    /// <param name="header">Leading bytes of the file.<para>文件起始字节。</para></param>
    /// <returns>True when the text looks like a supported log.<para>文本属于受支持的日志时为 true。</para></returns>
    public static bool LooksLikeLog(ReadOnlySpan<byte> header)
    {
        header = header[..Math.Min(header.Length, 8192)];
        var encoding = header is [0xFF, 0xFE, ..] ? Encoding.Unicode
            : header is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode : Encoding.UTF8;
        using var reader = new StringReader(encoding.GetString(header).TrimStart('\uFEFF'));
        for (var i = 0; i < 16 && reader.ReadLine() is { } line; i++)
            if (line == Header || LogTextParser.Recognizes(line)) return true;
        return false;
    }

    /// <summary>Writes a snapshot without closing the stream it was handed.<para>写入快照且不关闭传入的流。</para></summary>
    /// <param name="stream">Destination stream left open afterwards.<para>写完后保持打开的目标流。</para></param>
    /// <param name="entries">Entries serialized as JSON Lines.<para>按 JSON Lines 序列化的条目。</para></param>
    /// <param name="cancellationToken">Token cancelling the write.<para>取消写入的令牌。</para></param>
    public static async Task WriteAsync(Stream stream, IEnumerable<ILogLine> entries, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await stream.WriteAsync(HeaderBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(LineBreak, cancellationToken).ConfigureAwait(false);
        var buffer = new ArrayBufferWriter<byte>(1024);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var written = SerializeRecord(buffer, entry);
            await stream.WriteAsync(written, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(LineBreak, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes one record into the reusable buffer and returns exactly the bytes it wrote.
    /// <para>把一条记录序列化进可复用缓冲区，并返回它恰好写出的字节。</para>
    /// </summary>
    /// <remarks>
    /// Each record gets its own writer over a buffer this method owns. Sharing one <see cref="Utf8JsonWriter"/>
    /// across records and then interleaving raw span writes with its own <see cref="IBufferWriter{T}"/> calls
    /// leaves the writer's pending bytes and the sink's buffered bytes out of step, which showed up as duplicated
    /// records and NUL-filled gaps on large entries. A per-record writer has no such shared state.
    /// <para>每条记录都在本方法自有的缓冲区上新建写入器。跨记录共享一个 <see cref="Utf8JsonWriter"/>，
    /// 再把它自己的 <see cref="IBufferWriter{T}"/> 调用与裸 span 写入交织，会让写入器的待写字节与接收端的缓冲
    /// 字节错位；大条目上表现为记录重复和 NUL 空洞。每条记录一个写入器就没有这种共享状态。</para>
    /// </remarks>
    internal static ReadOnlyMemory<byte> SerializeRecord(ArrayBufferWriter<byte> buffer, ILogLine line)
    {
        buffer.Clear();
        using (var json = new Utf8JsonWriter(buffer, JsonWriterOptions))
        {
            Serialize(json, line);
        }

        return buffer.WrittenMemory;
    }

    /// <summary>Serializes one entry as a single JSON Line record.<para>把一条条目序列化为一行 JSON 记录。</para></summary>
    /// <param name="line">Entry to serialize.<para>要序列化的条目。</para></param>
    /// <returns>The JSON text without a trailing newline.<para>不含末尾换行的 JSON 文本。</para></returns>
    internal static string Serialize(ILogLine line)
    {
        var record = new LogFileRecord(line.Text, line.Level, [.. line.Columns]);
        return JsonSerializer.Serialize(record, JsonContext.LogFileRecord);
    }

    internal static void Serialize(Utf8JsonWriter writer, ILogLine line)
    {
        var record = new LogFileRecord(line.Text, line.Level, [.. line.Columns]);
        JsonSerializer.Serialize(writer, record, JsonContext.LogFileRecord);
    }

    /// <summary>The JSON Lines record separator: a single LF, independent of the platform.
    /// <para>JSON Lines 的记录分隔符：单个 LF，与平台无关。</para></summary>
    internal static ReadOnlyMemory<byte> LineBreak { get; } = "\n"u8.ToArray();

    /// <summary>Reads exported logs, logcat text or project logs without live-cache eviction.<para>读取导出日志、logcat 或项目日志，不执行实时缓存淘汰。</para></summary>
    /// <param name="path">File to read.<para>要读取的文件。</para></param>
    /// <param name="cancellationToken">Token cancelling the read.<para>取消读取的令牌。</para></param>
    /// <returns>Parsed log lines in file order.<para>按文件顺序解析出的日志行。</para></returns>
    public static async Task<IReadOnlyList<ILogLine>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException(LanguageService.Instance.GetString("Log_FileTooLarge"));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        List<ILogLine> result = [];
        var parser = new LogTextParser();
        var firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var exported = firstLine == Header;
        if (firstLine is not null && firstLine.StartsWith("# UotanStudio Log ", StringComparison.Ordinal) && !exported)
            throw new InvalidDataException(LanguageService.Instance.GetString("Log_FileUnsupportedVersion"));
        var lineNumber = 0;
        for (var line = exported ? await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) : firstLine;
             line is not null; line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (line.Length > (exported ? 12 * MaximumEntryCharacters : MaximumEntryCharacters) || lineNumber > MaximumEntries)
                throw new InvalidDataException(LanguageService.Instance.GetString("Log_FileTooLarge"));
            if (line.Contains('\0'))
                throw new InvalidDataException(LanguageService.Instance.GetString("Log_FileInvalidRecord", lineNumber));
            if (exported)
            {
                try
                {
                    var record = JsonSerializer.Deserialize(line, JsonContext.LogFileRecord);
                    if (record is null || !IsValid(record)) throw new JsonException();
                    result.Add(record);
                }
                catch (JsonException)
                {
                    throw new InvalidDataException(LanguageService.Instance.GetString("Log_FileInvalidRecord", lineNumber + 1));
                }
            }
            else parser.Add(line, result);
        }
        if (!exported) parser.Finish(result);
        return result;
    }

    private static bool IsValid(LogFileRecord record)
    {
        if (record.Text is null || record.Text.Length > MaximumEntryCharacters || record.Columns is null
            || record.Columns.Length > 8 || (record.Level is { } level && !Enum.IsDefined(level))) return false;
        var remaining = record.Text.AsSpan().IndexOfAny('\r', '\n');
        if (remaining < 0) remaining = record.Text.Length;
        foreach (var column in record.Columns)
        {
            if (column.Text is null || column.Text.Length > MaximumEntryCharacters || column.Text.Contains('\n')
                || column.Text.Contains('\r') || column.DocumentLength < 1 || column.DocumentLength > remaining
                || column.Width is < 1 or > 256) return false;
            remaining -= column.DocumentLength;
        }
        return true;
    }
}