using UotanStudio.Services;
using UotanStudio.Services.Logging;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Workflow.Execution;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Optional entry metadata: a scope and structured fields survive the store, reach the session file and the log
/// window, and a scoped facade stamps them without changing the recorded text.
/// <para>可选条目元数据：作用域与结构化字段经存储保留，进入会话文件与日志窗口；带作用域的门面在不改变记录文本的前提下
/// 附加它们。</para>
/// </summary>
public sealed class LogEntryMetadataTests
{
    [Fact]
    public void Write_WithScopeAndFields_RecordsBothOnTheEntry()
    {
        var service = new LogService();
        LogField[] fields = [new("attempt", "2"), new("region", "cn")];

        service.Write(LogLevel.Warn, "Flash", "retrying", "stage-clean", fields);

        var entry = Assert.Single(service.Entries);
        Assert.Equal("stage-clean", entry.Scope);
        Assert.Equal(fields, entry.Fields);
    }

    [Fact]
    public void Write_WithBlankScopeAndEmptyFields_RecordsNull()
    {
        var service = new LogService();

        service.Write(LogLevel.Info, "S", "m", "   ", []);

        var entry = Assert.Single(service.Entries);
        Assert.Null(entry.Scope);
        Assert.Null(entry.Fields);
    }

    [Fact]
    public void Write_WithFields_CopiesTheCallerList()
    {
        var service = new LogService();
        List<LogField> fields = [new("k", "v1")];

        service.Write(LogLevel.Info, "S", "m", null, fields);
        fields[0] = new LogField("k", "v2");

        Assert.Equal("v1", Assert.Single(service.Entries).Fields![0].Value);
    }

    [Fact]
    public void Write_WithFields_ChargesTheRetentionBudget()
    {
        var service = new LogService { MaxQueueSize = int.MaxValue, MaxQueueCharacters = 64 * 1024 };
        var payload = new string('f', 4096);
        var padding = new string('x', 4096);

        for (var i = 0; i < 10; i++)
        {
            service.Write(LogLevel.Info, "S", $"m{i}:{padding}", null, [new LogField("payload", payload)]);
        }

        Assert.True(service.Entries.Count < 10);
        Assert.DoesNotContain(service.Entries, entry => entry.Message.StartsWith("m0:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileSink_RendersScopeAndFieldsBesideTheMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"uotan-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using (var sink = new LogFileSink(directory))
            {
                sink.Append(new LogEntry(DateTime.Now, LogLevel.Info, "Flash", "line", "stage-1",
                    [new LogField("attempt", "2")]));
                sink.Flush();
            }

            var path = Assert.Single(Directory.GetFiles(directory, "UotanStudio-*.log"));
            var entries = await LogFileService.ReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("Flash (stage-1) - line [attempt=2]", Assert.Single(entries).Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ViewModel_ShowsScopeAndFieldsInTheDisplayedLine()
    {
        var service = new LogService();
        using var model = new LogViewModel(service, maxEntries: 10, maxCharacters: long.MaxValue);

        service.Write(LogLevel.Info, "Flash", "line", "stage-1", [new LogField("attempt", "2")]);
        model.FlushPending();

        Assert.Contains("Flash (stage-1) - line [attempt=2]", Assert.Single(model.Entries).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScopedLog_StampsTheScopeOnPlainAndInterpolatedWrites()
    {
        var service = new LogService();
        IUnifiedLog scoped = new ScopedUnifiedLog(service, "stage-1");

        scoped.Write(LogLevel.Info, "Engine", "plain");
        scoped.Write(LogLevel.Info, "Engine", $"node {7}");

        var entries = service.Entries;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("stage-1", entry.Scope));
        Assert.Equal("node 7", entries[1].Message);
    }

    [Fact]
    public async Task NodeExecutionEngine_WithAScope_StampsEveryEntry()
    {
        var service = new LogService();
        var engine = new NodeExecutionEngine(service, "stage-1");

        await engine.ExecuteAsync([], [], TestContext.Current.CancellationToken);

        Assert.NotEmpty(service.Entries);
        Assert.All(service.Entries, entry => Assert.Equal("stage-1", entry.Scope));
    }
}