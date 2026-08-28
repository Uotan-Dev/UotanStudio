using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// Verification result of an AVB-signed image.
/// <para>AVB 签名镜像的校验结果。</para>
/// </summary>
public sealed class AvbVerifyResult
{
    /// <summary>Gets or sets whether an AVB footer or structurally valid standalone VBMeta image was recognized. This does not imply a valid signature. <para>获取或设置是否识别到 AVB 尾部或结构有效的独立 VBMeta 镜像；不表示签名有效。</para></summary>
    public bool IsSigned { get; set; }

    /// <summary>Gets or sets whether verification succeeded. <para>获取或设置验证是否成功。</para></summary>
    public bool IsValid { get; set; }

    /// <summary>Gets or sets the error message when verification fails. <para>获取或设置验证失败时的错误信息。</para></summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the parsed AVB footer, if present. <para>获取或设置解析出的 AVB 尾部（若存在）。</para></summary>
    public AvbFooter? Footer { get; set; }

    /// <summary>Gets or sets the parsed VBMeta image header, if present. <para>获取或设置解析出的 VBMeta 镜像头（若存在）。</para></summary>
    public AvbVBMetaImageHeader? VBMetaHeader { get; set; }
}

/// <summary>
/// Verifies Android Verified Boot footer images and standalone VBMeta images.
/// <para>验证 Android Verified Boot 尾部镜像和独立 VBMeta 镜像。</para>
/// </summary>
public static class AvbVerifier
{
    /// <summary>Checks whether the image carries an AVB footer. <para>检查镜像是否携带 AVB 尾部。</para></summary>
    /// <param name="imageData">The image bytes. <para>镜像字节。</para></param>
    /// <returns><c>true</c> if a standard footer is present. <para>存在标准尾部时返回 <c>true</c>。</para></returns>
    public static bool IsSigned(byte[] imageData) => imageData.Length >= AvbFooter.Size && AvbFooter.TryLoad(imageData) != null;

    /// <summary>Verifies the complete VBMeta image referenced by an AVB footer. <para>验证 AVB 尾部所引用的完整 VBMeta 镜像。</para></summary>
    /// <param name="imageData">The partition image bytes. <para>分区镜像字节。</para></param>
    /// <param name="certificateData">Optional trusted X.509 certificate containing an RSA public key. <para>可选的受信任 X.509 证书，其中包含 RSA 公钥。</para></param>
    /// <returns>The verification result. <para>验证结果。</para></returns>
    public static AvbVerifyResult Verify(byte[] imageData, byte[]? certificateData = null)
    {
        var result = new AvbVerifyResult();
        if (imageData.Length < AvbFooter.Size)
        {
            result.ErrorMessage = "Image too small to contain an AVB footer.";
            return result;
        }

        var footer = AvbFooter.TryLoad(imageData);
        if (footer == null)
        {
            var footerBytes = imageData.AsSpan(imageData.Length - AvbFooter.Size, AvbFooter.Size);
            if (footerBytes[..4].SequenceEqual("AVBf"u8))
            {
                var major = BinaryPrimitives.ReadUInt32BigEndian(footerBytes.Slice(4, 4));
                var minor = BinaryPrimitives.ReadUInt32BigEndian(footerBytes.Slice(8, 4));
                result.ErrorMessage = $"Unsupported AVB footer version {major}.{minor}; supported version is {AvbFooter.SupportedVersionMajor}.{AvbFooter.SupportedVersionMinor}.";
            }
            else
            {
                result.ErrorMessage = "No AVB footer found.";
            }
            return result;
        }

        result.IsSigned = true;
        result.Footer = footer;
        var footerOffset = (ulong)(imageData.Length - AvbFooter.Size);
        if (footer.Value.OriginalImageSize > footer.Value.VbmetaOffset ||
            footer.Value.VbmetaOffset > footerOffset ||
            footer.Value.VbmetaSize > footerOffset - footer.Value.VbmetaOffset ||
            footer.Value.VbmetaSize < AvbVBMetaImageHeader.Size ||
            footer.Value.VbmetaOffset > int.MaxValue ||
            footer.Value.VbmetaSize > int.MaxValue)
        {
            result.ErrorMessage = "Invalid AVB footer image size, VBMeta offset, or VBMeta size.";
            return result;
        }

        var vbmetaOffset = (int)footer.Value.VbmetaOffset;
        var vbmetaSize = (int)footer.Value.VbmetaSize;
        var vbmeta = imageData.AsSpan(vbmetaOffset, vbmetaSize).ToArray();
        AvbVBMetaImageHeader header;
        try
        {
            header = AvbVBMetaImageHeader.FromBytes(vbmeta);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }

        result.VBMetaHeader = header;
        if (!header.IsValid())
        {
            result.ErrorMessage = "Invalid VBMeta header.";
            return result;
        }

        if (!AvbSignatureVerifier.TryGetKeySizeAndHash(header.AlgorithmType, out _, out _))
        {
            result.ErrorMessage = $"Unknown VBMeta algorithm type {header.AlgorithmType}.";
            return result;
        }

        if (!TryValidateVbmetaRanges(header, (ulong)vbmeta.Length, out var rangeError))
        {
            result.ErrorMessage = rangeError;
            return result;
        }

        RSA? trustedKey = null;
        try
        {
            if (certificateData is { Length: > 0 })
            {
                trustedKey = ExtractRsaPublicKeyFromCertificate(certificateData);
                if (trustedKey == null)
                {
                    result.ErrorMessage = "Failed to extract RSA public key from certificate.";
                    return result;
                }
            }

            var signatureResult = AvbSignatureVerifier.Verify(vbmeta, trustedKey);
            if (header.AlgorithmType == AvbSignatureVerifier.AlgorithmNone)
            {
                result.ErrorMessage = "VBMeta algorithm NONE is unsigned.";
                return result;
            }

            result.IsValid = signatureResult.HashIsValid && signatureResult.SignatureIsValid;
            if (!result.IsValid)
            {
                result.ErrorMessage = signatureResult.ErrorMessage ?? (trustedKey != null
                    ? "VBMeta hash or trusted signature verification failed."
                    : "VBMeta hash or embedded-key signature verification failed.");
            }
        }
        finally
        {
            trustedKey?.Dispose();
        }

        return result;
    }

