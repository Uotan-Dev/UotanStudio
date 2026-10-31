using UotanStudio.Comm.Diagnostics;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Logging;
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
            await File.WriteAllBytesAsync(loader, [0x52, 0x4B, 0x42, 0x4F, 0x4F, 0x54],
                TestContext.Current.CancellationToken);
            RockchipConfigFile.Save(config, RockchipConfigFile.CreateHeader(),
            [
                new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loader, Selected = true },
                new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true }
            ]);
            var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

            await model.ImportRkConfigAsync(config);

            Assert.Equal(loader, model.EffectiveRkLoaderPath);

            File.Delete(loader);
            await model.ImportRkConfigAsync(config);

            Assert.Null(model.EffectiveRkLoaderPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
            Assert.True(bootRow.CanSelect);
            Assert.Null(bootRow.Step);
            Assert.Equal("loader", model.Partitions[0].Command);
            Assert.Equal(loader, model.Partitions[0].FilePath);
            Assert.True(model.Partitions[0].CanSelect);
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