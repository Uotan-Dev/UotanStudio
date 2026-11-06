using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Writing;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class FlashJobProtocolTests
{
    [Fact]
    public void JobFormat_RoundTrip_PreservesPathsWithSpacesAndEscapes()
    {
        var drive = FlashTestData.CreateDrive("D:\\", 4096, mountPoints: ["D:\\", "E:\\mnt dir"]);
        var tricky = FlashTestData.CreateDrive(@"\\?\Volume{abc}", 8192);
        var job = new FlashJob
        {
            ImagePath = "C:\\Images\\my system.img",
            Drives = [drive, tricky],
            Options = new FlashJobOptions { VerifyAfterWrite = false, AllowLargeDrive = true },
        };

        var parsed = FlashJobFormat.Read(FlashJobFormat.Write(job));

        Assert.Equal(job.ImagePath, parsed.ImagePath);
        Assert.False(parsed.Options.VerifyAfterWrite);
        Assert.True(parsed.Options.AllowLargeDrive);
        Assert.Equal(2, parsed.Drives.Count);
        var target = parsed.Drives[0];
        Assert.Equal(drive.DevicePath, target.DevicePath);
        Assert.Equal(drive.DisplayName, target.DisplayName);
        Assert.Equal(drive.Size, target.Size);
        Assert.Equal(drive.MountPoints, target.MountPoints);
        Assert.Equal(tricky.DevicePath, parsed.Drives[1].DevicePath);
        Assert.Empty(parsed.Drives[1].MountPoints);
    }

    [Fact]
    public void JobFormat_PathWithNewline_SurvivesTheRoundTrip()
    {
        var drive = FlashTestData.CreateDrive("/dev/sda", 1024, mountPoints: ["odd\nmount"]);
        var job = new FlashJob
        {
            ImagePath = "a\nb.img",
            Drives = [drive],
            Options = new FlashJobOptions(),
        };

        var parsed = FlashJobFormat.Read(FlashJobFormat.Write(job));

        Assert.Equal(job.ImagePath, parsed.ImagePath);
        Assert.Equal(["odd\nmount"], parsed.Drives[0].MountPoints);
    }

    [Fact]
    public void JobFormat_WithoutHeader_IsRejected()
    {
        var exception = Assert.Throws<FormatException>(() =>
            FlashJobFormat.Read("image C:\\a.img\ntarget 0 /dev/sda sda 1024 1 0 0 x\n"));

        Assert.Contains("header", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgressFormat_RoundTrip_PreservesStageAndStatistics()
    {
        var progress = new FlashProgress
        {
            Stage = FlashStage.Verifying,
            BytesProcessed = 1024,
            TotalBytes = 4096,
            Percentage = 25,
            BytesPerSecond = 512.5,
            RemainingTime = TimeSpan.FromSeconds(6),
            TargetCount = 1,
        };

        var parsed = FlashProgressFormat.TryParse(FlashProgressFormat.WriteProgress(progress));

        Assert.NotNull(parsed);
        var value = Assert.IsType<FlashProgress>(parsed!.Progress);
        Assert.Equal(FlashStage.Verifying, value.Stage);
        Assert.Equal(1024, value.BytesProcessed);
        Assert.Equal(4096, value.TotalBytes);
        Assert.Equal(25, value.Percentage);
        Assert.Equal(1, value.TargetCount);
        Assert.Equal(TimeSpan.FromSeconds(6), value.RemainingTime);
    }

    [Fact]
    public void ProgressFormat_UnknownTotal_KeepsPercentageUndetermined()
    {
        var progress = new FlashProgress
        {
            Stage = FlashStage.Writing,
            BytesProcessed = 2048,
            BytesPerSecond = 0,
            TargetCount = 1,
        };

        var value = Assert.IsType<FlashProgress>(
            FlashProgressFormat.TryParse(FlashProgressFormat.WriteProgress(progress))!.Progress);

        Assert.Null(value.TotalBytes);
        Assert.Null(value.Percentage);
        Assert.Null(value.RemainingTime);
    }

    [Fact]
    public void ProgressFormat_AccessDeniedCause_IsReportedAsError()
    {
        var exception = new FlashAccessDeniedException(
            @"Windows could not open ""\\.\PhysicalDrive3"" for writing.",
            new UnauthorizedAccessException("Access is denied"));

        var parsed = FlashProgressFormat.TryParse(FlashProgressFormat.WriteError(exception));

        Assert.NotNull(parsed);
        Assert.True(parsed!.IsError);
        Assert.Equal(nameof(FlashAccessDeniedException), parsed.ErrorType);
        Assert.Equal(nameof(UnauthorizedAccessException), parsed.InnermostType);
        Assert.Contains("PhysicalDrive3", parsed.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressFormat_MultiLineMessage_StaysOnOneReport()
    {
        var line = FlashProgressFormat.WriteError(new IOException("line one\nline two"));

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);

        var parsed = FlashProgressFormat.TryParse(line);
        Assert.NotNull(parsed);
        Assert.Equal("line one\nline two", parsed!.ErrorMessage);
    }

    [Fact]
    public void AccessClassifier_CurrentPlatform_UsesThatPlatformsDenialCode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(FlashAccessClassifier.IsAccessDenied(FlashAccessClassifier.WindowsAccessDenied));
            Assert.False(FlashAccessClassifier.IsAccessDenied(2));
            return;
        }

        Assert.True(FlashAccessClassifier.IsAccessDenied(13));
        Assert.False(FlashAccessClassifier.IsAccessDenied(2));
    }
}