using System.Buffers.Binary;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbFooterEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-avb-footer-" + Guid.NewGuid().ToString("N"));

    public AvbFooterEditorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RemoveFooterAsync_ValidFooter_RestoresOriginalBytes()
    {
        var input = Path.Combine(_root, "signed.img");
        var output = Path.Combine(_root, "unsigned.img");
        var original = Enumerable.Range(0, 4097).Select(value => (byte)(value * 31)).ToArray();
        await File.WriteAllBytesAsync(input, BuildSignedImage(original), TestContext.Current.CancellationToken);

        var result = await AvbFooterEditor.RemoveFooterAsync(
            input, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(original, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal(original.Length.ToString(), result.Metadata["OriginalImageSize"]);
    }

    [Fact]
    public async Task RemoveFooterAsync_InvalidRanges_PreservesExistingOutput()
    {
        var input = Path.Combine(_root, "invalid.img");
        var output = Path.Combine(_root, "existing.img");
        var image = BuildSignedImage([1, 2, 3, 4]);
        BinaryPrimitives.WriteUInt64BigEndian(
            image.AsSpan(image.Length - AvbFooter.Size + 20, 8), checked((ulong)image.Length));
        await File.WriteAllBytesAsync(input, image, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(output, [9, 8, 7], TestContext.Current.CancellationToken);

        var result = await AvbFooterEditor.RemoveFooterAsync(
            input, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveFooterAsync_CanceledBeforeStart_PreservesExistingOutput()
    {
        var input = Path.Combine(_root, "signed.img");
        var output = Path.Combine(_root, "existing.img");
        await File.WriteAllBytesAsync(input, BuildSignedImage([1, 2, 3, 4]), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(output, [9, 8, 7], TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => AvbFooterEditor.RemoveFooterAsync(input, output, cancellationToken: cancellation.Token));

        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] BuildSignedImage(byte[] original)
    {
        var vbmeta = new byte[256];
        "AVB0"u8.CopyTo(vbmeta);
        var image = new byte[original.Length + vbmeta.Length + AvbFooter.Size];
        original.CopyTo(image, 0);
        vbmeta.CopyTo(image, original.Length);
        var footer = image.AsSpan(image.Length - AvbFooter.Size);
        "AVBf"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer[4..], AvbFooter.SupportedVersionMajor);
        BinaryPrimitives.WriteUInt32BigEndian(footer[8..], AvbFooter.SupportedVersionMinor);
        BinaryPrimitives.WriteUInt64BigEndian(footer[12..], checked((ulong)original.Length));
        BinaryPrimitives.WriteUInt64BigEndian(footer[20..], checked((ulong)original.Length));
        BinaryPrimitives.WriteUInt64BigEndian(footer[28..], checked((ulong)vbmeta.Length));
        return image;
    }
}