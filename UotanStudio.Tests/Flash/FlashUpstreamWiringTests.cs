using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Firmware;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Wiring tests for the upstream flash-page features: the log viewer's severity markers and the
/// generate-flash-plan option.
/// <para>上游刷机页功能接线测试：日志查看器的严重级别标记与生成烧录方案选项。</para>
/// </summary>
public sealed class FlashUpstreamWiringTests
{
    [Fact]
    public void AppendLog_PrefixesEveryLineWithItsSeverityMarker()
    {
        var unified = new RecordingUnifiedLog();
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, unified);
        model.AppendLog("plain line");
        model.AppendError("boom line");
        model.FlushPendingLog();

        // The page log viewer colors a line by the marker its text carries, so the buffer must tag every line.
        // 页面日志查看器按行内标记着色，因此缓冲必须为每一行打上级别标记。
        Assert.Contains("[Info] plain line", model.LogText, StringComparison.Ordinal);
        Assert.Contains("[Error] boom line", model.LogText, StringComparison.Ordinal);

        // The default info level is what the unified log records without the debug switch, so the line is
        // automatically registered into the global log window.
        // 默认 info 级别是统一日志在未开调试开关时就会记录的级别，因此该行会自动注册进全局日志窗口。
        Assert.Contains(unified.Entries, entry => entry.Level == LogLevel.Info
            && entry.Source == "Flash" && entry.Message == "plain line");
        Assert.Contains(unified.Entries, entry => entry.Level == LogLevel.Error
            && entry.Source == "Flash" && entry.Message == "boom line");
    }

    [Fact]
    public async Task LoadPackageAsync_GenerateFlashPlan_WritesThePlanBesideThePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var operation = new InstallOperation { Type = InstallOperation.Types.Type.Replace };
            operation.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
            var manifest = new DeltaArchiveManifest { BlockSize = 4 };
            manifest.Partitions.Add(new PartitionUpdate { PartitionName = "boot" });
            manifest.Partitions[0].Operations.Add(operation);
            var payload = Path.Combine(root, "payload.bin");
            await File.WriteAllBytesAsync(payload, PayloadDeltaTests.BuildPayload(manifest,
                new Dictionary<InstallOperation, byte[]> { [operation] = "boot"u8.ToArray() }),
                TestContext.Current.CancellationToken);

            var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());
            model.GenerateFlashPlan = true;

            await model.LoadPackageAsync(payload);
            model.FlushPendingLog();

            Assert.Contains(model.Partitions, row => row.Name == "boot");
            var plan = Assert.Single(Directory.GetDirectories(root, "payload.bin-fastbootd-*"));
            Assert.True(File.Exists(Path.Combine(plan, "flash_fastbootd.txt")));
            Assert.True(File.Exists(Path.Combine(plan, "images", "boot.img")));
            Assert.Contains("Flash plan generated:", model.LogText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadPackageAsync_GenerateFlashPlanUnchecked_GeneratesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-plan-off-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var operation = new InstallOperation { Type = InstallOperation.Types.Type.Replace };
            operation.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
            var manifest = new DeltaArchiveManifest { BlockSize = 4 };
            manifest.Partitions.Add(new PartitionUpdate { PartitionName = "boot" });
            manifest.Partitions[0].Operations.Add(operation);
            var payload = Path.Combine(root, "payload.bin");
            await File.WriteAllBytesAsync(payload, PayloadDeltaTests.BuildPayload(manifest,
                new Dictionary<InstallOperation, byte[]> { [operation] = "boot"u8.ToArray() }),
                TestContext.Current.CancellationToken);

            var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

            await model.LoadPackageAsync(payload);
            model.FlushPendingLog();

            Assert.Contains(model.Partitions, row => row.Name == "boot");
            Assert.Empty(Directory.GetDirectories(root, "payload.bin-fastbootd-*"));
            Assert.DoesNotContain("Flash plan generated:", model.LogText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingUnifiedLog : IUnifiedLog
    {
        public List<(LogLevel Level, string Source, string Message)> Entries { get; } = [];

        public void Write(LogLevel level, string source, string message)
            => Entries.Add((level, source, message));

        public void Write(LogLevel level, string source, string format, params object[] args)
            => Write(level, source, string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));

        public void SetMinimumLevel(LogLevel level)
        {
        }
    }
}