using System.IO.Compression;
using UotanStudio.Compress;
using UotanStudio.Compress.Zip;
using UotanStudio.DD;
using UotanStudio.DD.Images;
using UotanStudio.DD.Writing;
using Xunit;

namespace UotanStudio.Tests.DD;

public class ImageFlasherTests
{
    [Fact]
    public async Task FlashAsync_RawImageToTwoTargets_WritesVerifiedBytes()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(1024 * 1024 + 13, 11);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var firstPath = directory.Combine("target-a.bin");
        var secondPath = directory.Combine("target-b.bin");
        var drives = new[]
        {
            FlashTestData.CreateDrive(firstPath, data.Length + 4096),
            FlashTestData.CreateDrive(secondPath, data.Length + 4096),
        };
        var progress = new RecordingProgress();

        var result = await new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            drives,
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = 64 * 1024 },
            progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(data, await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken));
        Assert.Equal(data, await File.ReadAllBytesAsync(secondPath, TestContext.Current.CancellationToken));
        Assert.Equal(data.Length, result.ImageBytes);
        Assert.Equal(data.Length * 2, result.BytesWritten);
        Assert.Equal(FlashTestData.ComputeSha256(data), result.SourceChecksum);
        Assert.Equal(FlashStage.Completed, progress.Reports[^1].Stage);
        Assert.Equal(100d, progress.Reports[^1].Percentage!.Value);
        Assert.Contains(progress.Reports, report => report.Stage == FlashStage.Verifying);
    }

    [Fact]
    public async Task FlashAsync_GzipImage_WritesDecodedBytes()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(256 * 1024, 13);
        var imagePath = directory.Combine("system.img.gz");
        using (var file = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write))
        using (var gzip = CompressionService.CreateCompressionStream(file, CompressionFormat.Gzip))
        {
            gzip.Write(data);
        }

        var targetPath = directory.Combine("target.bin");
        var image = ImageProbe.Probe(imagePath);
        var result = await new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [FlashTestData.CreateDrive(targetPath, data.Length + 4096)],
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = 64 * 1024 },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(data, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
        Assert.Equal(data.Length, result.ImageBytes);
        Assert.Null(image.Size);
    }

    [Fact]
    public async Task FlashAsync_GzipImageToSectorAlignedTarget_PadsFinalChunkAndVerifies()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(256 * 1024 + 278, 13);
        var imagePath = directory.Combine("system.img.gz");
        using (var file = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write))
        using (var gzip = CompressionService.CreateCompressionStream(file, CompressionFormat.Gzip))
        {
            gzip.Write(data);
        }

        var image = ImageProbe.Probe(imagePath);
        var factory = new AlignedDiskTargetFactory();
        var progress = new RecordingProgress();
        var result = await new ImageFlasher(factory).FlashAsync(
            image,
            [FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length + 4096)],
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = 64 * 1024 },
            progress,
            TestContext.Current.CancellationToken);

        var written = factory.Target!.WrittenBytes;
        var paddedLength = ((data.Length + AlignedDiskTargetFactory.AlignmentBytes - 1) /
            AlignedDiskTargetFactory.AlignmentBytes) * AlignedDiskTargetFactory.AlignmentBytes;
        Assert.Equal(paddedLength, written.Length);
        Assert.Equal(data, written[..data.Length]);
        Assert.All(written[data.Length..], value => Assert.Equal((byte)0, value));
        Assert.Equal(data.Length, result.ImageBytes);
        Assert.Equal(data.Length, result.BytesWritten);
        Assert.Equal(FlashTestData.ComputeSha256(data), result.SourceChecksum);
        Assert.Equal(FlashStage.Completed, progress.Reports[^1].Stage);
        Assert.Contains(progress.Reports, report => report.Stage == FlashStage.Verifying);
    }

    [Fact]
    public async Task FlashAsync_RawImageToSectorAlignedTarget_KeepsRealByteCounts()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(1024 * 1024 + 278, 29);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var factory = new AlignedDiskTargetFactory();

        var result = await new ImageFlasher(factory).FlashAsync(
            image,
            [FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length + 4096)],
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = 64 * 1024 },
            progress: null,
            TestContext.Current.CancellationToken);

        var written = factory.Target!.WrittenBytes;
        Assert.Equal(data, written[..data.Length]);
        Assert.All(written[data.Length..], value => Assert.Equal((byte)0, value));
        Assert.Equal(data.Length, result.ImageBytes);
        Assert.Equal(data.Length, result.BytesWritten);
        Assert.Equal(FlashTestData.ComputeSha256(data), result.SourceChecksum);
    }

    [Fact]
    public async Task FlashAsync_ZipImage_WritesEntryBytes()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(128 * 1024, 17);
        var imagePath = directory.Combine("system.zip");
        using (var archive = ZipContainer.Create(imagePath))
        {
            using var entry = archive.CreateEntry("system.img", CompressionLevel.Fastest).Open();
            entry.Write(data);
        }

        var targetPath = directory.Combine("target.bin");
        var image = ImageProbe.Probe(imagePath);
        await new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [FlashTestData.CreateDrive(targetPath, data.Length + 4096)],
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = 64 * 1024 },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(data, await File.ReadAllBytesAsync(targetPath, TestContext.Current.CancellationToken));
        Assert.Equal("system.img", image.ZipEntryName);
    }

    [Fact]
    public async Task FlashAsync_VerificationMismatch_ThrowsFlashVerificationException()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(64 * 1024, 19);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var drive = FlashTestData.CreateDrive(directory.Combine("target.bin"), data.Length);

        var exception = await Assert.ThrowsAsync<FlashVerificationException>(() => new ImageFlasher(MismatchDiskTargetFactory.Instance).FlashAsync(
            image,
            [drive],
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = 64 * 1024 },
            progress: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, exception.Offset);
        Assert.Equal(drive.DevicePath, exception.Drive.DevicePath);
    }

    [Fact]
    public async Task FlashAsync_CancelledDuringWrite_ThrowsOperationCanceledException()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(2 * 1024 * 1024, 23);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var targetPath = directory.Combine("target.bin");
        using var source = new CancellationTokenSource();
        var progress = new CancelOnWriteProgress(source, 512 * 1024);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [FlashTestData.CreateDrive(targetPath, data.Length + 4096)],
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = 64 * 1024 },
            progress,
            source.Token));
    }

    [Fact]
    public async Task FlashAsync_ProgressSink_ReportsEveryStageInOrder()
    {
        using var directory = new FlashTestDirectory();
        var data = FlashTestData.CreateImageData(256 * 1024, 25);
        var image = ImageProbe.Probe(directory.WriteFile("system.img", data));
        var targetPath = directory.Combine("target.bin");
        var progress = new RecordingProgress();

        await new ImageFlasher(FileDiskTargetFactory.Instance).FlashAsync(
            image,
            [FlashTestData.CreateDrive(targetPath, data.Length + 4096)],
            new FlashOptions { ConfirmOverwrite = true, BufferBytes = 64 * 1024 },
            progress,
            TestContext.Current.CancellationToken);

        var stages = progress.Reports.Select(report => report.Stage).Distinct().ToArray();
        Assert.Equal(
            [FlashStage.Preparing, FlashStage.Writing, FlashStage.Verifying, FlashStage.Completed],
            stages);
        Assert.Equal(0, progress.Reports[0].BytesProcessed);
        Assert.Equal(1, progress.Reports[0].TargetCount);
        var writing = progress.Reports.Where(report => report.Stage == FlashStage.Writing).ToArray();
        Assert.True(writing.Length >= 2);
        Assert.Equal(data.Length, writing[^1].BytesProcessed);
    }
}