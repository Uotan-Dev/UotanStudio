using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Services.Delivery;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class OtaZipPatchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-ota-patch-" + Guid.NewGuid().ToString("N"));

    public OtaZipPatchServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Verify_ValidStoredPayload_ReportsValidProperties()
    {
        var zip = await CreateZipAsync("original");

        var result = OtaZipPatchService.Verify(zip);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("original", result.Properties["CUSTOM"]);
    }

    [Fact]
    public async Task Patch_StoredMember_PublishesReplacementAndPreservesInput()
    {
        var zip = await CreateZipAsync("original");
        var replacement = Path.Combine(_root, "replacement.txt");
        await File.WriteAllTextAsync(replacement, "patched", TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "patched.zip");

        var result = await OtaZipPatchService.PatchAsync(zip, output,
            new OtaZipPatchOptions { ReplacementFiles = new Dictionary<string, string> { ["custom.txt"] = replacement } },
            TestContext.Current.CancellationToken);

        Assert.Equal(["custom.txt"], result.ReplacedEntries);
        Assert.Equal("original", await ReadEntryAsync(zip, "custom.txt"));
        Assert.Equal("patched", await ReadEntryAsync(output, "custom.txt"));
        Assert.True(OtaZipPatchService.Verify(output).IsValid);
    }

    [Fact]
    public async Task Patch_PayloadWithoutProperties_RejectsBeforePublishing()
    {
        var zip = await CreateZipAsync("original");
        var replacement = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(replacement, [1, 2, 3], TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "patched.zip");

        await Assert.ThrowsAsync<InvalidDataException>(() => OtaZipPatchService.PatchAsync(zip, output,
            new OtaZipPatchOptions { AllowPayloadReplacement = true, ReplacementFiles = new Dictionary<string, string> { ["payload.bin"] = replacement } },
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Rebuild_ReplacementPayload_RegeneratesPropertiesAndStoredRanges()
    {
        var input = await CreateRebuildZipAsync(CreatePayload("system", [1, 2, 3]));
        var replacement = CreatePayload("vendor", [4, 5, 6, 7]);
        var payloadPath = Path.Combine(_root, "replacement-payload.bin");
        await File.WriteAllBytesAsync(payloadPath, replacement, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "rebuilt.zip");

        var result = await OtaZipPatchService.RebuildAsync(
            input,
            output,
            new OtaZipRebuildOptions { PayloadPath = payloadPath },
            TestContext.Current.CancellationToken);

        Assert.True(result.Verification.IsValid, string.Join(Environment.NewLine, result.Verification.Errors));
        Assert.False(result.OuterSignatureApplied);
        Assert.Equal(replacement, await ReadEntryBytesAsync(output, "payload.bin"));
        var properties = ParseProperties(await ReadEntryAsync(output, "payload_properties.txt"));
        Assert.Equal(replacement.Length.ToString(), properties["FILE_SIZE"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(replacement)), properties["FILE_HASH"]);
        var metadataSize = checked(24 + (int)BinaryPrimitives.ReadUInt64BigEndian(replacement.AsSpan(12, 8))
            + (int)BinaryPrimitives.ReadUInt32BigEndian(replacement.AsSpan(20, 4)));
        Assert.Equal(metadataSize.ToString(), properties["METADATA_SIZE"]);
        Assert.Equal(
            Convert.ToBase64String(SHA256.HashData(replacement.AsSpan(0, metadataSize))),
            properties["METADATA_HASH"]);

        var ranges = DeliveryPackageBuilder.ReadStoredRanges(output);
        var metadata = ParseProperties(await ReadEntryAsync(output, "META-INF/com/android/metadata"));
        var payloadRange = ranges["payload.bin"];
        var propertiesRange = ranges["payload_properties.txt"];
        var metadataRange = ranges["META-INF/com/android/metadata"];
        var streaming =
            $"payload.bin:{payloadRange.Offset}:{payloadRange.Size},payload_properties.txt:{propertiesRange.Offset}:{propertiesRange.Size},metadata:{metadataRange.Offset}:{metadataRange.Size}";
        Assert.Equal(streaming, metadata["ota-streaming-property-files"]);
        Assert.Equal($"payload_metadata.bin:{payloadRange.Offset}:{metadataSize},{streaming}", metadata["ota-property-files"]);
    }

    [Fact]
    public async Task Rebuild_InvalidPayload_PreservesExistingOutput()
    {
        var input = await CreateRebuildZipAsync(CreatePayload("system", [1]));
        var payloadPath = Path.Combine(_root, "invalid-payload.bin");
        await File.WriteAllBytesAsync(payloadPath, [1, 2, 3], TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "existing.zip");
        await File.WriteAllBytesAsync(output, [0x7A], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => OtaZipPatchService.RebuildAsync(
            input,
            output,
            new OtaZipRebuildOptions { PayloadPath = payloadPath },
            TestContext.Current.CancellationToken));

        Assert.Equal([0x7A], await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rebuild_WithMatchingCertificateAndKey_AppliesWholeFileSignature()
    {
        var input = await CreateRebuildZipAsync(CreatePayload("system", [1]));
        var payloadPath = Path.Combine(_root, "signed-payload.bin");
        await File.WriteAllBytesAsync(payloadPath, CreatePayload("vendor", [2]), TestContext.Current.CancellationToken);
        var certificatePath = Path.Combine(_root, "ota-cert.pem");
        var keyPath = Path.Combine(_root, "ota-key.pem");
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=UotanStudio OTA Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem(), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);
        }
        var output = Path.Combine(_root, "signed.zip");

        var result = await OtaZipPatchService.RebuildAsync(
            input,
            output,
            new OtaZipRebuildOptions
            {
                PayloadPath = payloadPath,
                CertificatePath = certificatePath,
                SigningKeyPath = keyPath
            },
            new PemSigningKeyProvider(),
            TestContext.Current.CancellationToken);

        Assert.True(result.OuterSignatureApplied);
        Assert.True(result.Verification.IsValid, string.Join(Environment.NewLine, result.Verification.Errors));
        Assert.Contains("BEGIN CERTIFICATE", await ReadEntryAsync(output, "META-INF/com/android/otacert"));
        var bytes = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2)));
    }

    private async Task<string> CreateZipAsync(string custom)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        var payload = new byte[24];
        Encoding.ASCII.GetBytes("CrAU").CopyTo(payload, 0);
        var hash = Convert.ToBase64String(SHA256.HashData(payload));
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "payload.bin", payload);
        await WriteEntryAsync(archive, "payload_properties.txt", $"FILE_SIZE={payload.Length}\nFILE_HASH={hash}\nCUSTOM={custom}\n");
        await WriteEntryAsync(archive, "custom.txt", custom);
        return path;
    }

    private async Task<string> CreateRebuildZipAsync(byte[] payload)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        var metadataSize = checked(24 + (int)BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(12, 8))
            + (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(20, 4)));
        var properties =
            $"FILE_SIZE={payload.Length}\n" +
            $"FILE_HASH={Convert.ToBase64String(SHA256.HashData(payload))}\n" +
            $"METADATA_SIZE={metadataSize}\n" +
            $"METADATA_HASH={Convert.ToBase64String(SHA256.HashData(payload.AsSpan(0, metadataSize)))}\n";
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "payload.bin", payload);
        await WriteEntryAsync(archive, "payload_properties.txt", properties);
        await WriteEntryAsync(
            archive,
            "META-INF/com/android/metadata",
            "ota-type=AB\nota-streaming-property-files=old\nota-property-files=old\n");
        await WriteEntryAsync(archive, "custom.txt", "preserved");
        return path;
    }

    private static byte[] CreatePayload(string partitionName, byte[] body)
    {
        var manifest = new DeltaArchiveManifest { BlockSize = 4096 };
        manifest.Partitions.Add(new PartitionUpdate { PartitionName = partitionName });
        var manifestBytes = manifest.ToByteArray();
        var payload = new byte[24 + manifestBytes.Length + body.Length];
        "CrAU"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(12), checked((ulong)manifestBytes.Length));
        manifestBytes.CopyTo(payload, 24);
        body.CopyTo(payload, 24 + manifestBytes.Length);
        return payload;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content)
        => await WriteEntryAsync(archive, name, Encoding.UTF8.GetBytes(content));

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] content)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        await stream.WriteAsync(content, TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadEntryAsync(string zip, string name)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(string zip, string name)
    {
        using var archive = ZipFile.OpenRead(zip);
        await using var input = archive.GetEntry(name)!.Open();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static Dictionary<string, string> ParseProperties(string content)
        => content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.Ordinal);

    private sealed class PemSigningKeyProvider : IDeliverySigningKeyProvider
    {
        public RSA LoadPrivateKey(string path)
        {
            var key = RSA.Create();
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}