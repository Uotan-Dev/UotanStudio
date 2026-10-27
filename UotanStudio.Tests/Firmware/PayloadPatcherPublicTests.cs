using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PayloadPatcherPublicTests : IDisposable
{
    private const int Block = 4096;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "uotan-payload-public-patch-" + Guid.NewGuid().ToString("N"));

    public PayloadPatcherPublicTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReplaceAsync_UnsignedPayload_ReplacesTargetAndKeepsOtherPartitions()
    {
        var token = TestContext.Current.CancellationToken;
        var systemPath = WriteImage("system", 2, 0x11);
        var vendorPath = WriteImage("vendor", 2, 0x22);
        var newVendorPath = WriteImage("vendor-new", 3, 0x44);
        var sourcePath = Path.Combine(_root, "base.img");
        using var builder = new PayloadBuilder().WithBlockSize(Block);
        _ = builder.AddPartition("system", systemPath).AddPartition("vendor", vendorPath);
        builder.Build(sourcePath);
        var outputPath = Path.Combine(_root, "patched.img");

        var replaced = await PayloadPatcher.ReplaceAsync(
            sourcePath,
            outputPath,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["vendor"] = newVendorPath },
            cancellationToken: token);

        Assert.Equal(1, replaced);
        var metadata = PayloadParser.GetPayloadMetadata(outputPath);
        Assert.Equal(new[] { "system", "vendor" }, metadata.PartitionNames.Order(StringComparer.Ordinal).ToArray());
        var extractRoot = Path.Combine(_root, "extract");
        Directory.CreateDirectory(extractRoot);
        await PayloadParser.ExtractAsync(outputPath, ["system", "vendor"], extractRoot, token);
        Assert.Equal(
            await File.ReadAllBytesAsync(systemPath, token),
            await File.ReadAllBytesAsync(Path.Combine(extractRoot, "system.img"), token));
        Assert.Equal(
            await File.ReadAllBytesAsync(newVendorPath, token),
            await File.ReadAllBytesAsync(Path.Combine(extractRoot, "vendor.img"), token));
    }

    [Fact]
    public async Task ReplaceAsync_SignedPayload_SignsBothSlotsWithSuppliedKey()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create(2048);
        var (sourcePath, replacementPath) = BuildSignedPayload(key, "system");
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(key.ExportParameters(false));
        var outputPath = Path.Combine(_root, "signed-patched.img");

        var replaced = await PayloadPatcher.ReplaceAsync(
            sourcePath,
            outputPath,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["system"] = replacementPath },
            key,
            token);

        Assert.Equal(1, replaced);
        var verification = await PayloadSignatureVerifier.VerifyAsync(outputPath, publicKey, token);
        Assert.True(verification.IsSigned);
        Assert.True(verification.MetadataSignatureIsValid);
        Assert.True(verification.PayloadSignatureIsValid);
        Assert.Null(verification.ErrorMessage);
    }

    [Fact]
    public async Task ReplaceAsync_TamperedDataRegion_FailsPayloadSignatureSlot()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create(2048);
        var (sourcePath, replacementPath) = BuildSignedPayload(key, "system");
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(key.ExportParameters(false));
        var outputPath = Path.Combine(_root, "signed-patched.img");
        _ = await PayloadPatcher.ReplaceAsync(
            sourcePath,
            outputPath,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["system"] = replacementPath },
            key,
            token);
        var metadata = PayloadParser.GetPayloadMetadata(outputPath);
        Assert.True(metadata.SignaturesOffset > 0);
        var bytes = await File.ReadAllBytesAsync(outputPath, token);
        var dataStart = checked((int)metadata.BaseOffset);
        Assert.True(dataStart < bytes.Length);
        bytes[dataStart] ^= 1;
        var tamperedPath = Path.Combine(_root, "tampered.img");
        await File.WriteAllBytesAsync(tamperedPath, bytes, token);

        var verification = await PayloadSignatureVerifier.VerifyAsync(tamperedPath, publicKey, token);

        Assert.True(verification.MetadataSignatureIsValid);
        Assert.False(verification.PayloadSignatureIsValid);
        Assert.False(verification.IsSigned);
        Assert.NotNull(verification.ErrorMessage);
    }

    [Fact]
    public async Task ReplaceAsync_CanceledBeforeStart_PreservesExistingOutput()
    {
        var token = TestContext.Current.CancellationToken;
        var systemPath = WriteImage("system", 2, 0x11);
        var replacementPath = WriteImage("system-new", 2, 0x22);
        var sourcePath = Path.Combine(_root, "base.img");
        using var builder = new PayloadBuilder().WithBlockSize(Block);
        _ = builder.AddPartition("system", systemPath);
        builder.Build(sourcePath);
        var outputPath = Path.Combine(_root, "existing.img");
        await File.WriteAllBytesAsync(outputPath, [9, 8, 7], token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PayloadPatcher.ReplaceAsync(
            sourcePath,
            outputPath,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["system"] = replacementPath },
            cancellationToken: cancellation.Token));

        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(outputPath, token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (string SourcePath, string ReplacementPath) BuildSignedPayload(RSA key, string partitionName)
    {
        var imagePath = WriteImage(partitionName, 2, 0x11);
        var replacementPath = WriteImage(partitionName + "-new", 3, 0x55);
        var sourcePath = Path.Combine(_root, "signed-base.img");
        using var builder = new PayloadBuilder().WithBlockSize(Block).WithSigningKey(key);
        _ = builder.AddPartition(partitionName, imagePath);
        builder.Build(sourcePath);

        return (sourcePath, replacementPath);
    }

    private string WriteImage(string prefix, int blocks, byte fill)
    {
        var path = Path.Combine(_root, prefix + ".img");
        var data = new byte[blocks * Block];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(fill ^ (index % 251));
        }

        File.WriteAllBytes(path, data);
        return path;
    }
}