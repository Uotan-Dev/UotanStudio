using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Writing;
using UotanStudio.Tests.DD;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Shell;

public sealed class DiskFlashViewModelTests
{
    [Fact]
    public void SetImage_NonexistentFile_ReportsInvalidImage()
    {
        var vm = new DiskFlashViewModel(new StubDriveScanner(), new ImageFlasher(FileDiskTargetFactory.Instance));

        vm.SetImage(Path.Combine(Path.GetTempPath(), "uotan-missing-image.img"));

        Assert.False(vm.HasImage);
        Assert.True(vm.StatusIsError);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.Empty(vm.Drives);
    }

    [Fact]
    public async Task RefreshDrivesAsync_SystemDrive_MarksRowBlocked()
    {
        using var directory = new FlashTestDirectory();
        var drive = FlashTestData.CreateDrive(directory.Combine("system.bin"), 1024L * 1024, isSystem: true);
        var vm = new DiskFlashViewModel(new StubDriveScanner(drive), new ImageFlasher(FileDiskTargetFactory.Instance));

        await vm.RefreshDrivesAsync();

        var item = Assert.Single(vm.Drives);
        Assert.True(item.IsBlocked);
        Assert.False(item.IsSelected);
        Assert.True(vm.HasDrives);
        Assert.False(vm.CanFlash);
    }

    [Fact]
    public async Task AllowLargeDrive_LargeDrive_ChangesBlockedState()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 31);
        var imagePath = directory.WriteFile("system.img", data);
        var drive = FlashTestData.CreateDrive(directory.Combine("large.bin"), 200L * 1024 * 1024 * 1024);
        var vm = new DiskFlashViewModel(new StubDriveScanner(drive), new ImageFlasher(FileDiskTargetFactory.Instance));

        await vm.RefreshDrivesAsync();
        vm.SetImage(imagePath);

        Assert.True(Assert.Single(vm.Drives).IsBlocked);

        vm.AllowLargeDrive = true;

        var item = Assert.Single(vm.Drives);
        Assert.False(item.IsBlocked);
        Assert.False(vm.CanFlash);
    }

    [Fact]
    public async Task FlashAsync_WithoutConfirmation_ReportsConfirmRequired()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 37);
        var imagePath = directory.WriteFile("system.img", data);
        var targetPath = directory.Combine("target.bin");
        var drive = FlashTestData.CreateDrive(targetPath, data.Length + 4096);
        var vm = new DiskFlashViewModel(new StubDriveScanner(drive), new ImageFlasher(FileDiskTargetFactory.Instance));

        await vm.RefreshDrivesAsync();
        vm.SetImage(imagePath);
        Assert.Single(vm.Drives).IsSelected = true;

        await vm.FlashAsync();

        Assert.True(vm.StatusIsError);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public async Task FlashAsync_Confirmed_WritesSelectedTargetAndReportsSuccess()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(128 * 1024, 41);
        var imagePath = directory.WriteFile("system.img", data);
        var targetPath = directory.Combine("target.bin");
        var drive = FlashTestData.CreateDrive(targetPath, data.Length + 4096);
        var vm = new DiskFlashViewModel(new StubDriveScanner(drive), new ImageFlasher(FileDiskTargetFactory.Instance))
        {
            ConfirmOverwrite = true,
        };

        vm.SetImage(imagePath);
        await vm.RefreshDrivesAsync();
        Assert.Single(vm.Drives).IsSelected = true;

        await vm.FlashAsync();
        Assert.False(vm.StatusIsError);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.Equal(data, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
        Assert.Equal(1, vm.SelectedCount);
    }

    private sealed class StubDriveScanner : IDriveScanner
    {
        private readonly IReadOnlyList<FlashDrive> _drives;

        internal StubDriveScanner(params FlashDrive[] drives)
        {
            _drives = drives;
        }

        public Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_drives);
    }
}