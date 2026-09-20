using UotanStudio.Firmware.BoundedContexts.BootSecurity.Kernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class KernelToolchainServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-kernel-toolchain-" + Guid.NewGuid().ToString("N"));

    public KernelToolchainServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadKallsymsTextAsync_ValidLines_ParsesSymbols()
    {
        var path = Path.Combine(_root, "kallsyms.txt");
        await File.WriteAllTextAsync(path, "ffffff8008000000 T _stext\nffffff8008001000 t secondary_startup\n",
            TestContext.Current.CancellationToken);

        var symbols = await KernelToolchainService.ReadKallsymsTextAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(2, symbols.Count);
        Assert.Equal("_stext", symbols[0].Name);
        Assert.Equal('t', symbols[1].Type);
    }

    [Fact]
    public async Task AnalyzeAsync_GzipKernel_ReportsCompressionAndRelease()
    {
        var path = Path.Combine(_root, "kernel.gz");
        await using (var output = new FileStream(path, FileMode.CreateNew))
        await using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
            await gzip.WriteAsync("Linux version 6.1.12-android14-11\0"u8.ToArray(), TestContext.Current.CancellationToken);

        var result = await KernelToolchainService.AnalyzeAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models.CompressionType.Gzip, result.Compression);
        Assert.Equal("6.1.12-android14-11", result.Release);
        Assert.True(result.DecompressedLength > 0);
    }

    public void Dispose() => Directory.Delete(_root, true);
}