using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies that an AVB-signed boot image is detected on unpack and that rebuilding is refused
/// instead of silently dropping the footer. <para>测试在解包时检测 AVB 签名 boot 镜像，并拒绝重建而非静默丢弃 footer。</para>
/// </summary>
public sealed class BootRepackAvbFooterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-boot-avb-" + Guid.NewGuid().ToString("N"));
    private string Image => Path.Combine(_root, "boot.img");
    private string Extracted => Path.Combine(_root, "extracted");
    private string Output => Path.Combine(_root, "rebuilt.img");
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public BootRepackAvbFooterTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Unpack_AvbSignedBoot_RecordsFooterAndDisablesPacking(int version)
    {
        CreateBoot(version);
        AppendAvbFooter();

        var unpack = await new BootImageFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(unpack.Success, unpack.Summary);

        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.NotNull(metadata.Configuration["footer"]);
        Assert.Equal(64UL, metadata.Configuration["footer"]!["vbmetaSize"]!.GetValue<ulong>());
        Assert.False(metadata.CanPackDirectory);
        Assert.Contains("AVB footer", metadata.PackingNotes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Pack_AvbSignedBoot_FailsAndLeavesOutputIntact(int version)
    {
        CreateBoot(version);
        AppendAvbFooter();
        Assert.True((await new BootImageFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        File.WriteAllText(Output, "keep");

        var packed = await new BootImageFormatHandler().PackAsync(Extracted, Output, Cancellation);

        Assert.False(packed.Success);
        Assert.Contains("AVB footer", packed.Summary, StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(Output));
    }

    [Fact]
    public async Task Pack_UnsignedBoot_StillRoundTrips()
    {
        CreateBoot(4);
        Assert.True((await new BootImageFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        var metadata = RepackMetadata.Read(Extracted)!;
        Assert.Null(metadata.Configuration["footer"]);
        Assert.True(metadata.CanPackDirectory);

        var packed = await new BootImageFormatHandler().PackAsync(Extracted, Output, Cancellation);
        Assert.True(packed.Success, packed.Summary);
        Assert.Equal(File.ReadAllBytes(Image), File.ReadAllBytes(Output));
    }

    /// <summary>
    /// Genuine cross-validation: Google's avbtool (in WSL) writes a real hash footer onto a
    /// managed-built boot image, and the managed unpack must detect that independent footer and
    /// refuse to rebuild. <para>真实交叉验证：WSL 中的 Google avbtool 为托管生成的 boot 镜像写入真实
    /// hash footer，托管解包须检测到该独立 footer 并拒绝重建。</para>
    /// </summary>
    [Fact]
    public async Task Unpack_AvbToolSignedBoot_DetectsRealFooterAndRefusesPack()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.EnsureAvbTool(out var reason)) { Assert.Skip(reason); }

        CreateBoot(4);
        var keyPem = Path.Combine(_root, "test key.pem");
        using (var rsa = System.Security.Cryptography.RSA.Create(2048))
            await File.WriteAllTextAsync(keyPem, rsa.ExportPkcs8PrivateKeyPem(), Cancellation);

        var (output, exitCode) = WslTestHelpers.RunAvbTool(
            ["add_hash_footer", "--image", WslTestHelpers.ToWslPath(Image),
             "--partition_name", "boot", "--partition_size", "1048576",
             "--algorithm", "SHA256_RSA2048", "--key", WslTestHelpers.ToWslPath(keyPem)]);
        Assert.True(exitCode == 0, $"avbtool add_hash_footer failed: {output}");

        var unpack = await new BootImageFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(unpack.Success, unpack.Summary);

        var metadata = RepackMetadata.Read(Extracted)!;
        Assert.NotNull(metadata.Configuration["footer"]);
        Assert.False(metadata.CanPackDirectory);

        var packed = await new BootImageFormatHandler().PackAsync(Extracted, Output, Cancellation);
        Assert.False(packed.Success);
        Assert.Contains("AVB footer", packed.Summary, StringComparison.Ordinal);
    }

    private void CreateBoot(int version)
    {
        new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = (BootImageVersion)version,
                PageSize = version < 3 ? 2048u : 4096u,
                HeaderSize = version == 4 ? 1584u : version == 3 ? 1580u : 1664u,
                CommandLine = "console=ttyS0",
                ImageName = "test",
                KernelAddr = 0x8000,
                RamdiskAddr = 0x100000,
                TagsAddr = 0x100
            },
            Kernel = [1, 2, 3],
            Ramdisk = [4, 5, 6],
            Second = version < 3 ? [7, 8] : [],
            Signature = version == 4 ? [13, 14] : []
        }.PackToFile(Image);
    }

    private void AppendAvbFooter()
    {
        Span<byte> footer = stackalloc byte[AvbFooter.Size];
        "AVBf"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer[4..], AvbFooter.SupportedVersionMajor);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(12, 8), (ulong)new FileInfo(Image).Length);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(20, 8), (ulong)new FileInfo(Image).Length);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(28, 8), (ulong)AvbFooter.Size);
        using var stream = new FileStream(Image, FileMode.Append, FileAccess.Write);
        stream.Write(footer);
    }
}