    /// <summary>Validates a standalone VBMeta image and returns its parsed header. <para>验证独立 VBMeta 镜像并返回解析出的头。</para></summary>
    /// <param name="imageData">The VBMeta image bytes. <para>VBMeta 镜像字节。</para></param>
    /// <returns>The verification result. <para>验证结果。</para></returns>
    public static AvbVerifyResult VerifyVBMeta(byte[] imageData)
    {
        var result = new AvbVerifyResult();
        if (imageData.Length < AvbVBMetaImageHeader.Size)
        {
            result.ErrorMessage = "Image too small to contain a VBMeta header.";
            return result;
        }

        AvbVBMetaImageHeader header;
        try
        {
            header = AvbVBMetaImageHeader.FromBytes(imageData);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }

        if (!header.IsValid())
        {
            result.ErrorMessage = "Invalid VBMeta magic (expected AVB0).";
            return result;
        }

        result.VBMetaHeader = header;
        if (!TryValidateVbmetaRanges(header, (ulong)imageData.Length, out var error, allowTrailingPadding: true))
        {
            result.ErrorMessage = error;
            return result;
        }

        result.IsSigned = true;
        if (header.AlgorithmType == AvbSignatureVerifier.AlgorithmNone)
        {
            result.ErrorMessage = "VBMeta algorithm NONE is unsigned.";
            return result;
        }

        if (!AvbSignatureVerifier.TryGetKeySizeAndHash(header.AlgorithmType, out _, out _))
        {
            result.ErrorMessage = $"Unknown VBMeta algorithm type {header.AlgorithmType}.";
            return result;
        }

        var signatureResult = AvbSignatureVerifier.Verify(imageData);
        result.IsSigned = signatureResult.IsSigned;
        result.IsValid = signatureResult.HashIsValid && signatureResult.SignatureIsValid;
        if (!result.IsValid)
        {
            result.ErrorMessage = signatureResult.ErrorMessage ?? "VBMeta hash or embedded-key signature verification failed.";
        }

        return result;
    }

    private static bool TryValidateVbmetaRanges(AvbVBMetaImageHeader header, ulong vbmetaSize, out string? error, bool allowTrailingPadding = false)
    {
        if (header.AuthenticationDataBlockSize > ulong.MaxValue - header.AuxiliaryDataBlockSize)
        {
            error = "VBMeta authentication and auxiliary block sizes overflow.";
            return false;
        }

        if (vbmetaSize < AvbVBMetaImageHeader.Size)
        {
            error = "VBMeta image is smaller than its header.";
            return false;
        }

        var blocksSize = header.AuthenticationDataBlockSize + header.AuxiliaryDataBlockSize;
        var availableSize = vbmetaSize - AvbVBMetaImageHeader.Size;
        if (blocksSize > availableSize || (!allowTrailingPadding && blocksSize != availableSize))
        {
            error = "VBMeta authentication or auxiliary block exceeds the declared image size.";
            return false;
        }

        if (!RangeWithin(header.HashOffset, header.HashSize, header.AuthenticationDataBlockSize) ||
            !RangeWithin(header.SignatureOffset, header.SignatureSize, header.AuthenticationDataBlockSize) ||
            !RangeWithin(header.PublicKeyOffset, header.PublicKeySize, header.AuxiliaryDataBlockSize) ||
            !RangeWithin(header.PublicKeyMetadataOffset, header.PublicKeyMetadataSize, header.AuxiliaryDataBlockSize) ||
            !RangeWithin(header.DescriptorsOffset, header.DescriptorsSize, header.AuxiliaryDataBlockSize))
        {
            error = "VBMeta authentication or auxiliary data range is out of bounds.";
            return false;
        }

        if (header.AlgorithmType == AvbSignatureVerifier.AlgorithmNone &&
            (header.AuthenticationDataBlockSize != 0 || header.HashSize != 0 || header.SignatureSize != 0))
        {
            error = "VBMeta algorithm NONE must not contain authentication data.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool RangeWithin(ulong offset, ulong size, ulong containerSize) =>
        offset <= containerSize && size <= containerSize - offset;

    private static RSA? ExtractRsaPublicKeyFromCertificate(byte[] certificateData)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(certificateData);
            return cert.GetRSAPublicKey();
        }
        catch
        {
            return null;
        }
    }
}