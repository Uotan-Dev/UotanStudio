using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Verifies byte measurements throughout extraction, including source preservation and output hashing.</summary>
[Collection("Workspace state")]
public sealed class FileSystemByteProgressTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uotan-byte-progress-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("F2FS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    [InlineData("F2FS", true)]
    public async Task ExtractionReportsActualSizesRatherThanImageOrFileCounts(string format, bool sparse)
    {
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "small"), "payload");
        using (var file = File.Create(Path.Combine(source, "large"))) file.SetLength(12 * 1024 * 1024);
        var image = Path.Combine(root, "vendor_a.img");
        IFormatHandler handler;
        switch (format)
        {
            case "EXT4": Ext4Builder.Build(source, image, new()); handler = new Ext4FormatHandler(); break;
            case "EROFS": new ErofsBuilder().Build(source, image, new()); handler = new ErofsFormatHandler(); break;
            default: F2fsBuilder.Build(source, image, new()); handler = new F2fsFormatHandler(); break;
        }
        if (sparse)
        {
            var converted = Path.Combine(root, "vendor_a.simg");
            SparseImageConverter.ConvertRawToSparse(image, converted);
            image = converted;
            handler = new SparseFormatHandler();
        }
        var expectedBytes = 12L * 1024 * 1024 + 7;
        var events = new List<FileSystemByteProgress>();
        var progress = new InlineProgress<FileSystemByteProgress>(sample => events.Add(sample));
        var output = Path.Combine(root, "output");
        var result = await handler.UnpackAsync(image, output, new UnpackOptions { FileSystemProgress = progress }, Token);
        Assert.True(result.Success, result.Summary);
        foreach (var stage in new[] { FileSystemWorkStage.Preparing, FileSystemWorkStage.Extracting,
            FileSystemWorkStage.Preserving, FileSystemWorkStage.VerifyingImage, FileSystemWorkStage.VerifyingFiles })
        {
            var samples = events.Where(e => e.Stage == stage).ToArray();
            Assert.True(samples.Length >= 2, stage.ToString());
            Assert.Equal(0, samples[0].Processed);
            var total = stage is FileSystemWorkStage.Extracting or FileSystemWorkStage.VerifyingFiles
                ? expectedBytes : new FileInfo(image).Length;
            Assert.All(samples, sample => { Assert.Equal(total, sample.Total); Assert.InRange(sample.Processed, 0, total); });
            Assert.Equal(total, samples[^1].Processed);
            Assert.True(samples.Zip(samples.Skip(1)).All(pair => pair.First.Processed <= pair.Second.Processed));
        }
        Assert.Equal(12 * 1024 * 1024, new FileInfo(Path.Combine(output, "large")).Length);
        // A progress-enabled scan must retain exactly the same projection hashes as a normal scan.
        var restored = Path.Combine(root, "restored.img");
        Assert.True((await handler.PackAsync(output, restored, Token)).Success);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image)),
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(restored)));
    }

    [Fact]
    public async Task CounterMeasuresSyncAndAsyncWritesAndLeavesDestinationOpen()
    {
        var events = new List<(long Processed, long Total)>();
        var counter = new ExtractionByteCounter(9, new InlineProgress<(long Processed, long Total)>(events.Add), Token);
        using var output = new MemoryStream();
        using (var stream = counter.Wrap(output))
        {
            stream.Write([1, 2, 3]);
            await stream.WriteAsync(new byte[6], Token);
        }
        counter.Complete();
        Assert.Equal(9, output.Length);
        Assert.Equal((0L, 9L), events[0]);
        Assert.Equal((9L, 9L), events[^1]);
        Assert.True(output.CanWrite);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    [Fact]
    public void CounterReportsInsideALargeFileBeforeItCompletes()
    {
        var events = new List<(long Processed, long Total)>();
        var counter = new ExtractionByteCounter(1024 * 1024, new InlineProgress<(long Processed, long Total)>(events.Add), Token);
        using var output = new MemoryStream();
        using var stream = counter.Wrap(output);
        Thread.Sleep(120); // Exceed the production notification throttle, not a fabricated byte sample.
        stream.Write(new byte[65536]);
        Assert.Equal((65536L, 1024L * 1024), events[^1]);
        Assert.True(events[^1].Processed < events[^1].Total);
    }

    [Fact]
    public void CounterAggregatesParallelWorkersWithoutLosingBytes()
    {
        var events = new List<(long Processed, long Total)>();
        var counter = new ExtractionByteCounter(8192, new InlineProgress<(long Processed, long Total)>(events.Add), Token);
        Parallel.For(0, 8, new ParallelOptions { CancellationToken = Token }, _ =>
        {
            using var output = new MemoryStream();
            using var stream = counter.Wrap(output);
            for (var i = 0; i < 16; i++) stream.Write(new byte[64]);
        });
        counter.Complete();
        Assert.Equal((8192L, 8192L), events[^1]);
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Processed <= pair.Second.Processed));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}