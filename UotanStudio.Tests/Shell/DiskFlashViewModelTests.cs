using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;
using UotanStudio.DD.Writing;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.DD;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Shell;

public sealed class DiskFlashViewModelTests
{
    [Fact]
    public void SetImage_NonexistentFile_ReportsInvalidImage()
    {
        var vm = CreateViewModel(new RecordingDialogs());

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
        var vm = CreateViewModel(new RecordingDialogs(), drive);

        await vm.RefreshDrivesAsync();

        var item = Assert.Single(vm.Drives);
        Assert.True(item.IsBlocked);
        Assert.False(item.IsWritable);
        Assert.False(item.IsSelected);
        Assert.True(vm.HasDrives);
        Assert.False(vm.CanFlash);
    }

    [Fact]
    public async Task SelectDrive_SecondRow_ClearsTheFirstSelection()
    {
        using var directory = new FlashTestDirectory();
        var first = FlashTestData.CreateDrive(directory.Combine("first.bin"), 8L * 1024 * 1024);
        var second = FlashTestData.CreateDrive(directory.Combine("second.bin"), 8L * 1024 * 1024);
        var vm = CreateViewModel(new RecordingDialogs(), first, second);
        await vm.RefreshDrivesAsync();

        vm.SelectDrive(vm.Drives[0]);
        vm.SelectDrive(vm.Drives[1]);

        Assert.False(vm.Drives[0].IsSelected);
        Assert.True(vm.Drives[1].IsSelected);
        Assert.Equal(1, vm.SelectedCount);
        Assert.Same(vm.Drives[1], vm.SelectedItem);
    }

    [Fact]
    public async Task DriveRow_TickedDirectly_KeepsSelectionExclusive()
    {
        using var directory = new FlashTestDirectory();
        var first = FlashTestData.CreateDrive(directory.Combine("first.bin"), 8L * 1024 * 1024);
        var second = FlashTestData.CreateDrive(directory.Combine("second.bin"), 8L * 1024 * 1024);
        var vm = CreateViewModel(new RecordingDialogs(), first, second);
        await vm.RefreshDrivesAsync();

        vm.Drives[0].IsSelected = true;
        vm.Drives[1].IsSelected = true;

        Assert.False(vm.Drives[0].IsSelected);
        Assert.True(vm.Drives[1].IsSelected);
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task SelectDrive_BlockedRow_LeavesNothingSelected()
    {
        using var directory = new FlashTestDirectory();
        var blocked = FlashTestData.CreateDrive(directory.Combine("system.bin"), 8L * 1024 * 1024, isSystem: true);
        var vm = CreateViewModel(new RecordingDialogs(), blocked);
        await vm.RefreshDrivesAsync();

        vm.SelectDrive(vm.Drives[0]);

        Assert.False(vm.Drives[0].IsSelected);
        Assert.Null(vm.SelectedItem);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task CanFlash_RequiresImageSelectionAndConfirmation()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 17);
        var imagePath = directory.WriteFile("system.img", data);
        var drive = FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length + 4096);
        var vm = CreateViewModel(new RecordingDialogs(), drive);
        await vm.RefreshDrivesAsync();

        Assert.False(vm.CanFlash);

        vm.SetImage(imagePath);
        Assert.False(vm.CanFlash);

        vm.SelectDrive(vm.Drives[0]);
        Assert.False(vm.CanFlash);

        vm.ConfirmOverwrite = true;
        Assert.True(vm.CanFlash);

