using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// Verification result of an AVB-signed image.
/// <para>AVB 签名镜像的校验结果。</para>
/// </summary>
public sealed class AvbVerifyResult
{
    /// <summary>
    /// Gets or sets whether the image carries an AVB signature.
    /// <para>获取或设置镜像是否携带 AVB 签名。</para>
    /// </summary>
    public bool IsSigned { get; set; }

    /// <summary>
    /// Gets or sets whether the signature is valid.
    /// <para>获取或设置签名是否有效。</para>
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Gets or sets the error message when verification fails.
    /// <para>获取或设置校验失败时的错误信息。</para>
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the parsed AVB footer, if present.
    /// <para>获取或设置解析出的 AVB 尾部(若存在)。</para>
    /// </summary>
    public AvbFooter? Footer { get; set; }

    /// <summary>
    /// Gets or sets the parsed VBMeta image header, if present.
    /// <para>获取或设置解析出的 VBMeta 镜像头部(若存在)。</para>
    /// </summary>
    public AvbVBMetaImageHeader? VBMetaHeader { get; set; }
}

/// <summary>
/// AVB 1.0 signature verification tool, ported from MagiskBoot's AvbTool.
/// <para>AVB 1.0 签名验证工具,移植自 MagiskBoot 的 AvbTool。</para>
/// </summary>
public static class AvbVerifier
{

    /// <summary>
    /// Checks whether the image carries an AVB footer.
    /// <para>检查镜像是否携带 AVB 尾部。</para>
    /// </summary>
    public static bool IsSigned(byte[] imageData) => imageData.Length >= AvbFooter.Size && AvbFooter.TryLoad(imageData) != null;

    /// <summary>
    /// Verifies the AVB footer signature of a boot image.
    /// <para>校验启动镜像的 AVB 尾部签名。</para>
    /// </summary>
    /// <param name="imageData">The boot image bytes. <para>启动镜像字节。</para></param>
    /// <param name="certificateData">Optional X509 certificate data for RSA verification. <para>可选的 X509 证书数据,用于 RSA 校验。</para></param>
    /// <returns>The verification result. <para>校验结果。</para></returns>
    public static AvbVerifyResult Verify(byte[] imageData, byte[]? certificateData = null)
    {
        var result = new AvbVerifyResult { IsSigned = false };

        if (imageData.Length < AvbFooter.Size)
        {
            result.ErrorMessage = "Image too small to contain an AVB footer.";
            return result;
        }

        var footer = AvbFooter.TryLoad(imageData);
        if (footer == null)
        {
            result.ErrorMessage = "No AVB footer found.";
            return result;
        }

        result.IsSigned = true;
        result.Footer = footer.Value;

        try
        {
            var authDataOffset = (int)footer.Value.AuthDataOffset;
            var authDataSize = (int)footer.Value.AuthDataSize;

            if (authDataOffset <= 0 || authDataSize <= 0 ||
                (ulong)(authDataOffset + authDataSize) > (ulong)imageData.Length)
            {
                result.IsValid = false;
                result.ErrorMessage = "Invalid authentication data offset or size";
                return result;
            }

            var hashSize = GetHashSize(footer.Value.AlgorithmName);
            if (authDataSize < hashSize + 256)
            {
                result.IsValid = false;
                result.ErrorMessage = "Authentication data too small";
                return result;
            }

            var hashData = new byte[hashSize];
            Array.Copy(imageData, authDataOffset, hashData, 0, hashSize);

            var signatureData = new byte[authDataSize - hashSize];
            Array.Copy(imageData, authDataOffset + hashSize, signatureData, 0, signatureData.Length);

            var dataToHash = new byte[authDataOffset];
            Array.Copy(imageData, dataToHash, authDataOffset);

            var hashAlgorithm = GetHashAlgorithm(footer.Value.AlgorithmName);
            var computedHash = SHA256.HashData(dataToHash);
            if (hashAlgorithm != "SHA256")
            {
                computedHash = SHA512.HashData(dataToHash);
            }

            result.IsValid = false;

            if (certificateData is { Length: > 0 })
            {
                var rsa = ExtractRsaPublicKeyFromCertificate(certificateData);
                if (rsa != null)
                {
                    using (rsa)
                    {
                        result.IsValid = rsa.VerifyData(dataToHash, signatureData,
                            hashAlgorithm == "SHA256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA512,
                            RSASignaturePadding.Pkcs1);
                    }
                }
                else
                {
                    result.ErrorMessage = "Failed to extract RSA public key from certificate";
                }
            }
            else
            {
                // Without a certificate we can only verify the hash stored in the auth block.
                var hashMatch = computedHash.AsSpan().SequenceEqual(hashData);
                result.IsValid = hashMatch;
                if (!hashMatch)
                {
                    result.ErrorMessage = "Hash mismatch: image content changed since signing.";
                }
            }
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Validates a standalone VBMeta image (magic "AVB0"), returning header summary.
    /// <para>校验独立的 VBMeta 镜像(魔数 "AVB0"),返回头部摘要。</para>
    /// </summary>
    public static AvbVerifyResult VerifyVBMeta(byte[] imageData)
    {
        var result = new AvbVerifyResult { IsSigned = false };
        if (imageData.Length < AvbVBMetaImageHeader.Size)
        {
            result.ErrorMessage = "Image too small to contain a VBMeta header.";
            return result;
        }

        var header = AvbVBMetaImageHeader.FromBytes(imageData);
        if (!header.IsValid())
        {
            result.ErrorMessage = "Invalid VBMeta magic (expected AVB0).";
            return result;
        }

        result.IsSigned = true;
        result.VBMetaHeader = header;

        // Check that declared offsets stay within the image.
        var authSize = header.AuthenticationDataBlockSize;
        var auxSize = header.AuxiliaryDataBlockSize;
        var ok = authSize + auxSize <= (ulong)imageData.Length - AvbVBMetaImageHeader.Size;
        result.IsValid = ok;
        if (!ok)
        {
            result.ErrorMessage = "VBMeta data blocks exceed image size.";
        }
        return result;
    }

    private static int GetHashSize(string algorithmName) => algorithmName.StartsWith("SHA512", StringComparison.OrdinalIgnoreCase) ? 64 : 32;

    private static string GetHashAlgorithm(string algorithmName) => algorithmName.StartsWith("SHA512", StringComparison.OrdinalIgnoreCase) ? "SHA512" : "SHA256";

    private static RSA? ExtractRsaPublicKeyFromCertificate(byte[] certificateData)
    {
        try
        {
            using var cert = new X509Certificate2(certificateData);
            return cert.GetRSAPublicKey();
        }
        catch
        {
            try
            {
                return RSA.Create();
            }
            catch
            {
                return null;
            }
        }
    }
}