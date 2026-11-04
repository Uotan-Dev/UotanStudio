using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;
using UotanStudio.DD.Writing;
using Xunit;

namespace UotanStudio.Tests.DD;

public class ImageFlasherValidationTests
{
    [Fact]
    public async Task FlashAsync_WithoutConfirmOverwrite_ThrowsBeforeCreatingTarget()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(4096, 29);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var targetPath = directory.Combine("target.bin");

        var exception = await Assert.ThrowsAsync<FlashException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [FlashTestData.CreateDrive(targetPath, data.Length)],
            new FlashOptions(),
            progress: null,
            TestContext.Current.CancellationToken));

        Assert.Contains("ConfirmOverwrite", exception.Message);
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public async Task FlashAsync_NoTargets_ThrowsArgumentException()
    {
        using var directory = new FlashTestDirectory();
        var image = ImageProbe.Probe(directory.WriteFile("system.img", FlashTestData.CreateImageData(4096, 31)));

        await Assert.ThrowsAsync<ArgumentException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [],
            new FlashOptions { ConfirmOverwrite = true },
            progress: null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FlashAsync_DuplicateTarget_ThrowsArgumentException()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(4096, 37);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var drive = FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length);

        await Assert.ThrowsAsync<ArgumentException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [drive, drive],
            new FlashOptions { ConfirmOverwrite = true },
            progress: null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FlashAsync_SystemDrive_ThrowsFlashValidationException()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(4096, 41);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var drive = FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length, isSystem: true);

        var exception = await Assert.ThrowsAsync<FlashValidationException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [drive],
            new FlashOptions { ConfirmOverwrite = true },
            progress: null,
            TestContext.Current.CancellationToken));

        Assert.Contains(exception.Failures.SelectMany(failure => failure.Issues), issue => issue.Issue == DriveCompatibilityIssue.SystemDrive);
    }

    [Fact]
    public async Task FlashAsync_ReadOnlyDrive_ThrowsFlashValidationException()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(4096, 43);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var drive = FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length, isReadOnly: true);

        var exception = await Assert.ThrowsAsync<FlashValidationException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [drive],
            new FlashOptions { ConfirmOverwrite = true },
            progress: null,
            TestContext.Current.CancellationToken));

        Assert.Contains(exception.Failures.SelectMany(failure => failure.Issues), issue => issue.Issue == DriveCompatibilityIssue.Locked);
    }

    [Fact]
    public async Task FlashAsync_TooSmallDrive_ThrowsFlashValidationException()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(8192, 47);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var drive = FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length - 1);

        var exception = await Assert.ThrowsAsync<FlashValidationException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [drive],
            new FlashOptions { ConfirmOverwrite = true },
            progress: null,
            TestContext.Current.CancellationToken));

        Assert.Contains(exception.Failures.SelectMany(failure => failure.Issues), issue => issue.Issue == DriveCompatibilityIssue.TooSmall);
    }

    [Fact]
    public async Task FlashAsync_LargeDriveWithoutOptIn_ThrowsFlashValidationException()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(4096, 53);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var drive = FlashTestData.CreateDrive(
            directory.Combine("target.bin"),
            DriveCompatibilityEvaluator.LargeDriveThresholdBytes + 1);

        var exception = await Assert.ThrowsAsync<FlashValidationException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [drive],
            new FlashOptions { ConfirmOverwrite = true },
            progress: null,
            TestContext.Current.CancellationToken));

        Assert.Contains(exception.Failures.SelectMany(failure => failure.Issues), issue => issue.Issue == DriveCompatibilityIssue.LargeDrive);
    }

    [Fact]
    public async Task FlashAsync_LargeDriveWithOptIn_WritesImage()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(4096, 59);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var targetPath = directory.Combine("target.bin");
        var drive = FlashTestData.CreateDrive(targetPath, DriveCompatibilityEvaluator.LargeDriveThresholdBytes + 1);

        await new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [drive],
            new FlashOptions { ConfirmOverwrite = true, AllowLargeDrive = true },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(data, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FlashAsync_SourceImageOnTargetDrive_ThrowsFlashValidationException()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(4096, 61);
        var imagePath = directory.WriteFile("system.img", data);
        var image = ImageProbe.Probe(imagePath);
        var drive = FlashTestData.CreateDrive(
            directory.Combine("target.bin"),
            data.Length,
            mountPoints: [directory.Root]);

        var exception = await Assert.ThrowsAsync<FlashValidationException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [drive],
            new FlashOptions { ConfirmOverwrite = true },
            progress: null,
            TestContext.Current.CancellationToken));

        Assert.Contains(exception.Failures.SelectMany(failure => failure.Issues), issue => issue.Issue == DriveCompatibilityIssue.ContainsImage);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(FlashOptions.MaximumBufferBytes + 1)]
    public async Task FlashAsync_BufferOutsideRange_ThrowsArgumentOutOfRangeException(int bufferBytes)
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(4096, 67);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length)],
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = bufferBytes },
            progress: null,
            TestContext.Current.CancellationToken));
    }
}