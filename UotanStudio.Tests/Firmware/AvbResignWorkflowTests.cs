using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Covers the AVB re-sign workflow: the high-level <see cref="AvbResigner.ResignImageAsync"/> entry
/// point dispatches correctly for hash and hashtree modes, publishes atomically, and its output is
/// accepted by the pinned Google <c>avbtool 1.3.0 verify-image</c> in Alpine when WSL is available.
/// <para>覆盖 AVB 重签名工作流：高层入口 <see cref="AvbResigner.ResignImageAsync"/> 按 hash / hashtree
/// 模式正确分派，原子发布；在 WSL 可用时其产物须被固定的 Google <c>avbtool 1.3.0 verify-image</c> 接受。</para>
/// </summary>
public sealed class AvbResignWorkflowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-avb-resign-" + Guid.NewGuid().ToString("N"));
    private RsaKeyParameters _key = null!;
    private string _keyPem = null!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        using var rsa = RSA.Create(2048);
        _key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
        _keyPem = Path.Combine(_root, "test key.pem");
        await File.WriteAllTextAsync(_keyPem, rsa.ExportPkcs8PrivateKeyPem(), Token);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ResignImage_HashMode_ProducesVerifiableFooter()
    {
        var image = await WritePatternedAsync("boot.img", 128 * 1024);
        var signed = Path.Combine(_root, "boot.signed.img");

        var result = await AvbResigner.ResignImageAsync(image, signed, _key,
            new AvbResignOptions { Mode = AvbResignMode.Hash, PartitionName = "boot" }, Token);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(signed));
        Assert.Equal(signed, result.OutputPath);

        var verify = AvbVerifier.Verify(await File.ReadAllBytesAsync(signed, Token));
        Assert.True(verify.IsSigned, verify.ErrorMessage);
        Assert.True(verify.IsValid, verify.ErrorMessage);
    }

    [Fact]
    public async Task ResignImage_HashtreeMode_ProducesVerifiableFooter()
    {
        var image = await WritePatternedAsync("vendor.img", 256 * 1024);
        var signed = Path.Combine(_root, "vendor.signed.img");

        var result = await AvbResigner.ResignImageAsync(image, signed, _key,
            new AvbResignOptions { Mode = AvbResignMode.Hashtree, PartitionName = "vendor" }, Token);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(signed));

        var verify = AvbVerifier.Verify(await File.ReadAllBytesAsync(signed, Token));
        Assert.True(verify.IsSigned, verify.ErrorMessage);
        Assert.True(verify.IsValid, verify.ErrorMessage);
    }

    [Fact]
    public async Task ResignImage_ExplicitSamePath_FailsWithoutTouchingSource()
    {
        var image = await WritePatternedAsync("system.img", 64 * 1024);
        var original = File.ReadAllBytes(image);

        var result = await AvbResigner.ResignImageAsync(image, image, _key,
            new AvbResignOptions { Mode = AvbResignMode.Hash, PartitionName = "system" }, Token);

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(image));
    }

    [Fact]
    public async Task AddHashFooter_ExistingOutput_PreservesOriginalUntilSignedCopyCompletes()
    {
        var image = await WritePatternedAsync("dtbo.img", 32 * 1024);
        var signed = Path.Combine(_root, "dtbo.signed.img");
        File.WriteAllText(signed, "keep");

        var result = await AvbSigner.AddHashFooterAsync(image, "dtbo", _key, signed, Token);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEqual("keep", File.ReadAllText(signed));

        // No leftover temp files: the atomic publication must clean up.
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task ResignImage_MissingInputFile_FailsHonestly()
    {
        var signed = Path.Combine(_root, "missing-output.img");
        var result = await AvbResigner.ResignImageAsync(
            Path.Combine(_root, "does-not-exist.img"), signed, _key,
            new AvbResignOptions { Mode = AvbResignMode.Hash, PartitionName = "x" }, Token);
        Assert.False(result.Success);
        Assert.False(File.Exists(signed));
    }

    [Fact]
    public async Task ResignImage_HashMode_IsAcceptedByAvbTool()
    {
        SkipUnlessWslAvbTool();
        var image = await WritePatternedAsync("product_src.img", 96 * 1024);
        // avbtool resolves the partition image as <partition_name>.img next to the vbmeta — the
        // signed file itself must carry that name so verify_image reads the padded data + tree
        // region from the same file.
        // avbtool 按 <分区名>.img 在 vbmeta 同目录定位分区镜像 —— 签名后的文件必须使用该名称，
        // 才能让 verify_image 从同一文件读出补齐后的数据与树区域。
        var signed = Path.Combine(_root, "product.img");
        var result = await AvbResigner.ResignImageAsync(image, signed, _key,
            new AvbResignOptions { Mode = AvbResignMode.Hash, PartitionName = "product" }, Token);
        Assert.True(result.Success, result.ErrorMessage);
        AssertSignedByAvbTool(signed);
    }

    [Fact]
    public async Task ResignImage_HashtreeMode_IsAcceptedByAvbTool()
    {
        SkipUnlessWslAvbTool();
        var image = await WritePatternedAsync("vendor_ht_src.img", 192 * 1024);
        var signed = Path.Combine(_root, "vendor_ht.img");
        var result = await AvbResigner.ResignImageAsync(image, signed, _key,
            new AvbResignOptions { Mode = AvbResignMode.Hashtree, PartitionName = "vendor_ht" }, Token);
        Assert.True(result.Success, result.ErrorMessage);
        AssertSignedByAvbTool(signed);
    }

    private void AssertSignedByAvbTool(string signedPath)
    {
        // avbtool verify_image resolves the referenced partition image as <name>.img beside a vbmeta;
        // for add_hash_footer / add_hashtree_footer on a self-signed image we simply pass --image and
        // rely on the footer's own partition name resolution.
        var (output, exitCode) = WslTestHelpers.RunAvbTool(
            ["verify_image", "--image", WslTestHelpers.ToWslPath(signedPath), "--key", WslTestHelpers.ToWslPath(_keyPem)]);
        Assert.True(exitCode == 0, $"avbtool verify_image rejected the re-signed image: {output}");
    }

    private static void SkipUnlessWslAvbTool()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.EnsureAvbTool(out var reason)) { Assert.Skip(reason); }
    }

    private async Task<string> WritePatternedAsync(string name, int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 17 + 3);
        }

        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, data, Token);
        return path;
    }
}