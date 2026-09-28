using System.Formats.Tar;
using System.Text;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DatSplitServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-split-service-" + Guid.NewGuid().ToString("N"));

    public DatSplitServiceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconstructSplitDatImageAsync_FullDatVolumes_ProducesSourceImage(bool brotli)
    {
        var token = TestContext.Current.CancellationToken;
        var original = Enumerable.Range(0, DatReader.BlockSize * 12).Select(i => (byte)(i * 11 + 5)).ToArray();
        var image = await WriteAsync("source.img", original, token);
        var data = Path.Combine(_root, "system.new.dat");
        DatWriter.Write(image, data, Path.Combine(_root, "system.transfer.list"),
            new DatWriteOptions { CompressWithBrotli = brotli }, cancellationToken: TestContext.Current.CancellationToken);
        var generated = data + (brotli ? ".br" : "");
        var bytes = await File.ReadAllBytesAsync(generated, token);
        File.Delete(generated);
        var prefix = brotli ? "system.new.dat.br." : "system.new.dat.";
        var first = await WriteAsync(prefix + "000", bytes[..(bytes.Length / 2)], token);
        await WriteAsync(prefix + "001", bytes[(bytes.Length / 2)..], token);
        var output = await WriteAsync("reconstructed.img", [0xCA, 0xFE], token);

        var inspected = FirmwareService.Instance.InspectSplitDatOrTwrp(first, token);
        var result = await FirmwareService.Instance.ReconstructSplitDatImageAsync(
            first, output, cancellationToken: token);

        Assert.Equal(brotli ? DatSplitKind.NewDatBrotli : DatSplitKind.NewDat, inspected.Kind);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(["system.new.dat" + (brotli ? ".br" : "") + ".000",
            "system.new.dat" + (brotli ? ".br" : "") + ".001"], result.Entries);
        Assert.Equal(original, await File.ReadAllBytesAsync(output, token));
        Assert.Empty(Directory.GetFiles(_root, "reconstructed.img.*-*"));
    }

    [Fact]
    public async Task ImportSplitDatOrTwrpAsync_PatchVolumes_ImportsArtifactWithoutClaimingImageReconstruction()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "system.transfer.list"),
            "4\n1\n0\n0\nnew 2,0,1\n", token);
        var first = await WriteAsync("system.patch.dat.000", [1, 2], token);
        await WriteAsync("system.patch.dat.001", [3, 4], token);
        var artifact = Path.Combine(_root, "system.patch.dat");
        var image = await WriteAsync("system.img", [0x55], token);

        var imported = await FirmwareService.Instance.ImportSplitDatOrTwrpAsync(
            first, artifact, cancellationToken: token);
        var reconstructed = await FirmwareService.Instance.ReconstructSplitDatImageAsync(
            first, image, cancellationToken: token);

        Assert.True(imported.Success, imported.Summary);
        Assert.Contains(nameof(DatSplitKind.PatchDat), imported.Summary);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(artifact, token));
        Assert.False(reconstructed.Success);
        Assert.Equal([0x55], await File.ReadAllBytesAsync(image, token));
    }

    [Fact]
    public async Task ImportSplitDatOrTwrpAsync_TwrpTarVolumes_CombinesIndependentArchives()
    {
        var token = TestContext.Current.CancellationToken;
        var first = WriteTar("data.ext4.win000", "data/a", "one");
        WriteTar("data.ext4.win001", "data/b", "two");
        var destination = Path.Combine(_root, "data.tar");

        var result = await FirmwareService.Instance.ImportSplitDatOrTwrpAsync(
            first, destination, cancellationToken: token);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(DatSplitKind.TwrpTar, FirmwareService.Instance.InspectSplitDatOrTwrp(first, token).Kind);
        using var reader = new TarReader(File.OpenRead(destination));
        Assert.Equal("one", ReadEntry(reader.GetNextEntry()));
        Assert.Equal("two", ReadEntry(reader.GetNextEntry()));
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task ImportSplitDatOrTwrpAsync_TwrpRawVolumes_JoinsBytesInOrder()
    {
        var token = TestContext.Current.CancellationToken;
        await WriteAsync("boot.emmc.win000", [1, 2], token);
        var selected = await WriteAsync("boot.emmc.win001", [3, 4], token);
        var output = Path.Combine(_root, "boot.img");

        var result = await FirmwareService.Instance.ImportSplitDatOrTwrpAsync(
            selected, output, cancellationToken: token);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(DatSplitKind.TwrpImage, FirmwareService.Instance.InspectSplitDatOrTwrp(selected, token).Kind);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(output, token));
    }

    [Fact]
    public async Task ImportSplitDatOrTwrpAsync_MissingVolume_PreservesExistingArtifact()
    {
        var token = TestContext.Current.CancellationToken;
        var selected = await WriteAsync("system.new.dat.000", [1], token);
        await WriteAsync("system.new.dat.002", [2], token);
        var output = await WriteAsync("system.new.dat", [0xCA], token);

        var result = await FirmwareService.Instance.ImportSplitDatOrTwrpAsync(
            selected, output, cancellationToken: token);

        Assert.False(result.Success);
        Assert.Equal([0xCA], await File.ReadAllBytesAsync(output, token));
        Assert.Empty(Directory.GetFiles(_root, "system.new.dat.tmp-*"));
    }

    [Fact]
    public async Task ImportSplitDatOrTwrpAsync_CancelDuringCopy_PreservesExistingArtifact()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "system.transfer.list"),
            "4\n1\n0\n0\nnew 2,0,1\n", cancellation.Token);
        var selected = await WriteAsync("system.patch.dat.000", new byte[512 * 1024], cancellation.Token);
        await WriteAsync("system.patch.dat.001", [1], cancellation.Token);
        var output = await WriteAsync("system.patch.dat", [0xCA], cancellation.Token);
        var progress = new CallbackProgress<(long Processed, long Total)>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FirmwareService.Instance.ImportSplitDatOrTwrpAsync(
                selected, output, progress, cancellation.Token));

        Assert.Equal([0xCA], await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "system.patch.dat.tmp-*"));
    }

    [Fact]
    public async Task ReconstructSplitDatImageAsync_PreCanceled_PreservesExistingImage()
    {
        var output = await WriteAsync("existing.img", [0x7F], TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FirmwareService.Instance.ReconstructSplitDatImageAsync(
                "unused.new.dat.000", output, cancellationToken: cancellation.Token));

        Assert.Equal([0x7F], await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    private async Task<string> WriteAsync(string name, byte[] bytes, CancellationToken token)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, bytes, token);
        return path;
    }

    private string WriteTar(string name, string entryName, string text)
    {
        var path = Path.Combine(_root, name);
        using var writer = new TarWriter(File.Create(path), TarEntryFormat.Pax);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text))
        });
        return path;
    }

    private static string ReadEntry(TarEntry? entry)
    {
        var data = Assert.IsType<PaxTarEntry>(entry).DataStream!;
        using var text = new StreamReader(data, Encoding.UTF8);
        return text.ReadToEnd();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}