using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class DatSplitImportTests
{
    [Fact]
    public async Task ImportAsync_NumberedDat_RejoinsBytesAndReconstructsImage()
    {
        using var temp = new TempDirectory();
        var image = Enumerable.Range(0, DatReader.BlockSize * 24).Select(i => (byte)(i * 13 + 3)).ToArray();
        var imagePath = temp.WriteBytes("source.img", image);
        var dat = Path.Combine(temp.Path, "system.new.dat");
        var list = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(imagePath, dat, list);
        var original = File.ReadAllBytes(dat);
        File.Delete(dat);
        var first = temp.WriteBytes("system.new.dat.000", original[..13000]);
        temp.WriteBytes("system.new.dat.001", original[13000..]);
        temp.WriteBytes("vendor.new.dat.000", [0x7F]);
        var joined = Path.Combine(temp.Path, "system.new.dat");

        var set = await DatSplitImporter.ImportAsync(first, joined, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.NewDat, set.Kind);
        Assert.Equal(list, set.CompanionTransferList);
        Assert.Equal(["system.new.dat.000", "system.new.dat.001"], set.Parts.Select(Path.GetFileName));
        Assert.Equal(original, File.ReadAllBytes(joined));
        var output = Path.Combine(temp.Path, "system.img");
        DatReader.Unpack(list, joined, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(image, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task ImportAsync_BrotliDat_PreservesCompressedStreamAndReconstructsImage()
    {
        using var temp = new TempDirectory();
        var image = Enumerable.Range(0, DatReader.BlockSize * 64).Select(i => (byte)(i * 19 + 7)).ToArray();
        var imagePath = temp.WriteBytes("source.img", image);
        var dat = Path.Combine(temp.Path, "system.new.dat");
        var list = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(imagePath, dat, list, new DatWriteOptions { CompressWithBrotli = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(dat + ".br");
        File.Delete(dat + ".br");
        temp.WriteBytes("system.new.dat.br.000", original[..(original.Length / 2)]);
        var second = temp.WriteBytes("system.new.dat.br.001", original[(original.Length / 2)..]);

        var joined = dat + ".br";
        var set = await DatSplitImporter.ImportAsync(second, joined, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.NewDatBrotli, set.Kind);
        Assert.Equal(original, File.ReadAllBytes(joined));
        var output = Path.Combine(temp.Path, "system.img");
        DatReader.Unpack(list, joined, output, brotliCompressed: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(image, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconstructDatImageAsync_FullOtaVolumes_ProducesByteExactImage(bool brotli)
    {
        using var temp = new TempDirectory();
        var image = Enumerable.Range(0, DatReader.BlockSize * 18).Select(i => (byte)(i * 17 + 1)).ToArray();
        var source = temp.WriteBytes("source.img", image);
        var dat = Path.Combine(temp.Path, "system.new.dat");
        DatWriter.Write(source, dat, Path.Combine(temp.Path, "system.transfer.list"),
            new DatWriteOptions { CompressWithBrotli = brotli }, cancellationToken: TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(dat + (brotli ? ".br" : ""));
        File.Delete(dat + (brotli ? ".br" : ""));
        var prefix = brotli ? "system.new.dat.br." : "system.new.dat.";
        var first = temp.WriteBytes(prefix + "000", original[..(original.Length / 2)]);
        temp.WriteBytes(prefix + "001", original[(original.Length / 2)..]);
        var output = Path.Combine(temp.Path, "system.img");

        var set = await DatSplitImporter.ReconstructDatImageAsync(
            first, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(brotli ? DatSplitKind.NewDatBrotli : DatSplitKind.NewDat, set.Kind);
        Assert.Equal(image, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "system.img.data-*"));
    }

    [Fact]
    public async Task ReconstructDatImageAsync_CancelDuringCommands_PreservesExistingImage()
    {
        using var temp = new TempDirectory();
        var image = Enumerable.Range(0, DatReader.BlockSize * 8).Select(i => (byte)(i * 3 + 1)).ToArray();
        var source = temp.WriteBytes("source.img", image);
        var dat = Path.Combine(temp.Path, "system.new.dat");
        DatWriter.Write(source, dat, Path.Combine(temp.Path, "system.transfer.list"),
            new DatWriteOptions { MaxCommandRangeBlocks = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(dat);
        File.Delete(dat);
        var first = temp.WriteBytes("system.new.dat.000", original[..(original.Length / 2)]);
        temp.WriteBytes("system.new.dat.001", original[(original.Length / 2)..]);
        var output = temp.WriteBytes("system.img", [0xCA, 0xFE]);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new FractionProgress(cancel.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatSplitImporter.ReconstructDatImageAsync(first, output, progress, cancel.Token));

        Assert.Equal([0xCA, 0xFE], File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "system.img.data-*"));
        Assert.Empty(Directory.GetFiles(temp.Path, "system.img.tmp-*"));
    }

    [Fact]
    public async Task ImportAsync_TwrpTarVolumes_RewritesBothIndependentArchives()
    {
        using var temp = new TempDirectory();
        var one = WriteTar(temp, "data.ext4.win000", "data/a", "first");
        var two = WriteTar(temp, "data.ext4.win001", "data/b", "second");
        var destination = Path.Combine(temp.Path, "data.tar");

        var set = await DatSplitImporter.ImportAsync(two, destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.TwrpTar, set.Kind);
        Assert.Equal([one, two], set.Parts);
        using var reader = new TarReader(File.OpenRead(destination));
        Assert.Equal("first", ReadTarEntry(reader.GetNextEntry()));
        Assert.Equal("second", ReadTarEntry(reader.GetNextEntry()));
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task ImportAsync_TwrpGzipTarVolumes_ReadsEachArchiveSeparately()
    {
        using var temp = new TempDirectory();
        var first = WriteTar(temp, "data.ext4.win000", "data/a", "first");
        var second = WriteTar(temp, "data.ext4.win001", "data/b", "second");
        CompressInPlace(first);
        CompressInPlace(second);
        var destination = Path.Combine(temp.Path, "data.tar");

        var set = await DatSplitImporter.ImportAsync(first, destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.TwrpTar, set.Kind);
        using var reader = new TarReader(File.OpenRead(destination));
        Assert.Equal("first", ReadTarEntry(reader.GetNextEntry()));
        Assert.Equal("second", ReadTarEntry(reader.GetNextEntry()));
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task ImportAsync_EmmcVolumes_StreamsRawBytesInOrder()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("boot.emmc.win000", [1, 2]);
        var second = temp.WriteBytes("boot.emmc.win001", [3, 4]);
        var destination = Path.Combine(temp.Path, "boot.img");

        var set = await DatSplitImporter.ImportAsync(second, destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.TwrpImage, set.Kind);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ImportAsync_BrotliPatchDat_PreservesBytesAndIdentifiesPatch()
    {
        using var temp = new TempDirectory();
        temp.Write("system.transfer.list", "4\n1\n0\n0\nnew 2,0,1\n");
        temp.WriteBytes("system.patch.dat.br.000", [1, 2]);
        var second = temp.WriteBytes("system.patch.dat.br.001", [3, 4]);
        var destination = Path.Combine(temp.Path, "system.patch.dat.br");

        var set = await DatSplitImporter.ImportAsync(second, destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.PatchDatBrotli, set.Kind);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ImportAsync_TwrpThreadOneVolume_ImportsWithoutThreadZero()
    {
        using var temp = new TempDirectory();
        var first = WriteTar(temp, "data.ext4.win100", "data/a", "first");
        var destination = Path.Combine(temp.Path, "data.tar");

        var set = await DatSplitImporter.ImportAsync(first, destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.TwrpTar, set.Kind);
        using var reader = new TarReader(File.OpenRead(destination));
        Assert.Equal("first", ReadTarEntry(reader.GetNextEntry()));
        Assert.Null(reader.GetNextEntry());
    }

    [Theory]
    [InlineData("system.new.dat.000", "system.new.dat.002")]
    [InlineData("system.new.dat.000", "system.new.dat.00")]
    [InlineData("data.ext4.win000", "data.ext4.win002")]
    [InlineData("data.ext4.win100", "data.ext4.win102")]
    [InlineData("data.ext4.win000", "data.ext4.win200")]
    public void Inspect_MissingOrAmbiguousVolume_RejectsSet(string first, string second)
    {
        using var temp = new TempDirectory();
        var selected = temp.WriteBytes(first, [1]);
        temp.WriteBytes(second, [2]);
        if (first.StartsWith("system.", StringComparison.Ordinal))
        {
            temp.Write("system.transfer.list", "4\n1\n0\n0\nnew 2,0,1\n");
        }

        Assert.Throws<InvalidDataException>(() => DatSplitImporter.Inspect(selected));
    }

    [Fact]
    public async Task ImportAsync_OnlyAnotherPartitionTransferList_PreservesDestination()
    {
        using var temp = new TempDirectory();
        var selected = temp.WriteBytes("system.new.dat.000", [1]);
        temp.Write("vendor.transfer.list", "4\n1\n0\n0\nnew 2,0,1\n");
        var destination = temp.WriteBytes("system.new.dat", [0xCA]);

        var set = DatSplitImporter.Inspect(selected);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatSplitImporter.ImportAsync(selected, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(set.CompanionTransferList);
        Assert.Equal([0xCA], File.ReadAllBytes(destination));
    }

    [Fact]
    public void Inspect_CompressedEmmcVolume_RejectsUnknownCodec()
    {
        using var temp = new TempDirectory();
        var selected = temp.WriteBytes("boot.emmc.win000", [0x1F, 0x8B, 0x08]);

        Assert.Throws<InvalidDataException>(() => DatSplitImporter.Inspect(selected));
    }

    [Fact]
    public async Task ImportAsync_CompressedLaterEmmcVolume_PreservesDestination()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteBytes("boot.emmc.win000", [1, 2]);
        temp.WriteBytes("boot.emmc.win001", [0x1F, 0x8B, 0x08]);
        var destination = temp.WriteBytes("boot.img", [0xCA]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatSplitImporter.ImportAsync(first, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([0xCA], File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ImportAsync_InvalidSameStemTransferList_PreservesDestination()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteBytes("system.new.dat.000", [1, 2]);
        temp.Write("system.transfer.list", "not a transfer list");
        var destination = temp.WriteBytes("system.new.dat", [0xCA]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatSplitImporter.ImportAsync(first, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([0xCA], File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_NewDataLengthMismatch_PreservesDestination(bool brotli)
    {
        using var temp = new TempDirectory();
        temp.Write("system.transfer.list", "4\n1\n0\n0\nnew 2,0,1\n");
        var prefix = brotli ? "system.new.dat.br" : "system.new.dat";
        var selected = temp.WriteBytes(prefix + ".000", brotli ? [0xFF, 0xFF] : [1, 2]);
        var destination = temp.WriteBytes(prefix, [0xCA]);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            DatSplitImporter.ImportAsync(selected, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([0xCA], File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, prefix + ".tmp-*"));
    }

    [Fact]
    public async Task ImportAsync_TruncatedTarVolume_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        var first = WriteTar(temp, "data.ext4.win000", "data/a", "first");
        temp.WriteBytes("data.ext4.win001", new byte[512]);
        var destination = temp.WriteBytes("data.tar", [0xCA, 0xFE]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatSplitImporter.ImportAsync(first, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([0xCA, 0xFE], File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, "data.tar.tmp-*"));
    }

    [Fact]
    public async Task ImportAsync_CancellationDuringDatCopy_PreservesDestination()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteBytes("system.new.dat.000", new byte[512 * 1024]);
        temp.WriteBytes("system.new.dat.001", [1, 2, 3]);
        temp.Write("system.transfer.list", "4\n1\n0\n0\nnew 2,0,1\n");
        var destination = temp.WriteBytes("system.new.dat", [0xAA]);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new CallbackProgress(cancel.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatSplitImporter.ImportAsync(first, destination, progress, cancel.Token));

        Assert.Equal([0xAA], File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, "system.new.dat.tmp-*"));
    }

    [Fact]
    public async Task ImportAsync_DestinationIsSource_RejectsWithoutMutation()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("boot.emmc.win000", [1, 2]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            DatSplitImporter.ImportAsync(source, source, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([1, 2], File.ReadAllBytes(source));
    }

    private static string WriteTar(TempDirectory temp, string name, string entryName, string text)
    {
        var path = Path.Combine(temp.Path, name);
        using var output = File.Create(path);
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text))
        });
        return path;
    }

    private static string ReadTarEntry(TarEntry? entry)
    {
        Assert.NotNull(entry);
        Assert.NotNull(entry.DataStream);
        using var text = new StreamReader(entry.DataStream, Encoding.UTF8);
        return text.ReadToEnd();
    }

    private static void CompressInPlace(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        gzip.Write(bytes);
    }

    private sealed class CallbackProgress(Action callback) : IProgress<(long Processed, long Total)>
    {
        public void Report((long Processed, long Total) value) => callback();
    }

    private sealed class FractionProgress(Action callback) : IProgress<double>
    {
        public void Report(double value) => callback();
    }
}