using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// AVB signature verification tests: an RSA key pair is generated inside the test, a minimal
/// self-signed vbmeta image is constructed (header + authentication block with hash &amp; signature
/// + auxiliary block with the AVB-format public key), and <see cref="AvbSignatureVerifier"/>
/// is exercised against valid, tampered and wrong-key images.
/// <para>AVB 验签测试：在测试内生成 RSA 密钥对，构造最小自签名 vbmeta 镜像（头 + 认证块含哈希与
/// 签名 + 辅助块含 AVB 格式公钥），并针对有效 / 篡改 / 错误密钥镜像验证
/// <see cref="AvbSignatureVerifier"/>。</para>
/// </summary>
public class AvbSignatureVerificationTests
{
    private const uint AlgorithmSha256Rsa2048 = 1;
    private const int Sha256Size = 32;
    private const int Rsa2048SignatureSize = 256;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifyVBMetaFromStream_SignedMetadata_MatchesBufferVerification(bool tamper)
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);
        if (tamper)
        {
            vbmeta[140] ^= 1;
        }
        using var stream = new MemoryStream(vbmeta, writable: false);
        var result = AvbVerifier.VerifyVBMetaFromStream(stream, vbmeta.Length + 8L * 1024 * 1024 * 1024);
        var buffered = AvbVerifier.VerifyVBMeta(vbmeta);

        Assert.Equal(!tamper, result.IsValid);
        Assert.Equal(buffered.IsSigned, result.IsSigned);
        Assert.Equal(buffered.IsValid, result.IsValid);
        Assert.Equal(buffered.ErrorMessage, result.ErrorMessage);
        Assert.Equal(vbmeta.Length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void VerifyVBMetaFromStream_TruncatedMetadata_ThrowsWithoutClosingStream()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);
        using var stream = new MemoryStream(vbmeta[..^1], writable: false);

        Assert.Throws<EndOfStreamException>(() => AvbVerifier.VerifyVBMetaFromStream(stream, vbmeta.Length));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Verify_ValidSelfSignedVbmeta_Passes()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);

        var result = AvbSignatureVerifier.Verify(vbmeta);

        Assert.True(result.IsSigned);
        Assert.True(result.SignatureIsValid);
        Assert.True(result.HashIsValid);
        Assert.Equal("SHA256_RSA2048", result.AlgorithmName);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Verify_TamperedVbmeta_Fails()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);

        vbmeta[140] ^= 0xFF;

        var result = AvbSignatureVerifier.Verify(vbmeta);

        Assert.False(result.SignatureIsValid);
        Assert.False(result.HashIsValid);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    public void Verify_TamperedSerializedBlockSizeField_Fails(int fieldOffset)
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);
        vbmeta[fieldOffset + 7] ^= 1;

        var result = AvbSignatureVerifier.Verify(vbmeta, trustedPublicKey: rsa);

        Assert.False(result.SignatureIsValid);
        Assert.False(result.HashIsValid);
    }

    [Fact]
    public void Verify_OverflowingStoredHashRange_DoesNotThrowAndFails()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);
        WriteUInt64BE(vbmeta, 32, ulong.MaxValue);
        WriteUInt64BE(vbmeta, 40, 1);

        var result = AvbSignatureVerifier.Verify(vbmeta);

        Assert.False(result.HashIsValid);
        Assert.False(result.SignatureIsValid);
    }

    [Fact]
    public void Verify_PssSignature_IsRejected()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa, RSASignaturePadding.Pss);

        var result = AvbSignatureVerifier.Verify(vbmeta);

        Assert.True(result.HashIsValid);
        Assert.False(result.SignatureIsValid);
    }

    [Fact]
    public void Verify_WrongTrustedKey_Fails()
    {
        using var signer = SigningTestKeys.Create(2048);
        using var other = SigningTestKeys.Create(2048, alternate: true);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(signer);

        var result = AvbSignatureVerifier.Verify(vbmeta, trustedPublicKey: other);

        Assert.False(result.SignatureIsValid);
        Assert.True(result.HashIsValid);
    }

    [Fact]
    public void VerifySignature_ExplicitPublicKeyBlob_Verifies()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, publicKeyBlob) = BuildMinimalSelfSignedVbmeta(rsa);

        var signature = new byte[Rsa2048SignatureSize];
        Array.Copy(vbmeta, AvbSignatureVerifier.HeaderSize + Sha256Size, signature, 0, signature.Length);

        var result = AvbSignatureVerifier.VerifySignature(vbmeta, signature, publicKeyBlob, AlgorithmSha256Rsa2048);

        Assert.True(result.SignatureIsValid);
        Assert.Equal("SHA256_RSA2048", result.AlgorithmName);
    }

    [Fact]
    public void ImportAvbRsaPublicKey_RoundTripsModulus()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var blob = BuildAvbRsaPublicKeyBlob(parameters.Modulus!);

        using var imported = AvbSignatureVerifier.ImportAvbRsaPublicKey(blob);

        Assert.NotNull(imported);
        Assert.Equal(parameters.Modulus, imported.ExportParameters(false).Modulus);
    }

    [Fact]
    public async Task SignVbmeta_SignedImage_VerifiesThenRejectsTampering()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var privateKey = rsa;
        var root = CreateTempDir();
        try
        {
            var vbmetaPath = Path.Combine(root, "vbmeta.img");
            var outputPath = Path.Combine(root, "vbmeta.signed.img");
            await File.WriteAllBytesAsync(vbmetaPath, BuildUnsignedVbmetaTemplate(), TestContext.Current.CancellationToken);

            var salt = new byte[Sha256Size];
            RandomNumberGenerator.Fill(salt);
            var imageData = new byte[8192];
            RandomNumberGenerator.Fill(imageData);
            var digest = SHA256.HashData(salt.Concat(imageData).ToArray());

            var result = await AvbSigner.SignVbmetaAsync(
                vbmetaPath,
                privateKey,
                outputPath,
                [new AvbHashDescriptor { PartitionName = "boot", Salt = salt, Digest = digest }],
                TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(AlgorithmSha256Rsa2048, result.AlgorithmType);
            Assert.Equal("SHA256_RSA2048", result.AlgorithmName);

            var signed = await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken);
            var verify = AvbVerifier.VerifyVBMeta(signed);

            Assert.True(verify.IsSigned);
            Assert.True(verify.IsValid, verify.ErrorMessage);
            Assert.Equal(AlgorithmSha256Rsa2048, verify.VBMetaHeader?.AlgorithmType);

            var descriptor = Assert.Single(AvbSignatureVerifier.ExtractDescriptors(signed));
            Assert.Equal("boot", descriptor.PartitionName);
            Assert.Equal("sha256", descriptor.HashAlgorithm);
            Assert.Equal(salt, descriptor.Salt);
            Assert.Equal(digest, descriptor.Digest);

            signed[140] ^= 0xFF;
            var tamperedVerification = AvbVerifier.VerifyVBMeta(signed);
            Assert.True(tamperedVerification.IsSigned);
            Assert.False(tamperedVerification.IsValid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddHashFooter_SignedImage_VerifiesThenRejectsTampering()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var privateKey = rsa;
        var root = CreateTempDir();
        try
        {
            var imagePath = Path.Combine(root, "boot.img");
            var outputPath = Path.Combine(root, "boot.signed.img");
            var image = new byte[4096 * 3 + 1234];
            RandomNumberGenerator.Fill(image);
            await File.WriteAllBytesAsync(imagePath, image, TestContext.Current.CancellationToken);

            var result = await AvbSigner.AddHashFooterAsync(
                imagePath, "boot", privateKey, outputPath, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(AlgorithmSha256Rsa2048, result.AlgorithmType);

            var signed = await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken);
            var verify = AvbVerifier.Verify(signed);

            Assert.True(verify.IsSigned);
            Assert.True(verify.IsValid, verify.ErrorMessage);
            Assert.True(verify.Footer.HasValue);
            using (var stream = new MemoryStream(signed, writable: false))
            {
                var streamed = AvbVerifier.VerifyFromStream(stream, signed.Length);
                Assert.True(streamed.IsValid, streamed.ErrorMessage);
                Assert.Equal(verify.Footer, streamed.Footer);
                Assert.True(stream.CanRead);
            }

            var footer = verify.Footer.Value;
            Assert.Equal((ulong)image.Length, footer.OriginalImageSize);
            Assert.Equal(0UL, footer.VbmetaOffset % 4096);
            Assert.Equal(0, signed.Length % 4096);
            Assert.Equal((ulong)signed.Length, footer.VbmetaOffset + ((footer.VbmetaSize + 4095) / 4096 * 4096) + 4096);
            var metadataEnd = checked((int)(footer.VbmetaOffset + footer.VbmetaSize));
            Assert.All(signed[metadataEnd..^AvbFooter.Size], value => Assert.Equal(0, value));

            var vbmetaBytes = signed.AsSpan((int)footer.VbmetaOffset, (int)footer.VbmetaSize).ToArray();
            var vbmetaVerify = AvbVerifier.VerifyVBMeta(vbmetaBytes);
            Assert.True(vbmetaVerify.IsValid, vbmetaVerify.ErrorMessage);
            Assert.Single(AvbSignatureVerifier.ExtractDescriptors(vbmetaBytes));

            var tampered = (byte[])signed.Clone();
            tampered[(int)footer.VbmetaOffset + 140] ^= 0xFF;
            var tamperedVerification = AvbVerifier.Verify(tampered);
            Assert.True(tamperedVerification.IsSigned);
            Assert.False(tamperedVerification.IsValid);
            using var tamperedStream = new MemoryStream(tampered, writable: false);
            Assert.False(AvbVerifier.VerifyFromStream(tamperedStream, tampered.Length).IsValid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Builds a minimal self-signed vbmeta image:
    /// <code>[256-byte header][auth block: sha256 hash + rsa2048 signature][aux block: AVB public key blob]</code>
    /// The signing input follows the AVB convention: the original serialized 256-byte header
    /// concatenated with the auxiliary data block (no descriptors / empty salt).
    /// <para>构造最小自签名 vbmeta 镜像。签名输入遵循 AVB 约定：原始序列化 256 字节头
    /// 拼接辅助数据块（无描述符 / 空盐）。</para>
    /// </summary>
    private static (byte[] Vbmeta, byte[] PublicKeyBlob) BuildMinimalSelfSignedVbmeta(
        RSA rsa, RSASignaturePadding? signaturePadding = null)
    {
        var modulus = rsa.ExportParameters(false).Modulus!;
        var publicKeyBlob = BuildAvbRsaPublicKeyBlob(modulus);

        const int authSize = Sha256Size + Rsa2048SignatureSize;
        var auxSize = (ulong)publicKeyBlob.Length;

        var header = new byte[AvbSignatureVerifier.HeaderSize];
        "AVB0"u8.CopyTo(header);
        WriteUInt32BE(header, 4, 1);
        WriteUInt32BE(header, 8, 0);
        WriteUInt64BE(header, 12, authSize);
        WriteUInt64BE(header, 20, auxSize);
        WriteUInt32BE(header, 28, AlgorithmSha256Rsa2048);
        WriteUInt64BE(header, 32, 0);
        WriteUInt64BE(header, 40, Sha256Size);
        WriteUInt64BE(header, 48, Sha256Size);
        WriteUInt64BE(header, 56, Rsa2048SignatureSize);
        WriteUInt64BE(header, 64, 0);
        WriteUInt64BE(header, 72, (ulong)publicKeyBlob.Length);
        WriteUInt64BE(header, 80, 0);
        WriteUInt64BE(header, 88, 0);
        WriteUInt64BE(header, 96, 0);
        WriteUInt64BE(header, 104, 0);
        WriteUInt64BE(header, 112, 0);
        WriteUInt32BE(header, 120, 0);
        WriteUInt32BE(header, 124, 0);
        Encoding.ASCII.GetBytes("test self-signed vbmeta").CopyTo(header, 128);

        var signingInput = header.Concat(publicKeyBlob).ToArray();

        var hash = SHA256.HashData(signingInput);
        var signature = rsa.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            signaturePadding ?? RSASignaturePadding.Pkcs1);

        var image = new byte[AvbSignatureVerifier.HeaderSize + authSize + auxSize];
        header.CopyTo(image, 0);
        hash.CopyTo(image, AvbSignatureVerifier.HeaderSize);
        signature.CopyTo(image, AvbSignatureVerifier.HeaderSize + Sha256Size);
        publicKeyBlob.CopyTo(image, AvbSignatureVerifier.HeaderSize + authSize);

        return (image, publicKeyBlob);
    }

    /// <summary>
    /// Builds an AVB-format RSA public key blob (avbtool extract_public_key layout):
    /// key_num_bits (BE) | n0inv (BE) | modulus (BE) | rr (BE).
    /// <para>构造 AVB 格式 RSA 公钥数据块：key_num_bits（BE）| n0inv（BE）| 模数（BE）| rr（BE）。</para>
    /// </summary>
    private static byte[] BuildAvbRsaPublicKeyBlob(byte[] modulus)
    {
        var keyNumBits = modulus.Length * 8;
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var n0inv = ComputeN0Inv(modulus);
        var rr = BigInteger.ModPow(BigInteger.One << (keyNumBits * 2), 1, n);
        var rrBytes = ToBigEndianPadded(rr, modulus.Length);

        var blob = new byte[8 + modulus.Length + modulus.Length];
        WriteUInt32BE(blob, 0, (uint)keyNumBits);
        WriteUInt32BE(blob, 4, n0inv);
        modulus.CopyTo(blob, 8);
        rrBytes.CopyTo(blob, 8 + modulus.Length);
        return blob;
    }

    private static uint ComputeN0Inv(byte[] modulus)
    {
        var lowWord = modulus.AsSpan(modulus.Length - sizeof(uint));
        var a = BinaryPrimitives.ReadUInt32BigEndian(lowWord);

        uint inv = 1;
        for (var i = 0; i < 5; i++)
        {
            inv = unchecked(inv * (2 - a * inv));
        }

        return unchecked(0 - inv);
    }

    private static byte[] ToBigEndianPadded(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[length];
        if (bytes.Length >= length)
        {
            Array.Copy(bytes, bytes.Length - length, padded, 0, length);
        }
        else
        {
            bytes.CopyTo(padded, length - bytes.Length);
        }

        return padded;
    }

    private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteUInt64BE(byte[] buffer, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            buffer[offset + i] = (byte)(value >> (56 - i * 8));
        }
    }

    /// <summary>
    /// Creates a unique temporary directory for a signing round-trip test.
    /// <para>为签名往返测试创建唯一的临时目录。</para>
    /// </summary>
    private static string CreateTempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "avb_signer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Builds a minimal unsigned 256-byte VBMeta template ("AVB0" magic, libavb 1.0, algorithm NONE)
    /// used as the source for <see cref="AvbSigner.SignVbmetaAsync"/>.
    /// <para>构造供 <see cref="AvbSigner.SignVbmetaAsync"/> 作为源的最小未签名 256 字节 VBMeta 模板
    /// （"AVB0" 魔数、libavb 1.0、算法 NONE）。</para>
    /// </summary>
    private static byte[] BuildUnsignedVbmetaTemplate()
    {
        var vbmeta = new byte[AvbVBMetaImageHeader.Size];
        "AVB0"u8.CopyTo(vbmeta);
        BinaryPrimitives.WriteUInt32BigEndian(vbmeta.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(vbmeta.AsSpan(8, 4), 0);
        Encoding.ASCII.GetBytes("test unsigned template").CopyTo(vbmeta, 128);
        return vbmeta;
    }

    [Fact]
    public async Task DisableVbmetaVerification_SetsFlagsAndZeroesAuthBlock()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);
        var sourcePath = Path.Combine(Path.GetTempPath(), $"avb_disable_{Guid.NewGuid():N}.img");
        var outputPath = sourcePath + ".disabled";
        try
        {
            await File.WriteAllBytesAsync(sourcePath, vbmeta, TestContext.Current.CancellationToken);

            var result = await AvbSigner.DisableVbmetaVerificationAsync(sourcePath, outputPath, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.ErrorMessage);

            var patched = await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.Equal(vbmeta.Length, patched.Length);
            Assert.Equal(AvbSigner.VbmetaFlagHashtreeDisabled | AvbSigner.VbmetaFlagVerificationDisabled,
                BinaryPrimitives.ReadUInt32BigEndian(patched.AsSpan(120, 4)));
            var authSize = (int)BinaryPrimitives.ReadUInt64BigEndian(patched.AsSpan(12, 8));
            Assert.True(authSize > 0);
            Assert.True(patched.AsSpan(256, authSize).IndexOfAnyExcept((byte)0) < 0);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }
}