using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Keys;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class PayloadSigningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "payload-signing-" + Guid.NewGuid().ToString("N"));
    public PayloadSigningTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(2048, 8192)]
    [InlineData(4096, 8192)]
    [InlineData(2048, 0)]
    public void Builder_SignsBothRangesAndRejectsTampering(int bits, int length)
    {
        using var key = RSA.Create(bits);
        using var builder = new PayloadBuilder();
        var original = RandomNumberGenerator.GetBytes(length);
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        builder.WithSigningKey(key).AddPartition("system", input).Build(output);
        var bytes = output.ToArray();
        AssertSignatures(bytes, key);
        using var wrongKey = RSA.Create(bits);
        var (metadata, _, metaSignature, _) = Decode(bytes);
        Assert.False(wrongKey.VerifyData(metadata, metaSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var path = Path.Combine(_root, "payload.bin");
        File.WriteAllBytes(path, bytes);
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        PayloadParser.Extract(path, ["system"], extracted);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(extracted, "system.img")));
        if (length == 0)
        {
            using var proto = new CodedInputStream(metadata[24..]);
            var found = false;
            uint tag;
            while ((tag = proto.ReadTag()) != 0)
            {
                if (tag == 32) { Assert.Equal(0UL, proto.ReadUInt64()); found = true; }
                else proto.SkipLastField();
            }
            Assert.True(found);
        }
    }

    [Fact]
    public async Task Delivery_UsesChosenKey_PersistsIdentity_AndPublishesMatchingProperties()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "keys"));
        var first = library.Generate("first", ManagedKeyKind.Payload, 2048);
        var chosen = library.Generate("chosen", ManagedKeyKind.Payload, 2048);
        var source = Path.Combine(_root, "system.img");
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(8192));
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            OutputDirectory = Path.Combine(_root, "output"),
            Container = [new() { Partition = "system", FileName = "system.img", SourcePath = source, SourceLength = 8192 }],
            Files = [new() { Generated = true, Partition = "payload", FileName = "ota.zip" }],
            Payload = new() { SigningKeyPath = library.PrivatePath(chosen), SigningKeyFingerprint = chosen.Fingerprint }
        };
        var state = DeliveryPlanStore.CaptureElement(_root, plan);
        var restored = DeliveryPlanStore.LoadFromState(state, _root);
        Assert.Equal(plan.Payload, restored!.Payload);
        Assert.DoesNotContain("PRIVATE KEY", state.GetRawText());
        await new DeliveryOutputService(new NoTools()).ExportAsync(restored, token: TestContext.Current.CancellationToken);
        var unpacked = Path.Combine(_root, "ota-content");
        ZipFile.ExtractToDirectory(Path.Combine(plan.OutputDirectory, "ota.zip"), unpacked);
        var bytes = File.ReadAllBytes(Path.Combine(unpacked, "payload.bin"));
        using var key = KeyLibraryService.LoadPrivateKey(library.PrivatePath(chosen));
        AssertSignatures(bytes, key);
        using var wrong = KeyLibraryService.LoadPrivateKey(library.PrivatePath(first));
        var (metadata, _, signature, _) = Decode(bytes);
        Assert.False(wrong.VerifyData(metadata, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var properties = File.ReadAllLines(Path.Combine(unpacked, "payload_properties.txt")).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), properties["FILE_HASH"]);
        Assert.Equal(bytes.Length.ToString(), properties["FILE_SIZE"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(metadata)), properties["METADATA_HASH"]);
        Assert.Equal(metadata.Length.ToString(), properties["METADATA_SIZE"]);
        Assert.DoesNotContain(Directory.GetFiles(plan.OutputDirectory, "*", SearchOption.AllDirectories), p => p.EndsWith(".pem"));
        var failurePlan = plan with { OutputDirectory = Path.Combine(_root, "failure") };
        File.Copy(library.PrivatePath(first), library.PrivatePath(chosen), true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools()).ExportAsync(failurePlan, token: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(failurePlan.OutputDirectory));
        library.Delete(chosen);
        await Assert.ThrowsAsync<FileNotFoundException>(() => new DeliveryOutputService(new NoTools()).ExportAsync(failurePlan, token: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(failurePlan.OutputDirectory));
        Assert.Empty(Directory.GetDirectories(_root, ".uotan-delivery-*"));
    }

    private static void AssertSignatures(byte[] bytes, RSA key)
    {
        var (metadata, payload, metadataSignature, payloadSignature) = Decode(bytes);
        Assert.True(key.VerifyData(metadata, metadataSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.True(key.VerifyData(payload, payloadSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        metadata[0] ^= 1;
        payload[^1] ^= 1;
        Assert.False(key.VerifyData(metadata, metadataSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(key.VerifyData(payload, payloadSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static (byte[] Metadata, byte[] Payload, byte[] MetadataSignature, byte[] PayloadSignature) Decode(byte[] bytes)
    {
        Assert.Equal("CrAU"u8.ToArray(), bytes[..4]);
        var metadataSize = checked(24 + (int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12)));
        var signatureSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(bytes[24..metadataSize]);
        var dataStart = metadataSize + signatureSize;
        var dataEnd = checked(dataStart + (int)manifest.SignaturesOffset);
        Assert.Equal(bytes.Length, checked(dataEnd + (int)manifest.SignaturesSize));
        byte[] Signature(byte[] encoded)
        {
            using var outer = new CodedInputStream(encoded);
            Assert.Equal(10u, outer.ReadTag());
            using var nested = new CodedInputStream(outer.ReadBytes().ToByteArray());
            Assert.Equal(18u, nested.ReadTag());
            var signature = nested.ReadBytes().ToByteArray();
            Assert.Equal(29u, nested.ReadTag());
            Assert.Equal((uint)signature.Length, nested.ReadFixed32());
            Assert.True(nested.IsAtEnd);
            Assert.True(outer.IsAtEnd);
            return signature;
        }
        return (bytes[..metadataSize], [.. bytes[..metadataSize], .. bytes[dataStart..dataEnd]],
            Signature(bytes[metadataSize..dataStart]), Signature(bytes[dataEnd..]));
    }

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token) => Task.CompletedTask;
    }
}