        vm.Drives[0].IsSelected = false;
        Assert.False(vm.CanFlash);
    }

    [Fact]
    public async Task FlashCommand_ExecutionState_FollowsTheSameGateAsTheButton()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 37);
        var imagePath = directory.WriteFile("system.img", data);
        var drive = FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length + 4096);
        var vm = CreateViewModel(new RecordingDialogs(), drive);
        await vm.RefreshDrivesAsync();

        Assert.False(vm.FlashCommand.CanExecute(null));

        vm.SetImage(imagePath);
        Assert.False(vm.FlashCommand.CanExecute(null));

        vm.SelectDrive(vm.Drives[0]);
        Assert.False(vm.FlashCommand.CanExecute(null));

        vm.ConfirmOverwrite = true;
        Assert.True(vm.FlashCommand.CanExecute(null));

        vm.Drives[0].IsSelected = false;
        Assert.False(vm.FlashCommand.CanExecute(null));
    }

    [Fact]
    public async Task FlashAsync_WithPrivilege_WritesSelectedTargetAndReportsSuccess()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(128 * 1024, 19);
        var imagePath = directory.WriteFile("system.img", data);
        var targetPath = directory.Combine("target.bin");
        var drive = FlashTestData.CreateDrive(targetPath, data.Length + 4096);
        var dialogs = new RecordingDialogs();
        var vm = CreateViewModel(dialogs, drive);
        vm.SetImage(imagePath);
        await vm.RefreshDrivesAsync();
        vm.SelectDrive(vm.Drives[0]);
        vm.ConfirmOverwrite = true;

        await vm.FlashAsync();

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(data, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FlashAsync_WithoutPrivilege_RaisesElevationPopupAndWritesNothing()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 23);
        var imagePath = directory.WriteFile("system.img", data);
        var targetPath = directory.Combine("target.bin");
        var drive = FlashTestData.CreateDrive(targetPath, data.Length + 4096);
        var dialogs = new RecordingDialogs();
        var prompt = new RecordingPrompt(allow: false);
        var vm = CreateViewModel(dialogs, prompt, isElevated: false, drive);
        vm.SetImage(imagePath);
        await vm.RefreshDrivesAsync();
        vm.SelectDrive(vm.Drives[0]);
        vm.ConfirmOverwrite = true;

        await vm.FlashAsync();

        Assert.Equal(FlashElevationReason.PrivilegeMissing, prompt.Reason);
        Assert.True(vm.StatusIsError);
        Assert.Single(dialogs.Errors);
        Assert.False(File.Exists(targetPath));
        Assert.False(vm.IsElevated);
    }

    [Fact]
    public async Task FlashAsync_RejectedWrite_ShowsErrorPopup()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 29);
        var imagePath = directory.WriteFile("system.img", data);
        var targetPath = directory.Combine("target.bin");
        var drive = FlashTestData.CreateDrive(targetPath, data.Length + 4096);
        var dialogs = new RecordingDialogs();
        var vm = CreateViewModel(dialogs, drive, flasher: new ImageFlasher(ThrowingDiskTargetFactory.Instance));
        vm.SetImage(imagePath);
        await vm.RefreshDrivesAsync();
        vm.SelectDrive(vm.Drives[0]);
        vm.ConfirmOverwrite = true;

        await vm.FlashAsync();

        Assert.True(vm.StatusIsError);
        Assert.Single(dialogs.Errors);
    }

    [Fact]
    public async Task AllowLargeDrive_LargeDrive_ChangesBlockedState()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 31);
        var imagePath = directory.WriteFile("system.img", data);
        var drive = FlashTestData.CreateDrive(directory.Combine("large.bin"), 200L * 1024 * 1024 * 1024);
        var vm = CreateViewModel(new RecordingDialogs(), drive);

        await vm.RefreshDrivesAsync();
        vm.SetImage(imagePath);

        Assert.True(Assert.Single(vm.Drives).IsBlocked);

        vm.AllowLargeDrive = true;

        var item = Assert.Single(vm.Drives);
        Assert.False(item.IsBlocked);
        Assert.False(vm.CanFlash);
    }

    private static DiskFlashViewModel CreateViewModel(
        RecordingDialogs dialogs,
        FlashDrive drive,
        ImageFlasher? flasher = null) =>
        CreateViewModel(dialogs, new RecordingPrompt(allow: false), isElevated: true, drive, flasher);

    private static DiskFlashViewModel CreateViewModel(
        RecordingDialogs dialogs,
        params FlashDrive[] drives) =>
        CreateViewModel(dialogs, new RecordingPrompt(allow: false), isElevated: true, drives);

    private static DiskFlashViewModel CreateViewModel(
        RecordingDialogs dialogs,
        RecordingPrompt prompt,
        bool isElevated,
        params FlashDrive[] drives) =>
        CreateViewModel(dialogs, prompt, isElevated, drives, flasher: null);

    private static DiskFlashViewModel CreateViewModel(
        RecordingDialogs dialogs,
        RecordingPrompt prompt,
        bool isElevated,
        FlashDrive drive,
        ImageFlasher? flasher = null) =>
        CreateViewModel(dialogs, prompt, isElevated, [drive], flasher);

    private static DiskFlashViewModel CreateViewModel(
        RecordingDialogs dialogs,
        RecordingPrompt prompt,
        bool isElevated,
        FlashDrive[] drives,
        ImageFlasher? flasher)
    {
        var service = new DiskFlashService(
            new StubDriveScanner(drives),
            new FixedProbe(isElevated),
            prompt,
            flasher ?? new ImageFlasher(FileDiskTargetFactory.Instance));
        return new DiskFlashViewModel(service, dialogs);
    }

    private sealed class StubDriveScanner : IDriveScanner
    {
        private readonly IReadOnlyList<FlashDrive> _drives;

        internal StubDriveScanner(IReadOnlyList<FlashDrive> drives) => _drives = drives;

        public Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_drives);
    }

    private sealed class FixedProbe(bool isElevated) : IDiskWriteProbe
    {
        public bool IsElevated { get; } = isElevated;

        public Task<bool?> CanWriteAsync(FlashDrive drive, CancellationToken cancellationToken = default) =>
            Task.FromResult<bool?>(IsElevated);
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

    private sealed class RecordingDialogs : IDiskFlashDialogs
    {
        internal List<string> Errors { get; } = [];

        internal List<string> Warnings { get; } = [];

        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add(message);
            return Task.CompletedTask;
        }

        public Task ShowWarningAsync(string title, string message)
        {
            Warnings.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDiskTargetFactory : IDiskTargetFactory
    {
        internal static ThrowingDiskTargetFactory Instance { get; } = new();

        public IDiskTarget Create(FlashDrive drive) => throw new FlashAccessDeniedException(
            "Windows could not open the target for writing.", new UnauthorizedAccessException("Access is denied"));
    }
}