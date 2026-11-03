using UotanStudio.Comm.Diagnostics;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Disk trail contract: configuring a directory starts writing immediately, below-minimum entries still reach the
/// file, and disabling the directory stops further records. Debug mode does not start or stop the trail.
/// <para>磁盘痕迹契约：配置目录后立即开始写入，低于最低级别的条目同样进入文件，取消目录后停止写入；调试模式不启动
/// 或停止文件接收端。</para>
/// </summary>
[Collection("Log pipeline")]
public sealed class LogServiceFileLoggingTests
{
    [Fact]
    public async Task ConfigureFileLogging_WritesEntriesBelowTheStoreMinimum()
    {
        var directory = CreateDirectory();
        var service = new LogService();
        try
        {
            service.SetMinimumLevel(LogLevel.Info);
            service.ConfigureFileLogging(directory);
            service.Write(LogLevel.Debug, "FileSink", "below-minimum");
            service.Write(LogLevel.Info, "FileSink", "info-line");
            service.FlushFileLog();
            service.ConfigureFileLogging(null);

            Assert.DoesNotContain(service.Entries, entry => entry.Message == "below-minimum");

            var path = Assert.Single(Directory.GetFiles(directory, "UotanStudio-*.log"));
            var entries = await LogFileService.ReadAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(entries, entry => entry.Text.Contains("below-minimum", StringComparison.Ordinal));
            Assert.Contains(entries, entry => entry.Text.Contains("info-line", StringComparison.Ordinal));
        }
        finally
        {
            service.ConfigureFileLogging(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigureFileLogging_Null_StopsFurtherWrites()
    {
        var directory = CreateDirectory();
        var service = new LogService();
        try
        {
            service.ConfigureFileLogging(directory);
            service.Write(LogLevel.Info, "FileSink", "kept");
            service.FlushFileLog();
            service.ConfigureFileLogging(null);
            service.Write(LogLevel.Info, "FileSink", "dropped");
            service.FlushFileLog();

            var path = Assert.Single(Directory.GetFiles(directory, "UotanStudio-*.log"));
            var entries = await LogFileService.ReadAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(entries, entry => entry.Text.Contains("kept", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, entry => entry.Text.Contains("dropped", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyDebugMode_DoesNotStopOrRestartTheFileTrail()
    {
        var directory = CreateDirectory();
        var service = new LogService();
        var commLevel = CommTrace.Level;
        var payloads = CommTrace.CaptureRawPayloads;
        var usbEnabled = UsbTrace.IsEnabled;
        var frames = UsbTrace.CaptureFrames;
        try
        {
            service.ConfigureFileLogging(directory);
            service.Write(LogLevel.Info, "FileSink", "before-debug");
            service.ApplyDebugMode(debug: true);
            service.Write(LogLevel.Info, "FileSink", "during-debug");
            service.ApplyDebugMode(debug: false);
            service.Write(LogLevel.Info, "FileSink", "after-debug");
            service.FlushFileLog();
            service.ConfigureFileLogging(null);

            var path = Assert.Single(Directory.GetFiles(directory, "UotanStudio-*.log"));
            var entries = await LogFileService.ReadAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(entries, entry => entry.Text.Contains("before-debug", StringComparison.Ordinal));
            Assert.Contains(entries, entry => entry.Text.Contains("during-debug", StringComparison.Ordinal));
            Assert.Contains(entries, entry => entry.Text.Contains("after-debug", StringComparison.Ordinal));
        }
        finally
        {
            CommTrace.Level = commLevel;
            CommTrace.CaptureRawPayloads = payloads;
            UsbTrace.IsEnabled = usbEnabled;
            UsbTrace.CaptureFrames = frames;
            service.ConfigureFileLogging(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"uotan-filelog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}