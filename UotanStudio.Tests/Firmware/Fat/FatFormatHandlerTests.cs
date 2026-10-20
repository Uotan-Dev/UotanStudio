using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Fat;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware.Fat;

public class FatFormatHandlerTests
{
    public FatFormatHandlerTests() => FirmwareFormatRegistry.RegisterAll();

    [Fact]
    public void Detect_Fat12VolumeImage_SelectsFatHandler()
    {
        using var temp = new TempDirectory();
        var image = Build(temp, variant: 12, ("README.TXT"u8.ToArray(), 10));

        var handler = FormatDetector.Detect(image);

        Assert.NotNull(handler);
        Assert.Equal("FAT", handler!.FormatName);
    }

    [Fact]
    public async Task ReadMetadataAsync_Fat16Volume_ReportsGeometryAndEntries()
    {
        using var temp = new TempDirectory();
        var image = Build(temp, variant: 16, ("DATA.BIN"u8.ToArray(), 4_000));

        var result = await Handler(temp).ReadMetadataAsync(image, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("FAT16", result.Metadata["Format"]);
        Assert.Equal("512", result.Metadata["BytesPerSector"]);
        Assert.Equal("4100", result.Metadata["TotalClusters"]);
        Assert.Equal("2", result.Metadata["FatCount"]);
        Assert.Equal("declared capacity matches the image length", result.Metadata["CapacityEquivalence"]);
        Assert.Contains("LOGS/DATA.BIN", result.Entries);
    }

    [Fact]
    public void IsFatImage_Ext4SuperblockImage_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var header = new byte[4_096];
        header[510] = 0x55;
        header[511] = 0xAA;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1_080), 0xEF53);
        var path = temp.WriteBytes("rootfs.img", header);

        Assert.False(FatParser.IsFatImage(path));
        Assert.False(FatParser.IsFatImage(Path.Combine(temp.Path, "missing.img")));
        Assert.False(FatParser.IsFatImage(temp.WriteBytes("zeros.img", new byte[8_192])));
    }

    [Fact]
    public async Task UnpackAsync_LongUnicodeNamesAndBoundarySizes_WritesExactBytes()
    {
        using var temp = new TempDirectory();
        var readme = Encoding.UTF8.GetBytes("firmware readme");
        var log = new byte[1_000];
        Random.Shared.NextBytes(log);
        var exact = new byte[512];
        Random.Shared.NextBytes(exact);
        var span = new byte[1_500];
        Random.Shared.NextBytes(span);
        var image = new FatImageBuilder(12)
            .AddFile("README.TXT", readme)
            .AddFile("logs/启动日志 2026-01-02 记录.txt", log)
            .AddFile("EMPTY.DAT", [])
            .AddFile("EXACT.BIN", exact)
            .AddFile("span.bin", span)
            .WriteTo(temp.Path + Path.DirectorySeparatorChar + "sdcard.img");

        var target = Path.Combine(temp.Path, "out");
        var result = await Handler(temp).UnpackAsync(image, target, new UnpackOptions(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        AssertFile(target, "README.TXT", readme);
        AssertFile(target, "logs/启动日志 2026-01-02 记录.txt", log);
        AssertFile(target, "EMPTY.DAT", []);
        AssertFile(target, "EXACT.BIN", exact);
        AssertFile(target, "span.bin", span);
        Assert.Equal("0", result.Metadata["UnreadableEntries"]);
        Assert.True(Directory.Exists(Path.Combine(target, "logs")));
    }

    [Fact]
    public async Task UnpackAsync_TruncatedVolume_WritesCoveredEntriesAndReportsUnreadableTail()
    {
        using var temp = new TempDirectory();
        var kept = Encoding.UTF8.GetBytes("kept payload");
        var tail = new byte[3_000];
        Random.Shared.NextBytes(tail);
        var image = new FatImageBuilder(16)
            .AddFile("KEPT.BIN", kept)
            .AddFile("tail.bin", tail)
            .WriteTo(temp.Path + Path.DirectorySeparatorChar + "modemfirmware.img", truncateFinalFile: true);

        var target = Path.Combine(temp.Path, "out");
        var result = await Handler(temp).UnpackAsync(image, target, new UnpackOptions(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        AssertFile(target, "KEPT.BIN", kept);
        Assert.False(File.Exists(Path.Combine(target, "tail.bin")));
        Assert.Equal("1", result.Metadata["UnreadableEntries"]);
        Assert.DoesNotContain("tail.bin", result.Entries, StringComparer.Ordinal);
        Assert.Contains("exceeds the image length", result.Metadata["CapacityEquivalence"], StringComparison.Ordinal);
        Assert.True(long.Parse(result.Metadata["ImageSize"]) < long.Parse(result.Metadata["VolumeSize"]));
    }

    [Fact]
    public async Task UnpackAsync_OutputBudgetExceeded_FailsAndKeepsCoveredPrefix()
    {
        using var temp = new TempDirectory();
        var first = Encoding.UTF8.GetBytes("first file");
        var second = new byte[2_048];
        Random.Shared.NextBytes(second);
        var image = new FatImageBuilder(12)
            .AddFile("FIRST.BIN", first)
            .AddFile("SECOND.BIN", second)
            .WriteTo(temp.Path + Path.DirectorySeparatorChar + "budget.img");

        var target = Path.Combine(temp.Path, "out");
        var result = await Handler(temp).UnpackAsync(image, target,
            new UnpackOptions { MaximumOutputBytes = 1_024 }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("output budget", result.Summary, StringComparison.Ordinal);
        AssertFile(target, "FIRST.BIN", first);
        Assert.False(File.Exists(Path.Combine(target, "SECOND.BIN")));
    }

    [Fact]
    public void ReadFile_Fat32VolumeWithHighClusterField_ReturnsDeclaredBytes()
    {
        using var temp = new TempDirectory();
        var payload = new byte[900];
        Random.Shared.NextBytes(payload);
        var image = new FatImageBuilder(32)
            .AddFile("images/pdp.mbn", payload)
            .WriteTo(temp.Path + Path.DirectorySeparatorChar + "fat32.img");

        using var parser = new FatParser(image);
        Assert.Equal("FAT32", parser.Variant);
        Assert.Equal(32, parser.ReservedSectorCount);
        Assert.Equal(0, parser.MaxRootDirectoryEntries);
        using var output = new MemoryStream();
        parser.ReadFile("images/pdp.mbn", output);

        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public void ListFiles_Fat12Volume_ExposesDirectoriesBeforeChildrenAndRejectsUnknownPath()
    {
        using var temp = new TempDirectory();
        var image = Build(temp, variant: 12, ("NOTE.TXT"u8.ToArray(), 40));

        using var parser = new FatParser(image);
        var entries = parser.ListFiles();

        Assert.Contains(entries, entry => entry is { Kind: FatVolumeEntryKind.Directory, Path: "LOGS" });
        Assert.Contains("LOGS/NOTE.TXT", entries.Select(entry => entry.Path));
        Assert.Throws<FileNotFoundException>(() => parser.ReadFile("logs/missing.txt", Stream.Null));
    }

    private static void AssertFile(string root, string relativePath, byte[] expected)
    {
        var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(target), $"Expected '{relativePath}' to be extracted.");
        Assert.Equal(expected, File.ReadAllBytes(target));
    }

    private static string Build(TempDirectory temp, int variant, (byte[] NameBytes, int Size) file)
    {
        var name = Encoding.ASCII.GetString(file.NameBytes);
        var content = new byte[file.Size];
        Random.Shared.NextBytes(content);
        return new FatImageBuilder(variant)
            .AddFile("logs/" + name, content)
            .WriteTo(temp.Path + Path.DirectorySeparatorChar + "volume.img");
    }

    private static IReadonlyFormatHandler Handler(TempDirectory temp) =>
        FormatDetector.RegisteredHandlers.FirstOrDefault(handler => handler.FormatName == "FAT")
        ?? throw new InvalidOperationException($"No FAT handler registered while probing '{temp.Path}'.");
}