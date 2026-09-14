using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;

public sealed class AvbVerifyResult
{
    public bool IsSigned { get; set; }

    public bool IsValid { get; set; }

    public string? ErrorMessage { get; set; }

    public AvbFooter? Footer { get; set; }

    public AvbVBMetaImageHeader? VBMetaHeader { get; set; }
}

public static class AvbVerifier
{
    /// <summary>
    /// Checks whether the image carries an AVB footer.
    /// <para>检查镜像是否携带 AVB 尾部。</para>
    /// </summary>
    public static bool IsSigned(byte[] imageData) => imageData.Length >= AvbFooter.Size && AvbFooter.TryLoad(imageData) != null;

    /// <summary>
    /// Verifies the complete VBMeta image referenced by an AVB footer.
    /// <para>验证 AVB 尾部所引用的完整 VBMeta 镜像。</para>
    /// </summary>
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
                result.ErrorMessage = $"Unsupported AVB footer version {major}.{minor}; supported major version is {AvbFooter.SupportedVersionMajor}.";
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

    /// <summary>
    /// Validates a standalone VBMeta image and returns its parsed header.
    /// <para>验证独立 VBMeta 镜像并返回解析出的头。</para>
    /// </summary>
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

    /// <summary>
    /// Verifies an AVB-signed image from a seekable stream, reading only the footer and the VBMeta region instead of loading the whole file.
    /// <para>从可定位流验证 AVB 签名镜像，仅读取尾部和 VBMeta 区域而非整文件。</para>
    /// </summary>
    public static AvbVerifyResult VerifyFromStream(Stream stream, long imageLength)
    {
        var result = new AvbVerifyResult();
        if (imageLength < AvbFooter.Size)
        {
            result.ErrorMessage = "Image too small to contain an AVB footer.";
            return result;
        }


        Span<byte> tail = stackalloc byte[AvbFooter.Size];
        stream.Position = imageLength - AvbFooter.Size;
        stream.ReadExactly(tail);
        var footer = AvbFooter.TryLoad(tail);
        if (footer == null)
        {
            result.ErrorMessage = "No AVB footer found.";
            return result;
        }

        result.IsSigned = true;
        result.Footer = footer;
        var footerOffset = (ulong)(imageLength - AvbFooter.Size);
        if (footer.Value.OriginalImageSize > footer.Value.VbmetaOffset ||
            footer.Value.VbmetaOffset > footerOffset ||
            footer.Value.VbmetaSize > footerOffset - footer.Value.VbmetaOffset ||
            footer.Value.VbmetaSize < AvbVBMetaImageHeader.Size ||
            footer.Value.VbmetaOffset > long.MaxValue ||
            footer.Value.VbmetaSize > long.MaxValue)
        {
            result.ErrorMessage = "Invalid AVB footer image size, VBMeta offset, or VBMeta size.";
            return result;
        }


        var readOffset = (long)footer.Value.VbmetaOffset;
        var readSize = (int)footer.Value.VbmetaSize;
        var vbmeta = new byte[readSize];
        stream.Position = readOffset;
        stream.ReadExactly(vbmeta);

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

        if (!TryValidateVbmetaRanges(header, (ulong)vbmeta.Length, out var rangeError))
        {
            result.ErrorMessage = rangeError;
            return result;
        }

        if (!AvbSignatureVerifier.TryGetKeySizeAndHash(header.AlgorithmType, out _, out _))
        {
            result.ErrorMessage = $"Unknown VBMeta algorithm type {header.AlgorithmType}.";
            return result;
        }

        var signatureResult = AvbSignatureVerifier.Verify(vbmeta);
        if (header.AlgorithmType == AvbSignatureVerifier.AlgorithmNone)
        {
            result.ErrorMessage = "VBMeta algorithm NONE is unsigned.";
            return result;
        }

        result.IsValid = signatureResult.HashIsValid && signatureResult.SignatureIsValid;
        if (!result.IsValid)
        {
            result.ErrorMessage = signatureResult.ErrorMessage ?? "VBMeta hash or embedded-key signature verification failed.";
        }

        return result;
    }

    /// <summary>
    /// Validates a standalone VBMeta image from a seekable stream, reading only the header and declared block sizes.
    /// <para>从可定位流验证独立 VBMeta 镜像，仅读取头部和声明的块大小。</para>
    /// </summary>
    public static AvbVerifyResult VerifyVBMetaFromStream(Stream stream, long imageLength)
    {
        var result = new AvbVerifyResult();
        if (imageLength < AvbVBMetaImageHeader.Size)
        {
            result.ErrorMessage = "Image too small to contain a VBMeta header.";
            return result;
        }


        Span<byte> headerBytes = stackalloc byte[AvbVBMetaImageHeader.Size];
        stream.Position = 0;
        stream.ReadExactly(headerBytes);

        AvbVBMetaImageHeader header;
        try
        {
            header = AvbVBMetaImageHeader.FromBytes(headerBytes);
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
        if (!TryValidateVbmetaRanges(header, (ulong)imageLength, out var error, allowTrailingPadding: true))
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