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
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(Header.AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = new LogFileRecord(entry.Text, entry.Level, [.. entry.Columns]);
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonContext.LogFileRecord).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

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