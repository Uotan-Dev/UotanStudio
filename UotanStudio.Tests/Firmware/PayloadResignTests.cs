using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PayloadResignTests
{
    [Theory]
    [InlineData(2048, 8192)]
    [InlineData(4096, 0)]
    public void Resign_ReplaceKey_VerifiesBothRangesAndPreservesData(int bits, int length)
    {
        using var original = SigningTestKeys.Create(2048);
        using var replacement = SigningTestKeys.Create(bits);
        var image = RandomNumberGenerator.GetBytes(length);
        using var builder = new PayloadBuilder().WithSigningKey(original);
        builder.AddPartition("system", new MemoryStream(image));
        var signedPayload = Path.GetTempFileName();
        var resignedPayload = Path.GetTempFileName();
        var extracted = Path.Combine(Path.GetTempPath(), "resign-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            builder.Build(signedPayload);
            var before = File.ReadAllBytes(signedPayload);
            PayloadSigner.Resign(signedPayload, replacement, resignedPayload, TestContext.Current.CancellationToken);
            var after = File.ReadAllBytes(resignedPayload);
            AssertSignatures(after, replacement);
            var (_, _, _, _, dataBefore, _) = DecodeRegions(before);
            var (_, _, _, _, dataAfter, _) = DecodeRegions(after);
            Assert.Equal(dataBefore, dataAfter);
            Directory.CreateDirectory(extracted);
            PayloadParser.Extract(resignedPayload, ["system"], extracted);
            Assert.Equal(image, File.ReadAllBytes(Path.Combine(extracted, "system.img")));
        }
        finally
        {
            File.Delete(signedPayload);
            File.Delete(resignedPayload);
            if (Directory.Exists(extracted))
            {
                Directory.Delete(extracted, true);
            }
        }
    }

    [Fact]
    public void Resign_UnsignedPayload_ProducesVerifiableSignatures()
    {
        using var key = SigningTestKeys.Create(2048);
        var image = RandomNumberGenerator.GetBytes(4096);
        using var builder = new PayloadBuilder();
        builder.AddPartition("system", new MemoryStream(image));
        var unsignedPayload = Path.GetTempFileName();
        var resigned = Path.GetTempFileName();
        try
        {
            builder.Build(unsignedPayload);
            var before = File.ReadAllBytes(unsignedPayload);
            var (_, manifestBefore, _, _, _, _) = DecodeRegions(before);
            Assert.Equal(0UL, manifestBefore.SignaturesSize);
            PayloadSigner.Resign(unsignedPayload, key, resigned, TestContext.Current.CancellationToken);
            var after = File.ReadAllBytes(resigned);
            AssertSignatures(after, key);
            var (_, manifestAfter, _, _, _, _) = DecodeRegions(after);
            Assert.Equal(manifestBefore.MinorVersion, manifestAfter.MinorVersion);
            Assert.Equal(
                manifestBefore.Partitions.Sum(partition => partition.Operations.Sum(operation => (long)operation.DataLength)),
                manifestAfter.Partitions.Sum(partition => partition.Operations.Sum(operation => (long)operation.DataLength)));
        }
        finally
        {
            File.Delete(unsignedPayload);
            File.Delete(resigned);
        }
    }

    [Fact]
    public void Resign_OutputEqualsInput_ReplacesFileAtomically()
    {
        using var first = SigningTestKeys.Create(2048);
        using var second = SigningTestKeys.Create(2048, alternate: true);
        var image = RandomNumberGenerator.GetBytes(4096);
        using var builder = new PayloadBuilder().WithSigningKey(first);
        builder.AddPartition("system", new MemoryStream(image));
        var payload = Path.GetTempFileName();
        try
        {
            builder.Build(payload);
            PayloadSigner.Resign(payload, second, payload, TestContext.Current.CancellationToken);
            var bytes = File.ReadAllBytes(payload);
            AssertSignatures(bytes, second);
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(payload) + ".tmp-*"));
        }
        finally
        {
            File.Delete(payload);
        }
    }

    [Fact]
    public void Resign_InvalidKeySize_ThrowsArgumentException()
    {
        using var key = RSA.Create(1024);
        var payload = Path.GetTempFileName();
        try
        {
            Assert.Throws<ArgumentException>(() => PayloadSigner.Resign(payload, key, payload + ".out", TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(payload);
            File.Delete(payload + ".out");
        }
    }

    [Fact]
    public void Resign_CorruptInput_PreservesExistingOutput()
    {
        using var key = SigningTestKeys.Create(2048);
        var garbage = Path.GetTempFileName();
        var existing = Path.GetTempFileName();
        try
        {
            File.WriteAllText(garbage, "not-a-payload");
            File.WriteAllBytes(existing, [1, 2, 3]);
            Assert.ThrowsAny<Exception>(() => PayloadSigner.Resign(garbage, key, existing, TestContext.Current.CancellationToken));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(existing));
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(existing) + ".tmp-*"));
        }
        finally
        {
            File.Delete(garbage);
            File.Delete(existing);
        }
    }

    private static void AssertSignatures(byte[] bytes, RSA key)
    {
        var (metadata, _, metadataSignature, payloadSignature, data, _) = DecodeRegions(bytes);
        Assert.True(key.VerifyData(metadata, metadataSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        byte[] signedBlob = [.. metadata, .. data];
        Assert.True(key.VerifyData(signedBlob, payloadSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        metadata[0] ^= 1;
        signedBlob[^1] ^= 1;
        Assert.False(key.VerifyData(metadata, metadataSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(key.VerifyData(signedBlob, payloadSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.NotEmpty(metadataSignature);
        Assert.NotEmpty(payloadSignature);
    }

    private static (byte[] Metadata, DeltaArchiveManifest Manifest, byte[] MetadataSignature, byte[] PayloadSignature, byte[] Data, byte[] Full) DecodeRegions(byte[] bytes)
    {
        Assert.Equal("CrAU"u8.ToArray(), bytes[..4]);
        var manifestLength = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12));
        var metadataSize = checked(24 + (int)manifestLength);
        var signatureSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(bytes[24..metadataSize]);
        var dataStart = metadataSize + signatureSize;
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

        var metadata = bytes[..metadataSize];
        if (signatureSize == 0 && manifest.SignaturesSize == 0)
        {
            return ([.. metadata], manifest, [], [], bytes[dataStart..], bytes);
        }

        var dataEnd = checked(dataStart + (int)manifest.SignaturesOffset);
        Assert.Equal(bytes.Length, checked(dataEnd + (int)manifest.SignaturesSize));
        return ([.. metadata], manifest, Signature(bytes[metadataSize..dataStart]), Signature(bytes[dataEnd..]), bytes[dataStart..dataEnd], bytes);
    }
}