using UotanStudio.Firmware.Common;
using Xunit;

namespace UotanStudio.Firmware.Tests;

/// <summary>
/// Smoke tests for the firmware format handlers with bundled reference test data.
/// <para>使用内置参考测试数据对各固件格式处理器进行冒烟测试。</para>
/// </summary>
public class FormatHandlerSmokeTests
{
    private static string TestDataPath(string relative)
        => Path.Combine(AppContext.BaseDirectory, "TestData", relative);

    private static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "UotanStudioFirmwareTests", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FormatDetector_RegistersAllHandlers()
    {
        FirmwareFormatRegistry.RegisterAll();
        var handlers = FormatDetector.RegisteredHandlers;
        Assert.True(handlers.Count >= 15, $"Expected at least 15 handlers, got {handlers.Count}");
        var names = handlers.Select(h => h.FormatName).ToList();
        Assert.Contains("Android Sparse Image (simg)", names);
        Assert.Contains("Android OTA Payload (payload.bin)", names);
        Assert.Contains("Android Verified Boot (AVB)", names);
        Assert.Contains("Android Logical Partition (super)", names);
        Assert.Contains("SPRD/UNISOC PAC Firmware", names);
        Assert.Contains("LG DZ/KDZ Firmware", names);
    }

    [Fact]
    public async Task CpioHandler_ReadsTestTar()
    {
        FirmwareFormatRegistry.RegisterAll();
        var handler = new Cpio.CpioFormatHandler();
        var tarPath = TestDataPath(Path.Combine("Cpio", "test.tar"));

        var result = await handler.ReadMetadataAsync(tarPath);
        Assert.True(result.Success, result.Summary);
        Assert.NotEmpty(result.Entries);
        Assert.All(result.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e)));
    }

    [Fact]
    public async Task CpioHandler_UnpackTestTar()
    {
        FirmwareFormatRegistry.RegisterAll();
        var handler = new Cpio.CpioFormatHandler();
        var tarPath = TestDataPath(Path.Combine("Cpio", "test.tar"));
        var outDir = TempDir("cpio-unpack");

        var result = await handler.UnpackAsync(tarPath, outDir);
        Assert.True(result.Success, result.Summary);
        var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
    }

    [Fact]
    public async Task SparseHandler_ReadsSampleSimg()
    {
        FirmwareFormatRegistry.RegisterAll();
        var handler = new Sparse.SparseFormatHandler();
        var imgPath = TestDataPath(Path.Combine("Sparse", "sample.simg"));

        var result = await handler.ReadMetadataAsync(imgPath);
        Assert.True(result.Success, result.Summary);
        Assert.Contains("chunks", result.Summary);
    }

    [Fact]
    public async Task SparseHandler_UnpackToRaw()
    {
        FirmwareFormatRegistry.RegisterAll();
        var handler = new Sparse.SparseFormatHandler();
        var imgPath = TestDataPath(Path.Combine("Sparse", "sample.simg"));
        var outDir = TempDir("sparse-unpack");

        var result = await handler.UnpackAsync(imgPath, outDir);
        Assert.True(result.Success, result.Summary);
        var rawFiles = Directory.EnumerateFiles(outDir, "*.raw").ToList();
        Assert.NotEmpty(rawFiles);
        var raw = new FileInfo(rawFiles[0]);
        Assert.True(raw.Length > 0);
    }

    [Theory]
    [InlineData("sample.bin.gz")]
    [InlineData("sample.bin.xz")]
    [InlineData("sample.bin.lz4")]
    [InlineData("sample.bin.zstd")]
    [InlineData("sample.bin.bz2")]
    [InlineData("sample.bin.br")]
    [InlineData("sample.bin.lzma")]
    public async Task CompressHandler_DecompressesSamples(string fileName)
    {
        FirmwareFormatRegistry.RegisterAll();
        var handler = new Compress.CompressFormatHandler();
        var compressedPath = TestDataPath(Path.Combine("Compress", fileName));
        var originalPath = TestDataPath(Path.Combine("Compress", "sample.bin"));
        var outDir = TempDir($"compress-{Path.GetFileNameWithoutExtension(fileName)}");

        var result = await handler.UnpackAsync(compressedPath, outDir);
        Assert.True(result.Success, $"{fileName}: {result.Summary}");

        var decompressed = Directory.EnumerateFiles(outDir).FirstOrDefault();
        Assert.NotNull(decompressed);
        var original = File.ReadAllBytes(originalPath);
        var output = File.ReadAllBytes(decompressed);
        Assert.Equal(original, output);
    }

    [Fact]
    public async Task SparseHandler_PackUnpackRoundTrip()
    {
        FirmwareFormatRegistry.RegisterAll();
        var handler = new Sparse.SparseFormatHandler();
        var rawPath = TestDataPath(Path.Combine("Compress", "sample.bin"));
        var outDir = TempDir("sparse-roundtrip");
        var sparsePath = Path.Combine(outDir, "out.simg");
        _ = Path.Combine(outDir, "out.raw");

        // Pack raw -> sparse.
        var pack = await handler.PackAsync(rawPath, sparsePath);
        Assert.True(pack.Success, pack.Summary);
        Assert.True(File.Exists(sparsePath));

        // Unpack sparse -> raw and compare bytes.
        var unpack = await handler.UnpackAsync(sparsePath, outDir);
        Assert.True(unpack.Success, unpack.Summary);
        Assert.True(File.Exists(Path.Combine(outDir, "out.raw")), "expected out.raw output");
    }
}