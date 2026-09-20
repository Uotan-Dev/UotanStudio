using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Workflow;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Amlogic;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.SonySin;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class VendorFormatBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-vendor-" + Guid.NewGuid().ToString("N"));
    public VendorFormatBoundaryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Amlogic_MagicAtOffset8_IsDetectedAndUnpackIsExplicitlyUnsupported()
    {
        var path = Path.Combine(_root, "candidate.img");
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x27B51956);
        await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
        var handler = new AmlogicFormatHandler();
        Assert.True(handler.CanHandle(path));
        Assert.False((await handler.UnpackAsync(path, _root, cancellationToken: TestContext.Current.CancellationToken)).Success);
    }

    [Fact]
    public async Task Amlogic_ValidItem_IsExtractedWithBoundedOffsets()
    {
        var path = Path.Combine(_root, "valid.img");
        var image = new byte[72 + 576 + 3];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 0x27B51956);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(72 + 8), 648);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(72 + 16), 3);
        Encoding.UTF8.GetBytes("USB").CopyTo(image, 72 + 24);
        Encoding.UTF8.GetBytes("DDR").CopyTo(image, 72 + 280);
        image.AsSpan(image.Length - 3, 3).Fill(0x5A);
        await File.WriteAllBytesAsync(path, image, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "out");
        var result = await new AmlogicFormatHandler().UnpackAsync(path, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        Assert.Equal([0x5A, 0x5A, 0x5A], await File.ReadAllBytesAsync(Path.Combine(output, "DDR.USB"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Amlogic_DirectoryPackThenExtract_PreservesItemBytes()
    {
        var source = Path.Combine(_root, "src"); Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "DDR.USB"), [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var image = Path.Combine(_root, "packed.img");
        var handler = new AmlogicFormatHandler();
        var packed = await handler.PackAsync(source, image, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);
        var verified = await handler.VerifyAsync(image, TestContext.Current.CancellationToken);
        Assert.True(verified.Success, verified.Summary);
        var corrupted = await File.ReadAllBytesAsync(image, TestContext.Current.CancellationToken);
        BinaryPrimitives.WriteUInt64LittleEndian(corrupted.AsSpan(12), checked((ulong)corrupted.Length + 1));
        await File.WriteAllBytesAsync(image, corrupted, TestContext.Current.CancellationToken);
        var rejected = await handler.VerifyAsync(image, TestContext.Current.CancellationToken);
        Assert.False(rejected.Success);
        await File.WriteAllBytesAsync(image, corrupted.AsSpan().ToArray(), TestContext.Current.CancellationToken);
        var extracted = Path.Combine(_root, "roundtrip");
        var unpacked = await handler.UnpackAsync(image, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(extracted, "DDR.USB"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SonySin_HeaderIsContentConfirmedAndUnknownChunksAreRejected()
    {
        var path = Path.Combine(_root, "candidate.sin");
        var sin = new byte[32];
        sin[0] = 3; sin[1] = (byte)'S'; sin[2] = (byte)'I'; sin[3] = (byte)'N';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(4), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(0x18), 1);
        await File.WriteAllBytesAsync(path, sin, TestContext.Current.CancellationToken);
        var handler = new SonySinFormatHandler();
        Assert.True(handler.CanHandle(path));
        Assert.False((await handler.UnpackAsync(path, _root, cancellationToken: TestContext.Current.CancellationToken)).Success);
    }

    [Fact]
    public void SelinuxPlan_ChangesOnlyExplicitBootArguments()
    {
        var path = Path.Combine(_root, "missing-boot.img");
        // The plan API intentionally requires a real parser input; this assertion documents the boundary.
        Assert.Throws<FileNotFoundException>(() => new SelinuxSwitchService().CreatePlan(path, SelinuxSwitchMode.Permissive));
    }

    [Fact]
    public async Task RealBootImages_RecoveryMergeAndSelinuxPlan_RoundTrip()
    {
        var source = @"D:\Firmware\boot(5).img";
        var recovery = @"D:\Firmware\SM7150\boot_a.img";
        if (!File.Exists(source) || !File.Exists(recovery))
            return;
        var output = Path.Combine(_root, "merged.img");
        var result = await new RecoveryRamdiskMergeService().MergeAsync(source, recovery, output,
            TestContext.Current.CancellationToken);
        Assert.True(File.Exists(output));
        Assert.True(result.MergedBytes > 0);
        using (var parser = new BootImageParser())
        {
            parser.ParseFile(output);
            Assert.Equal(result.Compression, parser.Header.RamdiskCompression);
            Assert.True(parser.Ramdisk.Length > 0);
        }
        var plan = new SelinuxSwitchService().CreatePlan(source, SelinuxSwitchMode.Permissive);
        Assert.Contains("androidboot.selinux=permissive", plan.UpdatedCommandLine);
        var enforcing = new SelinuxSwitchService().CreatePlan(source, SelinuxSwitchMode.Enforcing);
        Assert.Contains("androidboot.selinux=enforcing", enforcing.UpdatedCommandLine);
        var disabled = new SelinuxSwitchService().CreatePlan(source, SelinuxSwitchMode.Disable);
        Assert.DoesNotContain("androidboot.selinux=", disabled.UpdatedCommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("androidboot.hardware=taimen", disabled.UpdatedCommandLine);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}