using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Services.Firmware;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class AvbOtaServiceTests : IDisposable
{
    private const int Block = 4096;
    private readonly TempDirectory _temp = new();
    private string _root => _temp.Path;

    [Fact]
    public async Task ListPartitionsAsync_RawPayload_ReturnsManifestNames()
    {
        var token = TestContext.Current.CancellationToken;
        var boot = WriteImage("boot", 2, 0x11);
        var vendor = WriteImage("vendor", 2, 0x22);
        var payload = Path.Combine(_root, "payload.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(Block))
        {
            _ = builder.AddPartition("boot", boot).AddPartition("vendor", vendor);
            builder.Build(payload);
        }

        var names = await new AvbOtaService().ListPartitionsAsync(payload, token);

        Assert.Equal(new[] { "boot", "vendor" }, names.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task PatchAsync_RawPayload_ReSignsTargetAndUpdatesTopLevelVbmeta()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var keyPath = Path.Combine(_root, "avb-key.pem");
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), token);
        var bootContent = BuildContent(2 * Block, 0x31);
        var bootPath = _temp.WriteBytes("boot.img", bootContent);
        var signedBoot = Path.Combine(_root, "boot.signed.img");
        var signed = await AvbSigner.AddHashFooterAsync(bootPath, "boot", key, signedBoot, token);
        Assert.True(signed.Success, signed.ErrorMessage);

        var vbmetaPath = _temp.WriteBytes("vbmeta.img", Pad(ExtractVbmeta(await File.ReadAllBytesAsync(signedBoot, token))));
        var sourcePayload = Path.Combine(_root, "source.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(Block))
        {
            _ = builder.AddPartition("boot", bootPath).AddPartition("vbmeta", vbmetaPath);
            builder.Build(sourcePayload);
        }

        var outputPayload = Path.Combine(_root, "patched.bin");
        var service = new AvbOtaService();
        var patch = await service.PatchAsync(
            new AvbOtaPatchRequest
            {
                InputPath = sourcePayload,
                OutputPath = outputPayload,
                AvbPrivateKeyPath = keyPath,
                PartitionNames = ["boot"],
                FooterMode = AvbOtaFooterMode.Hash,
                VerifyOutput = true,
            },
            token);

        Assert.True(patch.Success, string.Join("; ", patch.Errors));
        Assert.Equal(["boot"], patch.PatchedPartitions);
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        await PayloadParser.ExtractAsync(outputPayload, ["boot", "vbmeta"], extracted, token);
        var image = await AvbImageVerifier.VerifyAsync(Path.Combine(extracted, "boot.img"), key, token);
        Assert.True(image.IsValid, image.ErrorMessage);
        var vbmeta = await File.ReadAllBytesAsync(Path.Combine(extracted, "vbmeta.img"), token);
        var signature = AvbSignatureVerifier.Verify(vbmeta, key);
        Assert.True(signature.HashIsValid && signature.SignatureIsValid, signature.ErrorMessage);
        Assert.Contains(signature.Descriptors, descriptor => descriptor.PartitionName == "boot");

        var verification = await service.VerifyAsync(
            new AvbOtaVerifyRequest
            {
                InputPath = outputPayload,
                VerifyPayloadSignatures = false,
            },
            token);

        Assert.True(verification.Success, string.Join("; ", verification.Errors));
        Assert.True(verification.ChainSuccess);
        Assert.Single(verification.Partitions);
    }

    [Fact]
    public async Task PatchAsync_OtaZip_RepublishesZipAndChainVerifies()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = SigningTestKeys.Create();
        var keyPath = Path.Combine(_root, "avb-key.pem");
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), token);
        var bootPath = _temp.WriteBytes("boot.zip.img", BuildContent(2 * Block, 0x41));
        var signedBoot = Path.Combine(_root, "boot.zip.signed.img");
        var signed = await AvbSigner.AddHashFooterAsync(bootPath, "boot", key, signedBoot, token);
        Assert.True(signed.Success, signed.ErrorMessage);
        var vbmetaPath = _temp.WriteBytes(
            "vbmeta.zip.img", Pad(ExtractVbmeta(await File.ReadAllBytesAsync(signedBoot, token))));
        var sourcePayload = Path.Combine(_root, "zip-source.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(Block))
        {
            _ = builder.AddPartition("boot", bootPath).AddPartition("vbmeta", vbmetaPath);
            builder.Build(sourcePayload);
        }

        var sourceZip = Path.Combine(_root, "source-ota.zip");
        await WriteOtaZipAsync(sourceZip, sourcePayload, token);
        var outputZip = Path.Combine(_root, "patched-ota.zip");
        var certificatePath = Path.Combine(_root, "ota-cert.pem");
        using (var certificate = new CertificateRequest(
                   "CN=AvbOtaServiceTest", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
               .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
        {
            await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem(), token);
        }

        var service = new AvbOtaService();

        var patch = await service.PatchAsync(
            new AvbOtaPatchRequest
            {
                InputPath = sourceZip,
                OutputPath = outputZip,
                AvbPrivateKeyPath = keyPath,
                PartitionNames = ["boot"],
                FooterMode = AvbOtaFooterMode.Hash,
                PayloadPrivateKeyPath = keyPath,
                OtaCertificatePath = certificatePath,
                OtaSigningKeyPath = keyPath,
                VerifyOutput = true,
            },
            token);

        Assert.True(patch.Success, string.Join("; ", patch.Errors));
        Assert.True(File.Exists(outputZip));
        var verification = await service.VerifyAsync(
            new AvbOtaVerifyRequest
            {
                InputPath = outputZip,
                VerifyPayloadSignatures = true,
            },
            token);
        Assert.True(verification.Success, string.Join("; ", verification.Errors));
        Assert.True(verification.PayloadSignatureIsValid);
        Assert.True(verification.ChainSuccess);
        Assert.Single(verification.Partitions);
    }

    public void Dispose() => _temp.Dispose();

    private static async Task WriteOtaZipAsync(string zipPath, string payloadPath, CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken);
        var manifestLength = checked((int)BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(12, 8)));
        var signatureLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(20, 4)));
        var metadataSize = checked(24 + manifestLength + signatureLength);
        var properties =
            $"FILE_HASH={Convert.ToBase64String(SHA256.HashData(payload))}\n"
            + $"FILE_SIZE={payload.Length}\n"
            + $"METADATA_HASH={Convert.ToBase64String(SHA256.HashData(payload.AsSpan(0, metadataSize)))}\n"
            + $"METADATA_SIZE={metadataSize}\n";
        using var archive = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "payload.bin", payload, cancellationToken);
        await WriteEntryAsync(archive, "payload_properties.txt", Encoding.UTF8.GetBytes(properties), cancellationToken);
        await WriteEntryAsync(archive, "META-INF/com/android/metadata", "ota-type=AB\n"u8.ToArray(), cancellationToken);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive, string name, byte[] content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, cancellationToken);
    }

    private string WriteImage(string name, int blocks, byte fill)
    {
        var content = new byte[blocks * Block];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(fill ^ (index % 251));
        }

        return _temp.WriteBytes(name + ".img", content);
    }

    private static byte[] BuildContent(int length, int seed)
    {
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(seed + index * 31);
        }

        return content;
    }

    private static byte[] ExtractVbmeta(byte[] signedImage)
    {
        var footer = AvbFooter.TryLoad(signedImage);
        Assert.True(footer.HasValue);
        var offset = checked((int)footer.Value.VbmetaOffset);
        var size = checked((int)footer.Value.VbmetaSize);
        return signedImage[offset..(offset + size)];
    }

    private static byte[] Pad(byte[] value)
    {
        var length = (value.Length + Block - 1) / Block * Block;
        var padded = new byte[length];
        value.CopyTo(padded, 0);
        return padded;
    }
}