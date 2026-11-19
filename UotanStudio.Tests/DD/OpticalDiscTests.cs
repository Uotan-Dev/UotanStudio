using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.Linux;
using UotanStudio.DD.Images;
using UotanStudio.DD.Writing;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class OpticalDiscTests
{
    [Fact]
    public async Task BurnAsync_NonIsoImage_ThrowsBeforeWriterCreation()
    {
        var drive = CreateOpticalDrive(writable: true);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllText(path, "not an iso");
        var image = new FlashImage
        {
            Path = path,
            DisplayName = Path.GetFileName(path),
            Format = ImageFormat.Raw,
            CompressedSize = 10,
            Size = 10,
        };
        var factory = new ThrowingWriterFactory();
        var burner = new OpticalDiscBurner(factory);
        try
        {
            await Assert.ThrowsAsync<FlashException>(() =>
                burner.BurnAsync(image, drive, new OpticalDiscSession(path),
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(factory.Called);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class ThrowingWriterFactory : IDiscWriterFactory
    {
        internal bool Called { get; private set; }

        public IDiscWriter Create(FlashDrive drive)
        {
            Called = true;
            throw new InvalidOperationException("Writer must not be created for an invalid ISO.");
        }
    }

    [Fact]
    public void Evaluate_OpticalDriveWithoutMedia_ReturnsNoWritableDiscError()
    {
        var drive = CreateOpticalDrive(writable: false);
        var image = CreateRawImage();

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);

        var issue = Assert.Single(issues);
        Assert.Equal(DriveCompatibilityIssue.NoWritableDisc, issue.Issue);
        Assert.Equal(DriveCompatibilitySeverity.Error, issue.Severity);
    }

    [Fact]
    public void Evaluate_OpticalDriveWithCompressedImage_ReturnsUnsupportedImageError()
    {
        var drive = CreateOpticalDrive(writable: true);
        var image = new FlashImage
        {
            Path = Path.Combine(Path.GetTempPath(), "disc.img.gz"),
            DisplayName = "disc.img.gz",
            Format = new ImageFormat(ImageContainerKind.Compressed, UotanStudio.Compress.CompressionFormat.Gzip),
            CompressedSize = 1024,
            Size = null,
        };

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, image, writing: true);

        var issue = Assert.Single(issues);
        Assert.Equal(DriveCompatibilityIssue.UnsupportedDiscImage, issue.Issue);
        Assert.Equal(DriveCompatibilitySeverity.Error, issue.Severity);
    }

    [Fact]
    public void Evaluate_OpticalDriveWithWritableMediaAndRawImage_Accepts()
    {
        var drive = CreateOpticalDrive(writable: true);

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, CreateRawImage(), writing: true);

        Assert.Empty(issues);
    }

    [Fact]
    public void Evaluate_OpticalDrive_SkipsDiskOnlyRules()
    {
        var drive = CreateOpticalDrive(writable: true) with { IsSystem = true, Size = long.MaxValue };

        var issues = DriveCompatibilityEvaluator.Evaluate(drive, CreateRawImage(), writing: true);

        Assert.Empty(issues);
    }

    [Fact]
    public void IsBlocking_OpticalErrors_BlocksWriting()
    {
        Assert.True(DriveCompatibilityEvaluator.IsBlocking(
            new DriveCompatibilityIssueInfo(DriveCompatibilityIssue.NoWritableDisc, DriveCompatibilitySeverity.Error),
            allowLargeDrive: true));
        Assert.True(DriveCompatibilityEvaluator.IsBlocking(
            new DriveCompatibilityIssueInfo(DriveCompatibilityIssue.UnsupportedDiscImage, DriveCompatibilitySeverity.Error),
            allowLargeDrive: true));
    }

    [Fact]
    public void ParseCdromInfo_RealKernelFields_ReportsWriterCapability()
    {
        var lines = new[]
        {
            "CD-ROM information, Id: cdrom.c 3.20 2003/12/17",
            "",
            "drive name:		sr0 sr1",
            "drive speed:		0 0",
            "Can read DVD:		1 1",
            "Can write CD-R:		1 0",
            "Can write CD-RW:		1 0",
            "Can write DVD-R:		1 0",
            "Can write DVD-RAM:		0 0",
        };

        var caps = LinuxOpticalDriveScanner.ParseCdromInfo(lines, ["sr0", "sr1"]);

        Assert.Equal(2, caps.Count);
        Assert.Equal(OpticalDiscFormat.Dvd, caps["sr0"].Format);
        Assert.True(caps["sr0"].Writable);
        Assert.Equal(OpticalDiscFormat.Dvd, caps["sr1"].Format);
        Assert.False(caps["sr1"].Writable);
    }

    [Fact]
    public void ParseCdromInfo_NoWriteFlags_ReportsNotWritable()
    {
        var lines = new[] { "drive name:		sr0", "Can read DVD:		1" };

        var caps = LinuxOpticalDriveScanner.ParseCdromInfo(lines, ["sr0"]);

        Assert.False(caps["sr0"].Writable);
    }

    [Fact]
    public void ScanFormat_RoundTrip_PreservesOpticalDriveFields()
    {
        var optical = CreateOpticalDrive(writable: true) with { DevicePath = "/dev/sr0", DisplayName = "DVD Burner" };
        var report = new DiskScanReport { Drives = [optical] };

        var parsed = DiskScanFormat.Read(DiskScanFormat.Write(report), out var isComplete);

        Assert.True(isComplete);
        var drive = Assert.Single(parsed.Drives);
        Assert.Equal(FlashMediaKind.OpticalDisc, drive.MediaKind);
        Assert.Equal(OpticalDiscFormat.Dvd, drive.DiscFormat);
        Assert.True(drive.IsDiscWritable);
    }

    [Fact]
    public void ScanFormat_LegacyRecord_DefaultsToDisk()
    {
        const string legacy = "uotan-disk-scan 1\ndrive 0 /dev/sda sda 1024 1 0 0 -\nend\n";

        var parsed = DiskScanFormat.Read(legacy, out var isComplete);

        Assert.True(isComplete);
        var drive = Assert.Single(parsed.Drives);
        Assert.Equal(FlashMediaKind.Disk, drive.MediaKind);
        Assert.False(drive.IsDiscWritable);
    }

    [Fact]
    public void PlatformDiscWriterFactory_NonOpticalDrive_IsRejected()
    {
        var drive = FlashTestData.CreateDrive("/dev/sdb", 1024);

        Assert.Throws<ArgumentException>(() => PlatformDiscWriterFactory.Instance.Create(drive));
    }

    private static FlashDrive CreateOpticalDrive(bool writable) => new()
    {
        DevicePath = "/dev/sr0",
        DisplayName = "Test Optical Drive",
        MediaKind = FlashMediaKind.OpticalDisc,
        DiscFormat = OpticalDiscFormat.Dvd,
        IsDiscWritable = writable,
        IsRemovable = true,
    };

    private static FlashImage CreateRawImage() => new()
    {
        Path = Path.Combine(Path.GetTempPath(), "disc.iso"),
        DisplayName = "disc.iso",
        Format = ImageFormat.Raw,
        CompressedSize = 1024,
        Size = 1024,
    };
}