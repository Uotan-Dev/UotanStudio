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

/// <summary>Reads and writes complete offline logs. 读取和写入完整的离线日志。</summary>
public static class LogFileService
{
    /// <summary>Version marker for lossless JSON Lines exports. 无损 JSON Lines 导出的版本标记。</summary>
    public const string Header = "# UotanStudio Log 1";
    /// <summary>Maximum input file size: 64 MiB. 最大输入文件大小：64 MiB。</summary>
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    /// <summary>Maximum number of offline entries, independent of live caches. 离线条目上限，与实时缓存独立。</summary>
    public const int MaximumEntries = 250000;
    internal const int MaximumEntryCharacters = 1024 * 1024;
    private static readonly AppSettingsJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });

    /// <summary>Recognizes logs from a bounded text-file header. 从有界文本文件头识别日志。</summary>
    public static bool LooksLikeLog(ReadOnlySpan<byte> header)
    {
        header = header[..Math.Min(header.Length, 8192)];
        var encoding = header.StartsWith(new byte[] { 0xFF, 0xFE }) ? Encoding.Unicode
            : header.StartsWith(new byte[] { 0xFE, 0xFF }) ? Encoding.BigEndianUnicode : Encoding.UTF8;
        using var reader = new StringReader(encoding.GetString(header).TrimStart('\uFEFF'));
        for (var i = 0; i < 16 && reader.ReadLine() is { } line; i++)
            if (line == Header || LogTextParser.Recognizes(line)) return true;
        return false;
    }

    /// <summary>Writes a snapshot without closing the caller's stream. 写入快照，不关闭调用方的流。</summary>
    public static async Task WriteAsync(Stream stream, IEnumerable<ILogLine> entries, CancellationToken cancellationToken = default)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(Header.AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = new LogFileRecord(entry.Text, entry.Level, entry.Columns.ToArray());
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonContext.LogFileRecord).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads exported logs, logcat text or project logs without live-cache eviction. 读取导出日志、logcat 或项目日志，不执行实时缓存淘汰。</summary>
    public static async Task<IReadOnlyList<ILogLine>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException(LanguageService.Instance.GetString("Log_FileTooLarge"));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var result = new List<ILogLine>();
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