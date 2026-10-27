using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Fat;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware.Fat;

public class FatBuilderTests
{
    private static readonly byte[] Readme = Encoding.UTF8.GetBytes("firmware readme payload");

    public FatBuilderTests() => FirmwareFormatRegistry.RegisterAll();

    [Fact]
    public void Build_AutoSmallTree_WritesFat12WithLongNamesAndExactCapacity()
    {
        using var temp = new TempDirectory();
        var source = CreateSourceTree(temp);
        var image = Path.Combine(temp.Path, "auto.img");

        var report = FatBuilder.Build(source, image, new FatBuildOptions { VolumeLabel = "ANDROID" });

        Assert.Equal("FAT12", report.Variant);
        Assert.Equal(new FileInfo(image).Length, report.VolumeBytes);
        using var parser = new FatParser(image);
        Assert.Equal("FAT12", parser.Variant);
        Assert.Equal("ANDROID", parser.VolumeLabel);
        Assert.Equal(parser.DeclaredVolumeBytes, parser.ImageBytes);
        Assert.Contains("logs/启动日志 2026-01-02 记录.txt", parser.ListFiles().Select(entry => entry.Path));
        AssertReads(parser, "readme.txt", Readme);
        AssertReads(parser, "EMPTY.DAT", []);
        AssertReads(parser, "logs/启动日志 2026-01-02 记录.txt", NestedPayload);
    }

    [Fact]
    public void Build_ExplicitFat16_PadsToTheFat16ClusterFloor()
    {
        using var temp = new TempDirectory();
        var source = CreateSourceTree(temp);
        var image = Path.Combine(temp.Path, "fat16.img");

        var report = FatBuilder.Build(source, image, new FatBuildOptions { Variant = FatVariant.Fat16 });
        var parser = new FatParser(image);
        using (parser)
        {
            Assert.Equal("FAT16", report.Variant);
            Assert.Equal("FAT16", parser.Variant);
            Assert.Equal(4_085, parser.TotalClusters);
            Assert.Equal(512, parser.MaxRootDirectoryEntries);
            AssertReads(parser, "readme.txt", Readme);
            AssertReads(parser, "images/pdp.mbn", NestedPayload);
        }
    }

    [Fact]
    public void Build_ExplicitFat32_PadsToTheFat32ClusterFloorAndKeepsRootCluster()
    {
        using var temp = new TempDirectory();
        var source = CreateSourceTree(temp);
        var image = Path.Combine(temp.Path, "fat32.img");

        var report = FatBuilder.Build(source, image, new FatBuildOptions { Variant = FatVariant.Fat32 });
        using var parser = new FatParser(image);
        Assert.Equal("FAT32", report.Variant);
        Assert.Equal("FAT32", parser.Variant);
        Assert.Equal(65_525, parser.TotalClusters);
        Assert.Equal(32, parser.ReservedSectorCount);
        Assert.Equal(0, parser.MaxRootDirectoryEntries);
        AssertReads(parser, "readme.txt", Readme);
        AssertReads(parser, "logs/启动日志 2026-01-02 记录.txt", NestedPayload);
    }

    [Fact]
    public void Build_ContentWiderThanRequestedVariant_FailsAndKeepsPreviousOutput()
    {
        using var temp = new TempDirectory();
        var source = CreateSourceTree(temp);
        var image = Path.Combine(temp.Path, "guarded.img");
        FatBuilder.Build(source, image, new FatBuildOptions());
        var before = File.ReadAllBytes(image);

        var oversized = Path.Combine(temp.Path, "oversized");
        Directory.CreateDirectory(oversized);
        File.WriteAllBytes(Path.Combine(oversized, "big.bin"), new byte[3 * 1024 * 1024]);

        Assert.Throws<InvalidDataException>(() =>
            FatBuilder.Build(oversized, image, new FatBuildOptions { Variant = FatVariant.Fat12 }));
        Assert.Equal(before, File.ReadAllBytes(image));
    }

    [Fact]
    public async Task PackAsync_UnpackedVolume_RebuildsRecordedGeometryAndContent()
    {
        using var temp = new TempDirectory();
        var source = CreateSourceTree(temp);
        var image = Path.Combine(temp.Path, "source.img");
        var original = FatBuilder.Build(source, image,
            new FatBuildOptions { Variant = FatVariant.Fat16, VolumeLabel = "EFI", TotalBytes = 8L * 1024 * 1024 });

        var handler = FormatDetector.RegisteredHandlers.OfType<IWritableFormatHandler>()
            .First(candidate => candidate.FormatName == "FAT");
        var unpacked = Path.Combine(temp.Path, "unpacked");
        var unpackedResult = await RepackMetadata.UnpackAsync(handler, image, unpacked, new UnpackOptions(),
            TestContext.Current.CancellationToken,
            () => handler.UnpackAsync(image, unpacked, null, TestContext.Current.CancellationToken));
        Assert.True(unpackedResult.Success, unpackedResult.Summary);
        var metadata = RepackMetadata.Read(unpacked);
        Assert.NotNull(metadata);
        Assert.True(metadata!.CanPackDirectory);
        Assert.Equal("fat16", metadata.PackingOptions["FatVariant"]);
        Assert.Equal(original.VolumeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            metadata.PackingOptions["TotalBytes"]);

        var rebuiltImage = Path.Combine(temp.Path, "rebuilt.img");
        var packResult = await handler.PackAsync(unpacked, rebuiltImage, TestContext.Current.CancellationToken);

        Assert.True(packResult.Success, packResult.Summary);
        Assert.Equal("FAT16", packResult.Metadata["Format"]);
        using var originalParser = new FatParser(image);
        using var rebuiltParser = new FatParser(rebuiltImage);
        Assert.Equal(originalParser.Variant, rebuiltParser.Variant);
        Assert.Equal(originalParser.DeclaredVolumeBytes, rebuiltParser.DeclaredVolumeBytes);
        Assert.Equal(originalParser.BytesPerSector, rebuiltParser.BytesPerSector);
        Assert.Equal(originalParser.SectorsPerCluster, rebuiltParser.SectorsPerCluster);
        Assert.Equal(originalParser.VolumeLabel, rebuiltParser.VolumeLabel);
        AssertReads(rebuiltParser, "readme.txt", Readme);
        AssertReads(rebuiltParser, "logs/启动日志 2026-01-02 记录.txt", NestedPayload);
        Assert.DoesNotContain(rebuiltParser.ListFiles(), entry => entry.Path.Contains("metadata.json", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] NestedPayload => _nestedPayload ??= BuildNestedPayload();

    private static byte[]? _nestedPayload;

    private static byte[] BuildNestedPayload()
    {
        var payload = new byte[3_013];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    private static string CreateSourceTree(TempDirectory temp)
    {
        var root = Path.Combine(temp.Path, "tree");
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllBytes(Path.Combine(root, "readme.txt"), Readme);
        File.WriteAllBytes(Path.Combine(root, "EMPTY.DAT"), []);
        File.WriteAllBytes(Path.Combine(root, "logs", "启动日志 2026-01-02 记录.txt"), NestedPayload);
        File.WriteAllBytes(Path.Combine(root, "images", "pdp.mbn"), NestedPayload);
        return root;
    }

    private static void AssertReads(FatParser parser, string path, byte[] expected)
    {
        using var output = new MemoryStream();
        parser.ReadFile(path, output);
        Assert.Equal(expected, output.ToArray());
    }
}