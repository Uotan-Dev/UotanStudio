using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.VendorFirmware.Dz;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class MetadataOnlyUnpackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uot-metadata-only-" + Guid.NewGuid().ToString("N"));

    public MetadataOnlyUnpackTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task UnpackAsync_KdzWithMetadataOnly_PublishesMetadataAndLayoutWithoutMembers()
    {
        var source = await KdzWithMembersAsync("container.kdz");
        var output = Path.Combine(_root, "metadata-only");

        FirmwareOpResult result;
        using (RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory))
        {
            result = await new DzFormatHandler().UnpackAsync(source, output,
                new UnpackOptions { MetadataOnly = true }, TestContext.Current.CancellationToken);
        }

        Assert.True(result.Success, result.Summary);
        var files = Directory.GetFiles(output).Select(Path.GetFileName).ToArray();
        Assert.Contains(RepackMetadata.FileName, files);
        Assert.Contains(files, name => name!.StartsWith(".kdz-layout-", StringComparison.Ordinal));
        Assert.DoesNotContain("abl", files);
        Assert.DoesNotContain("aop", files);
        var metadata = RepackMetadata.Read(output);
        Assert.NotNull(metadata?.Configuration["kdz"]);
    }

    [Fact]
    public async Task UnpackAsync_KdzWithoutMetadataOnly_WritesMemberFiles()
    {
        var source = await KdzWithMembersAsync("control.kdz");
        var output = Path.Combine(_root, "full-unpack");

        FirmwareOpResult result;
        using (RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory))
        {
            result = await new DzFormatHandler().UnpackAsync(source, output,
                new UnpackOptions(), TestContext.Current.CancellationToken);
        }

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(Path.Combine(output, "abl")));
        Assert.True(File.Exists(Path.Combine(output, "aop")));
    }

    [Fact]
    public async Task PackAsync_MetadataOnlyDirectory_ReportsFailureAndWritesNoContainer()
    {
        var source = await KdzWithMembersAsync("repack-guard.kdz");
        var output = Path.Combine(_root, "metadata-only-for-pack");
        var rebuilt = Path.Combine(_root, "rebuilt.kdz");
        using (RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory))
        {
            var handler = new DzFormatHandler();
            var unpacked = await handler.UnpackAsync(source, output,
                new UnpackOptions { MetadataOnly = true }, TestContext.Current.CancellationToken);
            Assert.True(unpacked.Success, unpacked.Summary);

            var packed = await handler.PackAsync(output, rebuilt, TestContext.Current.CancellationToken);
            Assert.False(packed.Success);
        }

        Assert.False(File.Exists(rebuilt));
    }

    [Fact]
    public async Task UnpackAsync_FormatWithoutMetadataOnlySupport_FailsAndCreatesNothing()
    {
        var output = Path.Combine(_root, "unsupported");
        var handler = new CpioFormatHandler();

        var result = await handler.UnpackAsync(Path.Combine(_root, "absent.cpio"), output,
            new UnpackOptions { MetadataOnly = true }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(handler.FormatName, result.Summary, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task UnpackAsync_MetadataOnlyWithCancel_ThrowsBeforePublishing()
    {
        var source = await KdzWithMembersAsync("cancelled.kdz");
        var output = Path.Combine(_root, "cancelled-output");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DzFormatHandler().UnpackAsync(source, output,
            new UnpackOptions { MetadataOnly = true }, cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    private async Task<string> KdzWithMembersAsync(string containerName)
    {
        var first = Path.Combine(_root, Path.GetFileNameWithoutExtension(containerName) + ".abl");
        var second = Path.Combine(_root, Path.GetFileNameWithoutExtension(containerName) + ".aop");
        File.WriteAllBytes(first, [1, 2, 3, 4]);
        File.WriteAllBytes(second, [5, 6, 7]);
        var source = Path.Combine(_root, containerName);
        await KdzBuilder.BuildAsync([new KdzSource("abl", first), new KdzSource("aop", second)], source,
            cancellationToken: TestContext.Current.CancellationToken);
        return source;
    }

    public void Dispose() => Directory.Delete(_root, true);
}