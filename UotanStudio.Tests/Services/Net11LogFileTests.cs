using System.Text;
using System.Text.Json;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class Net11LogFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task WriteAsync_MultilineGolden_PreservesHeaderEscapingNullEnumAndColumns()
    {
        ILogLine[] entries =
        [
            new TestLogLine("quote \"value\" \\ 中文 <tag>\nnext", LogLevel.Warn,
                [new LogTextColumn("tag", 4, 8, IsPriority: true)]),
            new TestLogLine("plain", null, [])
        ];
        using var stream = new MemoryStream();

        await LogFileService.WriteAsync(stream, entries, TestContext.Current.CancellationToken);

        const string expected = """
            # UotanStudio Log 1
            {"text":"quote \"value\" \\ 中文 <tag>\nnext","level":"Warn","columns":[{"text":"tag","documentLength":4,"width":8,"isPriority":true,"trimMiddle":false}]}
            {"text":"plain","columns":[]}
            """ + "\n";
        var bytes = stream.ToArray();
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.True(stream.CanWrite);
        var path = _temp.WriteBytes("golden.log", bytes);

        var restored = await LogFileService.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(2, restored.Count);
        Assert.Equal(entries[0].Text, restored[0].Text);
        Assert.Equal(LogLevel.Warn, restored[0].Level);
        var column = Assert.Single(restored[0].Columns);
        Assert.Equal(new LogTextColumn("tag", 4, 8, IsPriority: true), column);
        Assert.Null(restored[1].Level);
    }

    [Fact]
    public async Task WriteAsync_PreCancelledWithEmptyEntries_ThrowsWithoutWriting()
    {
        using var stream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LogFileService.WriteAsync(stream, [], cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, stream.Length);
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task Append_UnicodeAndOversizedEntries_DoesNotThrowAndRoundTrips()
    {
        var sink = new LogFileSink(_temp.Path);
        sink.Append(new LogEntry(new DateTime(2026, 1, 2, 3, 4, 5, 6), LogLevel.Info, "source",
            "中文 \"quoted\"\nnext"));
        sink.Append(new LogEntry(new DateTime(2026, 1, 2, 3, 4, 7, 8), LogLevel.Error, "source",
            new string('x', LogFileService.MaximumEntryCharacters + 32)));
        sink.Flush();
        sink.Dispose();
        sink.Append(new LogEntry(DateTime.UnixEpoch, LogLevel.Fatal, "ignored", "ignored"));
        sink.Flush();
        sink.Dispose();

        var path = Assert.Single(Directory.GetFiles(_temp.Path, "*.log"));
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(3, lines.Length);
        Assert.Equal(LogFileService.Header, lines[0]);
        using var first = JsonDocument.Parse(lines[1]);
        Assert.Equal("2026-01-02 03:04:05.006 [Info] source - 中文 \"quoted\"\nnext",
            first.RootElement.GetProperty("text").GetString());
        Assert.Equal("Info", first.RootElement.GetProperty("level").GetString());
        using var second = JsonDocument.Parse(lines[2]);
        var text = second.RootElement.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Equal(LogFileService.MaximumEntryCharacters, text.Length);
        Assert.EndsWith(" ... [truncated]", text, StringComparison.Ordinal);
    }

    private sealed record TestLogLine(string Text, LogLevel? Level, IReadOnlyList<LogTextColumn> Columns) : ILogLine;
}