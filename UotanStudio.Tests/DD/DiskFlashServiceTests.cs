using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Writing;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class DiskFlashServiceTests
{
    [Fact]
    public async Task FlashAsync_WithoutPrivilegeAndDeclined_ReportsDeclinedAndWritesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only: the privileged-writer relaunch exists only there, so the PrivilegeMissing elevation flow never starts on other hosts.");
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 11);
        var imagePath = directory.WriteFile("system.img", data);
        var targetPath = directory.Combine("target.bin");
        var drive = FlashTestData.CreateDrive(targetPath, data.Length + 4096);
        var prompt = new RecordingPrompt(allow: false);
        var service = new DiskFlashService(
            new SingleDriveScanner(drive), new FixedProbe(isElevated: false, canWrite: false), prompt);

        var failure = await service.FlashAsync(
            imagePath, [drive], new FlashJobOptions(), onProgress: null, TestContext.Current.CancellationToken);

        Assert.NotNull(failure);
        Assert.Equal(FlashElevationReason.PrivilegeMissing, prompt.Reason);
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public async Task FlashAsync_WithPrivilege_WritesImageWithoutAskingForElevation()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(128 * 1024, 13);
        var imagePath = directory.WriteFile("system.img", data);
        var targetPath = directory.Combine("target.bin");
        var flasher = new ImageFlasher(FileDiskTargetFactory.Instance);
        var drive = FlashTestData.CreateDrive(targetPath, data.Length + 4096);
        var prompt = new RecordingPrompt(allow: false);
        var service = new DiskFlashService(
            new SingleDriveScanner(drive), new FixedProbe(isElevated: true, canWrite: true), prompt, flasher);

        var failure = await service.FlashAsync(
            imagePath, [drive], new FlashJobOptions(), onProgress: null, TestContext.Current.CancellationToken);

        Assert.Null(failure);
        Assert.Null(prompt.Reason);
        Assert.Equal(data, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanWriteDriveAsync_ProbeVerdict_IsPassedThrough()
    {
        using var directory = new FlashTestDirectory();
        var drive = FlashTestData.CreateDrive(directory.Combine("target.bin"), 4096);
        var service = new DiskFlashService(
            new SingleDriveScanner(drive), new FixedProbe(isElevated: false, canWrite: false), new RecordingPrompt(false));

        Assert.False(await service.CanWriteDriveAsync(drive, TestContext.Current.CancellationToken));
        Assert.False(service.HasWriteAccess);
    }

    private sealed class SingleDriveScanner(params FlashDrive[] drives) : IDriveScanner
    {
        public Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FlashDrive>>(drives);
    }

    private sealed class FixedProbe(bool isElevated, bool? canWrite) : IDiskWriteProbe
    {
        public bool IsElevated { get; } = isElevated;

        public Task<bool?> CanWriteAsync(FlashDrive drive, CancellationToken cancellationToken = default) =>
            Task.FromResult(canWrite);
    }

    private sealed class RecordingPrompt(bool allow) : IFlashElevationPrompt
    {
        internal FlashElevationReason? Reason { get; private set; }

        public Task<bool> ConfirmElevationAsync(FlashElevationReason reason, string detail)
        {
            Reason = reason;
            return Task.FromResult(allow);
        }
    }
}