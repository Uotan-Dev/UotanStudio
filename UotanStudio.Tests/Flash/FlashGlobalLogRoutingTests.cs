using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Hash.RockchipCrc32;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashGlobalLogRoutingTests
{
    [Fact]
    public void ApplyDebugMode_TogglesCommDiagnosticsWithTheHostSetting()
    {
        var level = CommTrace.Level;
        var payloads = CommTrace.CaptureRawPayloads;
        var usbEnabled = UsbTrace.IsEnabled;
        var frames = UsbTrace.CaptureFrames;
        try
        {
            LogService.Instance.ApplyDebugMode(debug: true);
            Assert.Equal(CommLogLevel.Trace, CommTrace.Level);
            Assert.True(CommTrace.CaptureRawPayloads);
            Assert.True(UsbTrace.IsEnabled);
            Assert.True(UsbTrace.CaptureFrames);

            LogService.Instance.ApplyDebugMode(debug: false);
            Assert.Equal(CommLogLevel.Off, CommTrace.Level);
            Assert.False(CommTrace.CaptureRawPayloads);
            Assert.False(UsbTrace.IsEnabled);
            Assert.False(UsbTrace.CaptureFrames);
        }
        finally
        {
            CommTrace.Level = level;
            CommTrace.CaptureRawPayloads = payloads;
            UsbTrace.IsEnabled = usbEnabled;
            UsbTrace.CaptureFrames = frames;
            LogService.Instance.ApplyDebugMode(debug: false);
        }
    }

    [Fact]
    public async Task ImportRkConfigAsync_LoaderRow_IsEffectiveOnlyWhileItsFileExists()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"uotan-flash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var loader = Path.Combine(directory, "MiniLoaderAll.bin");
        var config = Path.Combine(directory, "download.cfg");
        try
        {
            await File.WriteAllBytesAsync(loader, BuildMinimalLoader(), TestContext.Current.CancellationToken);
            RockchipConfigFile.Save(config, RockchipConfigFile.CreateHeader(),
            [
                new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loader, Selected = true },
                new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true }
            ]);
            var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

            await model.ImportRkConfigAsync(config);

            await model.SpecialFileRowCheck;
            Assert.Equal(loader, model.EffectiveRkLoaderPath);

            File.Delete(loader);
            await model.ImportRkConfigAsync(config);

            await model.SpecialFileRowCheck;
            Assert.Null(model.EffectiveRkLoaderPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <remarks>
    /// The row check reads that file in the background; waiting for it before the finally deletes the directory keeps
    /// the cleanup from racing an open handle.
    /// <para>行检查在后台读取该文件；等它结束再让 finally 删除目录，清理才不会撞上仍被打开的文件句柄。</para>
    /// </remarks>
    [Fact]
    public async Task ImportRkConfigAsync_ConfigurationRowPointingAtAnotherConfiguration_StaysOutOfTheGrid()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"uotan-flash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var loader = Path.Combine(directory, "MiniLoaderAll.bin");
        var boot = Path.Combine(directory, "boot.img");
        var nested = Path.Combine(directory, "download.cfg");
        var config = Path.Combine(directory, "rock-5b-emmc.cfg");
        try
        {
            await File.WriteAllBytesAsync(loader, [0x4C, 0x44, 0x52, 0x20],
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(boot, [0x41, 0x4E, 0x44, 0x52],
                TestContext.Current.CancellationToken);
            RockchipConfigFile.Save(nested, RockchipConfigFile.CreateHeader(), []);
            RockchipConfigFile.Save(config, RockchipConfigFile.CreateHeader(),
            [
                new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loader, Selected = true },
                new RockchipConfigEntry { Name = "boot", Path = boot, Selected = true },
                new RockchipConfigEntry { Name = "rootfs", Path = nested, Selected = true }
            ]);
            var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

            await model.ImportRkConfigAsync(config);

            Assert.Equal(["Loader", "boot"], model.Partitions.Select(row => row.Name));
            Assert.Equal(model.Partitions, model.VisiblePartitions);
            var bootRow = Assert.Single(model.Partitions, row => row.Name == "boot");
            Assert.Equal("flash", bootRow.Command);
            Assert.Null(bootRow.Step);
            Assert.Equal("loader", model.Partitions[0].Command);
            Assert.Equal(loader, model.Partitions[0].FilePath);

            await model.SpecialFileRowCheck;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AppendLogAndAppendError_ReachTheGlobalLogAtTheirOwnLevel()
    {
        var log = new RecordingUnifiedLog();
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, log);

        model.AppendLog("loading package");
        model.AppendError("FileNotFoundException: the loader is missing");

        Assert.Collection(log.Entries,
            entry =>
            {
                Assert.Equal(LogLevel.Trace, entry.Level);
                Assert.Equal("Flash", entry.Source);
                Assert.Equal("loading package", entry.Message);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Equal("Flash", entry.Source);
                Assert.Contains("the loader is missing", entry.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void AppendLog_WhileTheGlobalStoreFiltersBelowInfo_StillPublishesTheLineToThePage()
    {
        var service = LogService.Instance;
        try
        {
            service.ApplyDebugMode(debug: false);
            var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, service);
            var marker = "page-only-" + Guid.NewGuid().ToString("N");

            model.AppendLog(marker);
            model.FlushPendingLog();

            Assert.Contains(marker, model.LogText, StringComparison.Ordinal);
        }
        finally
        {
            service.ApplyDebugMode(debug: false);
        }
    }

    /// <summary>
    /// Builds the smallest image the Rockchip loader handler accepts: a self-consistent header with no entry tables and
    /// a matching trailing checksum, so a loader row can be proven effective rather than merely pointing at a file.
    /// <para>构造瑞芯微 loader 处理器能接受的最小镜像：自洽头部、三张条目表都为空、末尾校验和相符，使 loader 行可以被
    /// 证实有效，而不只是指向某个文件。</para>
    /// </summary>
    private static byte[] BuildMinimalLoader()
    {
        const int headerSize = 102;
        var image = new byte[headerSize + 4];
        Encoding.ASCII.GetBytes("BOOT").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(6), 0x010B);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4),
            RockchipCrc32.Compute(image.AsSpan(0, image.Length - 4)));
        return image;
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