using System.Numerics;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Adb;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Regression tests for the ADB AUTH token signing construction: upstream adb and adbd both pass the
/// 20-byte challenge to <c>RSA_sign(NID_sha1, token, 20)</c>, which places the challenge bytes verbatim
/// inside the PKCS#1 v1.5 DigestInfo. Hashing the token first produced signatures adbd rejected, so
/// devices re-challenged forever and the USB authorization prompt appeared on every connection.
/// <para>ADB AUTH 令牌签名构造的回归测试：上游 adb 与 adbd 都把 20 字节挑战直接传入
/// <c>RSA_sign(NID_sha1, token, 20)</c>，挑战字节原样进入 PKCS#1 v1.5 的 DigestInfo。若先对令牌
/// 再算一次哈希，adbd 会拒绝签名并无限重新挑战，导致每次连接都弹出 USB 授权提示。</para>
/// </summary>
public sealed class AdbTokenSigningTests
{
    // PKCS#1 v1.5 DigestInfo prefix for a SHA-1 digest: 30 21 | 30 09 06 05 2b 0e 03 02 1a 05 00 | 04 14.
    private static readonly byte[] Sha1DigestInfoPrefix =
        [0x30, 0x21, 0x30, 0x09, 0x06, 0x05, 0x2b, 0x0e, 0x03, 0x02, 0x1a, 0x05, 0x00, 0x04, 0x14];

    /// <summary>Recovers the padded plaintext of a PKCS#1 v1.5 signature with the public key.
    /// <para>用公钥还原 PKCS#1 v1.5 签名的填充明文。</para></summary>
    private static byte[] RecoverPaddedPlaintext(byte[] signature, RSA publicKey)
    {
        var parameters = publicKey.ExportParameters(false);
        var modulus = parameters.Modulus!;
        var exponent = parameters.Exponent!;
        var signatureValue = BigInteger.Zero;
        for (var i = 0; i < signature.Length; i++)
        {
            signatureValue = (signatureValue << 8) | signature[i];
        }

        var modulusValue = BigInteger.Zero;
        foreach (var b in modulus)
        {
            modulusValue = (modulusValue << 8) | b;
        }

        var exponentValue = BigInteger.Zero;
        foreach (var b in exponent)
        {
            exponentValue = (exponentValue << 8) | b;
        }

        var recovered = BigInteger.ModPow(signatureValue, exponentValue, modulusValue).ToByteArray();
        Array.Reverse(recovered);
        if (recovered.Length < signature.Length)
        {
            var padded = new byte[signature.Length];
            Array.Copy(recovered, 0, padded, signature.Length - recovered.Length, recovered.Length);
            return padded;
        }

        return recovered;
    }

    [Fact]
    public void SignToken_DigestInfoCarriesChallengeVerbatim()
    {
        byte[] token = Enumerable.Range(0, 20).Select(static i => (byte)i).ToArray();
        using var authentication = AdbAuthentication.CreateNew();
        var signature = authentication.SignToken(token);

        var plaintext = RecoverPaddedPlaintext(signature, authentication.RsaKey);
        Assert.Equal(256, plaintext.Length);
        Assert.Equal(0x00, plaintext[0]);
        Assert.Equal(0x01, plaintext[1]);
        Assert.Equal(0x00, plaintext[220]);

        // Tail layout: DigestInfo(15-byte prefix + 20-byte digest slot).
        var info = plaintext[221..];
        Assert.Equal(Sha1DigestInfoPrefix, info[..Sha1DigestInfoPrefix.Length].ToArray());

        // The 20-byte digest slot carries the challenge verbatim, never SHA1(token).
        var digestSlot = info[Sha1DigestInfoPrefix.Length..];
        Assert.Equal(token, digestSlot);
        Assert.NotEqual(SHA1.HashData(token), digestSlot);
    }

    [Fact]
    public void SignToken_TwentyByteToken_ProducesSignature256Bytes()
    {
        using var authentication = AdbAuthentication.CreateNew();
        var signature = authentication.SignToken(new byte[AdbAuthentication.TokenSize]);
        Assert.Equal(256, signature.Length);
    }

    [Fact]
    public void SignToken_TokenNotTwentyBytes_Throws()
    {
        using var authentication = AdbAuthentication.CreateNew();
        Assert.Throws<ArgumentException>(() => authentication.SignToken(new byte[16]));
    }

    [Fact]
    public void SignToken_NullToken_Throws()
    {
        using var authentication = AdbAuthentication.CreateNew();
        Assert.Throws<ArgumentNullException>(() => authentication.SignToken(null!));
    }
}