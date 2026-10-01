using System.IO.Compression;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Fat;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware.Fat;

public sealed class ExFatKernelVolumeTests
{
    private const string FixtureAsset = "exfat-kernel-written.deflate";
    private const long VolumeBytes = 8_388_608;

    public ExFatKernelVolumeTests() => FirmwareFormatRegistry.RegisterAll();

    [Fact]
    public void Detect_KernelWrittenVolume_SelectsExFatHandler()
    {
        using var temp = new TempDirectory();
        var image = UnpackFixture(temp, VolumeBytes);

        var handler = FormatDetector.Detect(image);

        Assert.NotNull(handler);
        Assert.Equal("EXFAT", handler!.FormatName);
    }

    [Fact]
    public async Task ReadMetadataAsync_KernelWrittenVolume_ReportsBootRecordGeometryAndEntries()
    {
        using var temp = new TempDirectory();
        var image = UnpackFixture(temp, VolumeBytes);

        var result = await Handler().ReadMetadataAsync(image, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("exFAT", result.Metadata["Format"]);
        Assert.Equal("512", result.Metadata["BytesPerSector"]);
        Assert.Equal("8", result.Metadata["SectorsPerCluster"]);
        Assert.Equal("1", result.Metadata["FatCount"]);
        Assert.Equal("1536", result.Metadata["TotalClusters"]);
        Assert.Equal("4096", result.Metadata["ClusterSize"]);
        Assert.Equal(VolumeBytes.ToString(), result.Metadata["VolumeSize"]);
        Assert.Equal(VolumeBytes.ToString(), result.Metadata["ImageSize"]);
        Assert.Equal("declared capacity matches the image length", result.Metadata["CapacityEquivalence"]);
        Assert.Equal("6", result.Metadata["FileCount"]);
        Assert.Equal("10", result.Metadata["EntryCount"]);
        Assert.Equal("305629", result.Metadata["TotalDataBytes"]);
        Assert.Contains("image/dcp.mbn", result.Entries);
        Assert.Contains(CjkPath, result.Entries);
        Assert.Contains("logs/deep/nested", result.Entries);
    }

    [Fact]
    public async Task UnpackAsync_KernelWrittenVolume_WritesEveryFileByteIdentical()
    {
        using var temp = new TempDirectory();
        var image = UnpackFixture(temp, VolumeBytes);
        var target = Path.Combine(temp.Path, "out");

        var result = await Handler().UnpackAsync(image, target, new UnpackOptions(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("0", result.Metadata["UnreadableEntries"]);
        foreach (var (path, payload) in Manifest)
        {
            AssertFile(target, path, payload);
        }

        Assert.True(Directory.Exists(Path.Combine(target, "logs", "deep", "nested")));
    }

    [Fact]
    public async Task UnpackAsync_VolumeCutInsideFileClusters_WritesCoveredEntriesAndReportsUnreadableTail()
    {
        using var temp = new TempDirectory();
        var image = UnpackFixture(temp, 2_200_000);
        var target = Path.Combine(temp.Path, "out");

        var result = await Handler().UnpackAsync(image, target, new UnpackOptions(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("2", result.Metadata["UnreadableEntries"]);
        Assert.Equal(4, result.Entries.Count);
        foreach (var path in result.Entries)
        {
            var payload = Manifest.First(entry => entry.Path == path).Payload;
            AssertFile(target, path, payload);
        }

        Assert.False(File.Exists(Path.Combine(target, "image", "dcp.mbn")));
        Assert.Contains("exceeds the image length", result.Metadata["CapacityEquivalence"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpackAsync_VolumeCutInsideDirectoryTree_FailsInsteadOfPublishingPartialTree()
    {
        using var temp = new TempDirectory();
        var image = UnpackFixture(temp, 2_100_000);

        var result = await Handler().UnpackAsync(image, Path.Combine(temp.Path, "out"), new UnpackOptions(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Failed to unpack EXFAT", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpackAsync_OutputBudgetExceeded_FailsAndKeepsCoveredPrefix()
    {
        using var temp = new TempDirectory();
        var image = UnpackFixture(temp, VolumeBytes);
        var target = Path.Combine(temp.Path, "out");

        var result = await Handler().UnpackAsync(image, target,
            new UnpackOptions { MaximumOutputBytes = 1_000 }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("output budget", result.Summary, StringComparison.Ordinal);
        AssertFile(target, "image/small.bin", Pattern(512));
        Assert.False(File.Exists(Path.Combine(target, "spanning.bin")));
        Assert.False(File.Exists(Path.Combine(target, "image", "dcp.mbn")));
    }

    [Fact]
    public void ReadFile_KernelWrittenVolume_ReturnsDeclaredBytesForClusterSpanningAndUnicodeNames()
    {
        using var temp = new TempDirectory();
        var image = UnpackFixture(temp, VolumeBytes);
        using var parser = new ExFatParser(image);

        foreach (var (path, payload) in new[] { ("spanning.bin", Pattern(4_097)), (CjkPath, Pattern(1_000)) })
        {
            using var output = new MemoryStream();
            parser.ReadFile(path, output);
            Assert.Equal(payload, output.ToArray());
        }

        Assert.Throws<FileNotFoundException>(() => parser.ReadFile("missing.bin", Stream.Null));
    }

    [Fact]
    public void IsExFatImage_ForeignBootRecordsAndShortFiles_ReturnFalse()
    {
        using var temp = new TempDirectory();
        var fat = new FatImageBuilder(12)
            .AddFile("A.TXT", [1, 2, 3])
            .WriteTo(temp.Path + Path.DirectorySeparatorChar + "fat.img");
        var ext4Header = new byte[4_096];
        ext4Header[510] = 0x55;
        ext4Header[511] = 0xAA;
        BinaryPrimitivesWrite(ext4Header, 1_080, 0xEF53);

        Assert.False(ExFatParser.IsExFatImage(fat));
        Assert.False(ExFatParser.IsExFatImage(temp.WriteBytes("ext4.img", ext4Header)));
        Assert.False(ExFatParser.IsExFatImage(temp.WriteBytes("zeros.img", new byte[8_192])));
        Assert.False(ExFatParser.IsExFatImage(temp.WriteBytes("short.img", [0x55, 0xAA])));
        Assert.False(ExFatParser.IsExFatImage(Path.Combine(temp.Path, "missing.img")));
        Assert.False(ExFatParser.IsExFatImage(temp.WriteBytes("badgeom.img", BootRecordWithHeapBeyondImage())));
    }

    [Fact]
    public void IsFatImage_KernelWrittenExFatVolume_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var image = UnpackFixture(temp, VolumeBytes);

        Assert.False(FatParser.IsFatImage(image));
    }

    private static string CjkPath => "logs/deep/nested/启动日志 2026-01-02 记录.txt";

    private static (string Path, byte[] Payload)[] Manifest =>
    [
        ("README.TXT", "exFAT unpack fixture"u8.ToArray()),
        ("image/small.bin", Pattern(512)),
        ("empty.dat", []),
        ("spanning.bin", Pattern(4_097)),
        ("image/dcp.mbn", Pattern(300_000)),
        (CjkPath, Pattern(1_000)),
    ];

    private static IReadonlyFormatHandler Handler() =>
        FormatDetector.RegisteredHandlers.First(handler => handler.FormatName == "EXFAT");

    private static byte[] Pattern(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++)
        {
            payload[index] = (byte)((index * 7 + index % 251) % 256);
        }

        return payload;
    }

    private static string UnpackFixture(TempDirectory temp, long length)
    {
        var asset = Path.Combine(AppContext.BaseDirectory, "TestData", "Fat", FixtureAsset);
        using var compressed = File.OpenRead(asset);
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var image = new FileStream(Path.Combine(temp.Path, "kernel-volume.img"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        if (length == VolumeBytes)
        {
            deflate.CopyTo(image);
            return image.Name;
        }

        var prefix = new byte[length];
        deflate.ReadExactly(prefix);
        image.Write(prefix);

        return image.Name;
    }

    private static void AssertFile(string root, string relativePath, byte[] expected)
    {
        var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(target), $"Expected '{relativePath}' to be extracted.");
        Assert.Equal(expected, File.ReadAllBytes(target));
    }

    private static void BinaryPrimitivesWrite(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static byte[] BootRecordWithHeapBeyondImage()
    {
        var boot = new byte[8_192];
        boot[510] = 0x55;
        boot[511] = 0xAA;
        "EXFAT   "u8.CopyTo(boot.AsSpan(3));
        boot[0x6C] = 9;
        boot[0x6D] = 3;
        boot[0x6E] = 1;
        BinaryPrimitivesWrite(boot, 0x48, 16_384);
        BinaryPrimitivesWrite(boot, 0x50, 2_048);
        BinaryPrimitivesWrite(boot, 0x54, 16);
        BinaryPrimitivesWrite(boot, 0x58, 4_096);
        BinaryPrimitivesWrite(boot, 0x5C, 1_536);
        BinaryPrimitivesWrite(boot, 0x60, 5);
        return boot;
    }
}