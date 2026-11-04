using UotanStudio.Compress;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;
using Xunit;

namespace UotanStudio.Tests.DD;

public class DriveCompatibilityEvaluatorTests
{
    [Fact]
    public void Evaluate_WriteProtectedDrive_ReturnsLockedError()
    {
        var image = CreateImage(1024, 1024);
        var drive = FlashTestData.CreateDrive("target.bin", 4096, isReadOnly: true);

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);

        var issue = Assert.Single(issues);
        Assert.Equal(DriveCompatibilityIssue.Locked, issue.Issue);
        Assert.Equal(DriveCompatibilitySeverity.Error, issue.Severity);
    }

    [Fact]
    public void Evaluate_ReadOnlyDriveWithoutWriting_DoesNotReportLocked()
    {
        var image = CreateImage(1024, 1024);
        var drive = FlashTestData.CreateDrive("target.bin", 4096, isReadOnly: true);

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: false);

        Assert.Empty(issues);
    }

    [Fact]
    public void Evaluate_TooSmallDrive_ReturnsOnlyTooSmallError()
    {
        var image = CreateImage(8192, 8192);
        var drive = FlashTestData.CreateDrive("target.bin", 4096, isSystem: true);

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);

        var issue = Assert.Single(issues);
        Assert.Equal(DriveCompatibilityIssue.TooSmall, issue.Issue);
        Assert.Equal(DriveCompatibilitySeverity.Error, issue.Severity);
    }

    [Fact]
    public void Evaluate_SystemDrive_ReturnsSystemWarning()
    {
        var image = CreateImage(1024, 1024);
        var drive = FlashTestData.CreateDrive("target.bin", 4096, isSystem: true);

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);

        var issue = Assert.Single(issues);
        Assert.Equal(DriveCompatibilityIssue.SystemDrive, issue.Issue);
        Assert.Equal(DriveCompatibilitySeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Evaluate_LargeDrive_ReturnsLargeDriveWarning()
    {
        var image = CreateImage(1024, 1024);
        var drive = FlashTestData.CreateDrive("target.bin", DriveCompatibilityEvaluator.LargeDriveThresholdBytes + 1);

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);

        var issue = Assert.Single(issues);
        Assert.Equal(DriveCompatibilityIssue.LargeDrive, issue.Issue);
        Assert.Equal(DriveCompatibilitySeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Evaluate_SourceDrive_ReturnsContainsImageError()
    {
        using var directory = new FlashTestDirectory();
        var image = CreateImage(1024, 1024, directory.Combine("system.img"));
        var drive = FlashTestData.CreateDrive(
            directory.Combine("target.bin"),
            4096,
            mountPoints: [directory.Root]);

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);

        var issue = Assert.Single(issues);
        Assert.Equal(DriveCompatibilityIssue.ContainsImage, issue.Issue);
        Assert.Equal(DriveCompatibilitySeverity.Error, issue.Severity);
    }

    [Fact]
    public void Evaluate_NullImage_SkipsCapacityAndSourceChecks()
    {
        var drive = FlashTestData.CreateDrive("target.bin", 1024);

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image: null, writing: true);

        Assert.Empty(issues);
    }

    [Fact]
    public void IsDriveLargeEnough_EstimatedImage_ComparesCompressedSize()
    {
        var image = new FlashImage
        {
            Path = Path.Combine(Path.GetTempPath(), "system.img.gz"),
            DisplayName = "system.img.gz",
            Format = new ImageFormat(ImageContainerKind.Compressed, CompressionFormat.Gzip),
            CompressedSize = 8192,
            Size = null,
        };
        var tooSmall = FlashTestData.CreateDrive("small.bin", 4096);
        var largeEnough = FlashTestData.CreateDrive("large.bin", 8193);

        Assert.False(DriveCompatibilityEvaluator.IsDriveLargeEnough(tooSmall, image));
        Assert.True(DriveCompatibilityEvaluator.IsDriveLargeEnough(largeEnough, image));
    }

    [Fact]
    public void IsDriveLargeEnough_UnknownCapacity_DoesNotReject()
    {
        var image = CreateImage(8192, 8192);
        var drive = FlashTestData.CreateDrive("unknown.bin", 0);

        Assert.True(DriveCompatibilityEvaluator.IsDriveLargeEnough(drive, image));
    }

    private static FlashImage CreateImage(long? size, long compressedSize, string? path = null)
    {
        return new FlashImage
        {
            Path = path ?? Path.Combine(Path.GetTempPath(), "uotan-dd-image.img"),
            DisplayName = "system.img",
            Format = ImageFormat.Raw,
            CompressedSize = compressedSize,
            Size = size,
        };
    }
}