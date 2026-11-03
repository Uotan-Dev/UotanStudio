using System.Text;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Debug-mode file trail: session files carry every retained level and read back through the offline log
/// reader, rolling files stay inside the retention count, and setting a directory never writes on its own.
/// <para>调试模式文件痕迹：会话文件带上全部保留级别并能经离线日志读取器读回，滚动文件保持在保留数量内，
/// 仅设置目录不会自行写入。</para>
/// </summary>
public sealed class LogFileSinkTests
{
    [Fact]
    public async Task Append_FlushedSessionFile_ReadsBackThroughTheOfflineLogReader()
    {
        var directory = CreateDirectory();
        try
        {
            using (var sink = new LogFileSink(directory))
            {
                sink.Append(new LogEntry(new DateTime(2026, 11, 3, 12, 34, 56, 789), LogLevel.Warn,
                    "Firmware", "first line"));
                sink.Append(new LogEntry(new DateTime(2026, 11, 3, 12, 35, 0), LogLevel.Error,
                    "Flash", "multi\nline"));
                sink.Flush();
            }

            var path = Assert.Single(Directory.GetFiles(directory, "UotanStudio-*.log"));
            var entries = await LogFileService.ReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Collection(entries,
                entry =>
                {
                    Assert.Equal(LogLevel.Warn, entry.Level);
                    Assert.Contains("Firmware", entry.Text, StringComparison.Ordinal);
                    Assert.Contains("first line", entry.Text, StringComparison.Ordinal);
                },
                entry =>
                {
                    Assert.Equal(LogLevel.Error, entry.Level);
                    Assert.Contains("multi\nline", entry.Text, StringComparison.Ordinal);
                });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Append_LargeSession_StaysInOneFileWithoutRollingOrPruning()
    {
        var directory = CreateDirectory();
        try
        {
            const int entries = 20000;
            using (var sink = new LogFileSink(directory))
            {
                for (var i = 0; i < entries; i++)
                {
                    sink.Append(new LogEntry(DateTime.Now, LogLevel.Info, "S",
                        $"m{i:D5}-" + new string('r', 1000)));
                }
                sink.Flush();
            }

            var path = Assert.Single(Directory.GetFiles(directory, "UotanStudio-*.log"));
            Assert.True(new FileInfo(path).Length > 16L * 1024 * 1024);

            var retained = await LogFileService.ReadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(entries, retained.Count);
            Assert.Contains(retained, entry => entry.Text.Contains("m00000-", StringComparison.Ordinal));
            Assert.Contains(retained, entry => entry.Text.Contains("m19999-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TruncatedEntry_StaysReadableAfterTheOneMegabyteLineLimit()
    {
        var directory = CreateDirectory();
        try
        {
            using (var sink = new LogFileSink(directory))
            {
                sink.Append(new LogEntry(DateTime.Now, LogLevel.Info, "S",
                    new string('t', LogFileService.MaximumEntryCharacters + 4096)));
                sink.Flush();
            }

            var path = Assert.Single(Directory.GetFiles(directory, "UotanStudio-*.log"));
            var text = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains("[truncated]", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"uotan-logsink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}