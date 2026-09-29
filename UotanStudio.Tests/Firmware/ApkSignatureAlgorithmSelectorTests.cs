using System.Security.Cryptography;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// APK v2/v3 algorithm-selection tests. The selector mirrors apksigner's deterministic defaults, so the RSA and EC
/// digest upgrades are pinned at the exact key-size boundaries and the unsupported-key rejection is checked without
/// touching the signing pipeline.
/// <para>APK v2/v3 算法选择测试。选择器对齐 apksigner 的确定性默认策略，因此在精确的密钥位数边界上钉住 RSA 与 EC
/// 的摘要升级，并在不触及签名流水线的前提下校验不支持密钥的拒绝行为。</para>
/// </summary>
public class ApkSignatureAlgorithmSelectorTests
{
    [Theory]
    [InlineData(2048, ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha256)]
    [InlineData(3072, ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha256)]
    [InlineData(4096, ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha512)]
    public void Select_RsaKeySizeBoundaries_FollowsApksignerDigestUpgrade(int bits, ApkSignatureConstants.SignatureAlgorithm expected)
    {
        using var key = SigningTestKeys.Create(bits);

        Assert.Equal(bits, key.KeySize);
        Assert.Equal(expected, ApkSignatureAlgorithmSelector.Select(key, minSdkVersion: 24));
    }

    [Theory]
    [InlineData(256, ApkSignatureConstants.SignatureAlgorithm.EcdsaSha256)]
    [InlineData(384, ApkSignatureConstants.SignatureAlgorithm.EcdsaSha512)]
    [InlineData(521, ApkSignatureConstants.SignatureAlgorithm.EcdsaSha512)]
    public void Select_EcdsaKeySizeBoundaries_FollowsApksignerDigestUpgrade(int expectedKeySize, ApkSignatureConstants.SignatureAlgorithm expected)
    {
        using var key = ECDsa.Create(Curve(expectedKeySize));

        Assert.Equal(expectedKeySize, key.KeySize);
        Assert.Equal(expected, ApkSignatureAlgorithmSelector.Select(key, minSdkVersion: 24));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23)]
    [InlineData(28)]
    [InlineData(int.MaxValue)]
    public void Select_MinSdkVersion_DoesNotChangeTheSelectedAlgorithm(int minSdkVersion)
    {
        using var rsa = SigningTestKeys.Create(2048);
        using var ec = ECDsa.Create(Curve(256));

        Assert.Equal(ApkSignatureConstants.SignatureAlgorithm.RsaPkcs1Sha256,
            ApkSignatureAlgorithmSelector.Select(rsa, minSdkVersion));
        Assert.Equal(ApkSignatureConstants.SignatureAlgorithm.EcdsaSha256,
            ApkSignatureAlgorithmSelector.Select(ec, minSdkVersion));
    }

    [Fact]
    public void Select_UnsupportedKeyType_ThrowsNotSupportedExceptionNamingTheType()
    {
        using var key = new UnsupportedKey();

        var exception = Assert.Throws<NotSupportedException>(() => ApkSignatureAlgorithmSelector.Select(key, minSdkVersion: 24));

        Assert.Contains(nameof(UnsupportedKey), exception.Message, StringComparison.Ordinal);
        Assert.Contains("RSA", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_NullKey_ThrowsInsteadOfReturningAnAlgorithm()
        => Assert.ThrowsAny<Exception>(() => ApkSignatureAlgorithmSelector.Select(null!, minSdkVersion: 24));

    private static ECCurve Curve(int keySize) => keySize switch
    {
        256 => ECCurve.NamedCurves.nistP256,
        384 => ECCurve.NamedCurves.nistP384,
        _ => ECCurve.NamedCurves.nistP521
    };

    private sealed class UnsupportedKey : AsymmetricAlgorithm;
}