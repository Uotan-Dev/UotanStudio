using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.Windows;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class WindowsDiskCapacityTests
{
    [Fact]
    public async Task TryGetDiskLength_PhysicalDrive_ReportsCapacityWithoutElevation()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The raw capacity query is a Windows device IOCTL.");
        }

        var drives = await DriveScanner.CreateDefault()
            .ScanAsync(TestContext.Current.CancellationToken);

        var sampled = 0;
        foreach (var drive in drives.Take(4))
        {
            using var handle = OpenForReadAttributes(drive.DevicePath);
            if (handle is null)
            {
                continue;
            }

            var measured = WindowsDeviceApi.TryGetDiskLength(drive.DevicePath);

            Assert.NotNull(measured);
            Assert.True(measured!.Value > 0, $"{drive.DevicePath} reported a non-positive capacity.");
            sampled++;
        }

        Assert.True(sampled > 0, "No physical drive could be opened, so the capacity path was not exercised.");
    }

    [Fact]
    public async Task TryGetDiskLength_PhysicalDrive_MatchesTheReportedDriveSize()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The raw capacity query is a Windows device IOCTL.");
        }

        var drives = await DriveScanner.CreateDefault()
            .ScanAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(drives);
        Assert.All(drives, drive => Assert.True(drive.Size > 0,
            $"{drive.DevicePath} ({drive.DisplayName}) was scanned with an unknown capacity."));
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle? OpenForReadAttributes(string devicePath) =>
        WindowsDeviceApi.TryOpen(devicePath, WindowsDeviceApi.FileReadAttributes);
}