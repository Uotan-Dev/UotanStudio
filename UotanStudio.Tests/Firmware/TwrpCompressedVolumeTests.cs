using System.IO.Compression;
using UotanStudio.Compress;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class TwrpCompressedVolumeTests
{
    [Fact]
    public async Task ImportAsync_GzipEmmcVolumes_DecodesEachVolumeIntoMergedImage()
    {
        using var temp = new TempDirectory();
        var content = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 13 + 5)).ToArray();
        temp.WriteBytes("boot.emmc.win000", Gzip(content[..150_000]));
        var second = temp.WriteBytes("boot.emmc.win001", Gzip(content[150_000..]));
        var destination = Path.Combine(temp.Path, "boot.img");

        var set = await DatSplitImporter.ImportAsync(second, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.TwrpImage, set.Kind);
        Assert.Equal(content, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, "boot.img.tmp-*"));
    }

    [Fact]
    public async Task ImportAsync_LegacyLz4EmmcVolume_DecodesIntoMergedImage()
    {
        using var temp = new TempDirectory();
        var content = Enumerable.Range(0, 130_000).Select(i => (byte)(i * 7 + 1)).ToArray();
        var payload = CompressionService.Compress(content, CompressionFormat.Lz4Legacy);
        Assert.Equal([0x02, 0x21, 0x4C, 0x18], payload[..4]);
        var volume = temp.WriteBytes("boot.emmc.win000", payload);
        var destination = Path.Combine(temp.Path, "boot.img");

        var set = await DatSplitImporter.ImportAsync(volume, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.TwrpImage, set.Kind);
        Assert.Equal(content, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ImportAsync_ZstdEmmcVolume_DecodesIntoMergedImage()
    {
        using var temp = new TempDirectory();
        var content = Enumerable.Range(0, 130_000).Select(i => (byte)(i * 11 + 9)).ToArray();
        var payload = CompressionService.Compress(content, CompressionFormat.Zstd);
        Assert.Equal([0x28, 0xB5, 0x2F, 0xFD], payload[..4]);
        var volume = temp.WriteBytes("boot.emmc.win000", payload);
        var destination = Path.Combine(temp.Path, "boot.img");

        var set = await DatSplitImporter.ImportAsync(volume, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DatSplitKind.TwrpImage, set.Kind);
        Assert.Equal(content, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ImportAsync_CompressedVolumeExceedingDecodeCap_FailsDeterministically()
    {
        using var temp = new TempDirectory();
        var volume = temp.WriteBytes("boot.emmc.win000", Gzip(new byte[4 << 20]));
        var destination = temp.WriteBytes("boot.img", [0xCA]);

        var failure = await Assert.ThrowsAsync<CompressionException>(() => DatSplitImporter.ImportAsync(volume,
            destination, null, 1 << 16, TestContext.Current.CancellationToken));

        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal([0xCA], File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, "boot.img.tmp-*"));
    }

    [Fact]
    public void Inspect_TruncatedGzipHeader_StillRejects()
    {
        using var temp = new TempDirectory();
        var selected = temp.WriteBytes("boot.emmc.win000", [0x1F, 0x8B, 0x08]);

        Assert.Throws<InvalidDataException>(() => DatSplitImporter.Inspect(selected));
    }

    [Fact]
    public async Task ImportAsync_RawEmmcVolumes_KeepHistoricalByteCopy()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("boot.emmc.win000", [1, 2]);
        var second = temp.WriteBytes("boot.emmc.win001", [0x1F, 0x8B, 0x08, 0x00]);
        var destination = Path.Combine(temp.Path, "boot.img");

        await Assert.ThrowsAsync<InvalidDataException>(() => DatSplitImporter.ImportAsync(second, destination,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(destination));
    }

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }
        return output.ToArray();
    }
}