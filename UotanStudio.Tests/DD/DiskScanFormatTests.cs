using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Writing;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class DiskScanFormatTests
{
    [Fact]
    public void ScanFormat_RoundTrip_PreservesDrivesMountPointsAndIssues()
    {
        var removable = FlashTestData.CreateDrive("\\\\.\\PhysicalDrive3", 15_646_851_072, mountPoints: ["F:\\"]);
        var internalDrive = FlashTestData.CreateDrive("\\\\.\\PhysicalDrive0", 2_048_408_248_320, isSystem: true);
        var report = new DiskScanReport
        {
            Drives = [removable, internalDrive],
            Issues = [@"no capacity for \.PhysicalDrive4", @"secondsissue"],
        };

        var parsed = DiskScanFormat.Read(DiskScanFormat.Write(report), out var isComplete);

        Assert.True(isComplete);
        Assert.Equal(2, parsed.Drives.Count);
        Assert.Equal(removable.DevicePath, parsed.Drives[0].DevicePath);
        Assert.Equal(removable.Size, parsed.Drives[0].Size);
        Assert.Equal(["F:\\"], parsed.Drives[0].MountPoints);
        Assert.True(parsed.Drives[1].IsSystem);
        Assert.Equal(2, parsed.Issues.Count);
        Assert.Contains("no capacity", parsed.Issues[0], StringComparison.Ordinal);
        Assert.Equal(@"secondsissue", parsed.Issues[1]);
    }

    [Fact]
    public void ScanFormat_TruncatedReport_IsReportedAsIncomplete()
    {
        var full = DiskScanFormat.Write(new DiskScanReport
        {
            Drives = [FlashTestData.CreateDrive("/dev/sda", 1024)],
        });

        var truncated = full[..full.LastIndexOf(DiskScanFormat.End, StringComparison.Ordinal)];
        var parsed = DiskScanFormat.Read(truncated, out var isComplete);

        Assert.False(isComplete);
        Assert.Single(parsed.Drives);
    }

    [Fact]
    public void ScanFormat_WithoutHeader_IsRejected()
    {
        Assert.Throws<FormatException>(() =>
            DiskScanFormat.Read("drive 0 /dev/sda sda 1024 1 0 0 -\nend\n", out _));
    }

    [Fact]
    public void TextCodec_RoundTripsSpacesBackslashesAndNewlines()
    {
        const string value = "C:\\Program Files\\odd path\nsecond line";

        var escaped = FlashTextCodec.Escape(value);

        Assert.DoesNotContain(" ", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", escaped, StringComparison.Ordinal);
        Assert.Equal(value, FlashTextCodec.Unescape(escaped));
    }

    [Fact]
    public void TextCodec_EmptyValue_UsesTheSentinel()
    {
        Assert.Equal(FlashTextCodec.EmptyValue, FlashTextCodec.Escape(string.Empty));
        Assert.Equal(string.Empty, FlashTextCodec.Unescape(FlashTextCodec.EmptyValue));
    }
}