using System.Text;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Archive.Writing;
using UotanStudio.Firmware.Core.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed partial class CpioCompressedRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cpio-compressed-{Guid.NewGuid():N}");

    [Fact]
    public async Task PackAsync_CancelledOrInvalidMetadata_PreservesExistingOutput()
    {
        Directory.CreateDirectory(_root);
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        await File.WriteAllTextAsync(Path.Combine(extracted, "init.rc"), "boot",
            TestContext.Current.CancellationToken);
        var target = Path.Combine(_root, "ramdisk.cpio.gz");
        var original = Encoding.UTF8.GetBytes("old output");
        await File.WriteAllBytesAsync(target, original, TestContext.Current.CancellationToken);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new CpioFormatHandler().PackAsync(extracted, target, cancelled.Token));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));

        var metadata = new RepackMetadata
        {
            FormatName = "CPIO/TAR Archive",
            SourceFileName = "ramdisk.cpio.gz",
        };
        metadata.Configuration["cpio"] = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = "CrcAscii",
            ["compression"] = "Unsupported",
            ["entries"] = new System.Text.Json.Nodes.JsonArray(),
        };
        await metadata.SaveAsync(extracted, TestContext.Current.CancellationToken);
        var result = await new CpioFormatHandler().PackAsync(extracted, target,
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}