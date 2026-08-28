using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Avb;
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

    [Fact]
    public void Verify_ValidSelfSignedVbmeta_Passes()
    {
        using var rsa = RSA.Create(2048);
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
        using var rsa = RSA.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);

        // Flip a byte inside the release string (header), which does not change parsing.
        vbmeta[140] ^= 0xFF;

        var result = AvbSignatureVerifier.Verify(vbmeta);

        Assert.False(result.SignatureIsValid);
        Assert.False(result.HashIsValid);
    }

    [Theory]
    [InlineData(12)] // authentication_data_block_size
    [InlineData(20)] // auxiliary_data_block_size
    public void Verify_TamperedSerializedBlockSizeField_Fails(int fieldOffset)
    {
        using var rsa = RSA.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa);
        vbmeta[fieldOffset + 7] ^= 1;

        var result = AvbSignatureVerifier.Verify(vbmeta, trustedPublicKey: rsa);

        Assert.False(result.SignatureIsValid);
        Assert.False(result.HashIsValid);
    }

    [Fact]
    public void Verify_OverflowingStoredHashRange_DoesNotThrowAndFails()
    {
        using var rsa = RSA.Create(2048);
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
        using var rsa = RSA.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(rsa, RSASignaturePadding.Pss);

        var result = AvbSignatureVerifier.Verify(vbmeta);

        Assert.True(result.HashIsValid);
        Assert.False(result.SignatureIsValid);
    }

    [Fact]
    public void Verify_WrongTrustedKey_Fails()
    {
        using var signer = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var (vbmeta, _) = BuildMinimalSelfSignedVbmeta(signer);

        var result = AvbSignatureVerifier.Verify(vbmeta, trustedPublicKey: other);

        Assert.False(result.SignatureIsValid);
        // The stored digest is unaffected by the key and must still check out.
        Assert.True(result.HashIsValid);
    }

    [Fact]
    public void VerifySignature_ExplicitPublicKeyBlob_Verifies()
    {
        using var rsa = RSA.Create(2048);
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
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var blob = BuildAvbRsaPublicKeyBlob(parameters.Modulus!);

        using var imported = AvbSignatureVerifier.ImportAvbRsaPublicKey(blob);

        Assert.NotNull(imported);
        Assert.Equal(parameters.Modulus, imported.ExportParameters(false).Modulus);
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
        WriteUInt32BE(header, 4, 1);   // required_libavb_version_major
        WriteUInt32BE(header, 8, 0);   // required_libavb_version_minor
        WriteUInt64BE(header, 12, authSize);
        WriteUInt64BE(header, 20, auxSize);
        WriteUInt32BE(header, 28, AlgorithmSha256Rsa2048);
        WriteUInt64BE(header, 32, 0);                       // hash_offset
        WriteUInt64BE(header, 40, Sha256Size);              // hash_size
        WriteUInt64BE(header, 48, Sha256Size);              // signature_offset
        WriteUInt64BE(header, 56, Rsa2048SignatureSize);    // signature_size
        WriteUInt64BE(header, 64, 0);                       // public_key_offset
        WriteUInt64BE(header, 72, (ulong)publicKeyBlob.Length); // public_key_size
        WriteUInt64BE(header, 80, 0);                       // public_key_metadata_offset
        WriteUInt64BE(header, 88, 0);                       // public_key_metadata_size
        WriteUInt64BE(header, 96, 0);                       // descriptors_offset
        WriteUInt64BE(header, 104, 0);                      // descriptors_size
        WriteUInt64BE(header, 112, 0);                      // rollback_index
        WriteUInt32BE(header, 120, 0);                      // flags
        WriteUInt32BE(header, 124, 0);                      // rollback_index_location
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
        var rr = BigInteger.ModPow(BigInteger.One << (keyNumBits * 2), 1, n); // (2^(2*bits)) mod n
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
        // Montgomery inverse: n0inv = -modulus^{-1} mod 2^32 (modulus is odd).
        // Newton iteration over the low 32 bits doubles correct bits each round.
        uint a = 0;
        for (var i = 0; i < 4 && i < modulus.Length; i++)
        {
            a |= (uint)modulus[i] << (8 * i);
        }

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
}