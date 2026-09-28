using System.Buffers.Binary;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Workflow;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AndroidSecurityDisableServiceTests
{
    [Fact]
    public async Task ApplyAsync_FstabAndVbmeta_PublishesConsistentOutputs()
    {
        using var temp = new TempDirectory();
        var fstab = temp.Write("input/fstab.vendor",
            "/dev/block/system /system ext4 ro,barrier=1 wait,avb=vbmeta_system\n" +
            "/dev/block/userdata /data f2fs rw wait,fileencryption=aes-256-xts,forcefdeorfbe=footer\n");
        var vbmeta = temp.WriteBytes("input/vbmeta.img", CreateVbmeta());
        var output = Path.Combine(temp.Path, "output");

        var result = await AndroidSecurityDisableService.ApplyAsync(new AndroidSecurityDisableRequest
        {
            FstabPaths = [fstab],
            VbmetaPath = vbmeta,
            OutputDirectory = output
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.OutputPaths.Count);
        var text = await File.ReadAllTextAsync(Path.Combine(output, "fstab.vendor"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("avb", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileencryption", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forcefdeorfbe", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("barrier=1", text, StringComparison.Ordinal);
        var patchedVbmeta = await File.ReadAllBytesAsync(Path.Combine(output, "vbmeta.img"), TestContext.Current.CancellationToken);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(patchedVbmeta.AsSpan(120, 4));
        Assert.Equal(AvbSigner.VbmetaFlagHashtreeDisabled | AvbSigner.VbmetaFlagVerificationDisabled, flags);
    }

    [Fact]
    public async Task ApplyAsync_InvalidVbmeta_PreservesExistingOutputs()
    {
        using var temp = new TempDirectory();
        var fstab = temp.Write("input/fstab.vendor", "/dev/block/system /system ext4 ro wait,avb\n");
        var vbmeta = temp.WriteBytes("input/vbmeta.img", "invalid"u8.ToArray());
        var oldFstab = "old fstab"u8.ToArray();
        var oldVbmeta = "old vbmeta"u8.ToArray();
        temp.WriteBytes("output/fstab.vendor", oldFstab);
        temp.WriteBytes("output/vbmeta.img", oldVbmeta);
        var output = Path.Combine(temp.Path, "output");

        await Assert.ThrowsAsync<InvalidDataException>(() => AndroidSecurityDisableService.ApplyAsync(
            new AndroidSecurityDisableRequest
            {
                FstabPaths = [fstab],
                VbmetaPath = vbmeta,
                OutputDirectory = output
            }, TestContext.Current.CancellationToken));

        Assert.Equal(oldFstab, await File.ReadAllBytesAsync(Path.Combine(output, "fstab.vendor"), TestContext.Current.CancellationToken));
        Assert.Equal(oldVbmeta, await File.ReadAllBytesAsync(Path.Combine(output, "vbmeta.img"), TestContext.Current.CancellationToken));
    }

    private static byte[] CreateVbmeta()
    {
        var bytes = new byte[256];
        "AVB0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 1);
        return bytes;
    }
}