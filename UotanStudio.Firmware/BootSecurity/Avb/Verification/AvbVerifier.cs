using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

/// <summary>
/// Data carrier for the outcome of one AVB verification attempt.
/// <para>一次 AVB 验证尝试结果的数据载体。</para>
/// </summary>
public sealed class AvbVerifyResult
{
    /// <summary>Whether the verification path recognized AVB structures in the image.
    /// <para>验证流程是否在镜像中识别出 AVB 结构。</para></summary>
    /// <value>
    /// <see langword="true"/> once an AVB footer or standalone VBMeta header was located; <see cref="AvbVerifier.VerifyVBMeta"/> narrows it further to the presence of an embedded signature. <see langword="false"/> when the image is too small or holds no footer.
    /// <para>定位到 AVB 尾部或独立 VBMeta 头部后为 <see langword="true"/>；<see cref="AvbVerifier.VerifyVBMeta"/> 会将其进一步收窄为嵌入签名的存在。镜像过小或无尾部时为 <see langword="false"/>。</para>
    /// </value>
    public bool IsSigned { get; set; }

    /// <summary>Whether the VBMeta content passed both the hash and the signature checks.
    /// <para>VBMeta 内容是否同时通过哈希与签名校验。</para></summary>
    /// <value>
    /// <see langword="true"/> only when the VBMeta hash and signature pass; this does not verify partition data or establish trust in the embedded key.
    /// <para>仅在 VBMeta 哈希与签名通过时为 <see langword="true"/>；不代表已验证分区数据或信任内嵌密钥。</para>
    /// </value>
    public bool IsValid { get; set; }

    /// <summary>English reason describing why verification failed or stopped.
    /// <para>描述验证失败或中止原因的英文文本。</para></summary>
    /// <value>
    /// Not localized; may carry a message even when <see cref="IsValid"/> is <see langword="false"/> by omission, and stays <see langword="null"/> when the path set none.
    /// <para>不做本地化；即使 <see cref="IsValid"/> 因未执行而保持 <see langword="false"/>，也可能带有消息；当该路径未设置时保持 <see langword="null"/>。</para>
    /// </value>
    public string? ErrorMessage { get; set; }

    /// <summary>Parsed AVB footer from the end of the image.
    /// <para>从镜像末尾解析出的 AVB 尾部。</para></summary>
    /// <value>
    /// Set by the footer-based paths (<see cref="AvbVerifier.Verify"/> and <see cref="AvbVerifier.VerifyFromStream"/>) once the footer loads; <see langword="null"/> otherwise.
    /// <para>由基于尾部的路径（<see cref="AvbVerifier.Verify"/> 与 <see cref="AvbVerifier.VerifyFromStream"/>）在尾部加载成功后设置；其他情况为 <see langword="null"/>。</para>
    /// </value>
    public AvbFooter? Footer { get; set; }

    /// <summary>Parsed VBMeta image header.
    /// <para>解析出的 VBMeta 镜像头部。</para></summary>
    /// <value>
    /// Available whenever the VBMeta region bytes parsed without throwing, even if later validation fails; <see langword="null"/> when the region was never reached.
    /// <para>只要 VBMeta 区域字节成功解析且未抛出异常即可获得，即使后续校验失败；未读取该区域时为 <see langword="null"/>。</para>
    /// </value>
    public AvbVBMetaImageHeader? VBMetaHeader { get; set; }
}

/// <summary>
/// Verifies Android Verified Boot footers and VBMeta structures in whole image buffers or seekable streams, treating all input as untrusted.
/// <para>在完整镜像缓冲区或可定位流上验证 Android 验证启动（AVB）尾部与 VBMeta 结构，所有输入均视为不可信。</para>
/// </summary>
public static class AvbVerifier
{
    private const ulong MaximumMetadataBlockSize = 16 * 1024 * 1024;

    /// <summary>
    /// Checks whether the image carries an AVB footer.
    /// <para>检查镜像是否携带 AVB 尾部。</para>
    /// </summary>
    /// <param name="imageData">
    /// Complete image bytes; fewer than one footer length never match. <para>完整镜像字节；不足一个尾部长度时永不匹配。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a footer of the supported layout loads from the buffer; otherwise <see langword="false"/>. Reading a <see langword="null"/> buffer raises <see cref="NullReferenceException"/>.
    /// <para>当受支持布局的尾部能从缓冲区加载时为 <see langword="true"/>，否则为 <see langword="false"/>。对 <see langword="null"/> 缓冲区取值会引发 <see cref="NullReferenceException"/>。</para>
    /// </returns>
    public static bool IsSigned(byte[] imageData) => imageData.Length >= AvbFooter.Size && AvbFooter.TryLoad(imageData) != null;

    /// <summary>
    /// Verifies the complete VBMeta image referenced by an AVB footer.
    /// <para>验证 AVB 尾部所引用的完整 VBMeta 镜像。</para>
    /// </summary>
    /// <param name="imageData">
    /// Complete image bytes in memory; the footer must sit in the last <c>AvbFooter.Size</c> bytes and the VBMeta region must fall inside the buffer, otherwise the result reports the bound failure.
    /// <para>内存中的完整镜像字节；尾部必须位于最后 <c>AvbFooter.Size</c> 字节内，且 VBMeta 区域必须落在缓冲区之内，否则结果报告会界失败。</para>
    /// </param>
    /// <param name="certificateData">
    /// Optional certificate whose RSA public key is extracted as the trusted key; a <see langword="null"/> or empty value verifies against the public key embedded in VBMeta instead.
    /// <para>可选证书，从中提取 RSA 公钥作为可信密钥；为 <see langword="null"/> 或空时使用 VBMeta 内嵌公钥验证。</para>
    /// </param>
    /// <returns>
    /// An <see cref="AvbVerifyResult"/>. Every rejected layout (missing or unsupported footer, out-of-range VBMeta offset or size, unparsable or invalid header, unknown algorithm, bad ranges, NONE algorithm, failed hash or signature) is reported through <see cref="AvbVerifyResult.ErrorMessage"/> with <see cref="AvbVerifyResult.IsValid"/> left <see langword="false"/> rather than thrown.
    /// <para>返回 <see cref="AvbVerifyResult"/>。所有被拒绝的布局（缺失或不支持的尾部、越界的 VBMeta 偏移或大小、无法解析或无效的头、未知算法、范围错误、NONE 算法、哈希或签名验证失败）都通过 <see cref="AvbVerifyResult.ErrorMessage"/> 报告，且 <see cref="AvbVerifyResult.IsValid"/> 保持 <see langword="false"/>，而不抛出异常。</para>
    /// </returns>
    /// <example>
    /// <code>
    /// var result = AvbVerifier.Verify(File.ReadAllBytes("boot.img"));
    /// if (result.IsSigned &amp;&amp; !result.IsValid)
    ///     Console.WriteLine(result.ErrorMessage);
    /// </code>
    /// </example>
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
    /// <param name="imageData">
    /// VBMeta image bytes starting at the AVB0 header; trailing padding beyond the declared blocks is accepted.
    /// <para>以 AVB0 头起始的 VBMeta 镜像字节；接受超出声明块之后的尾部填充。</para>
    /// </param>
    /// <returns>
    /// An <see cref="AvbVerifyResult"/> verified against the embedded public key only. Structural and hash/signature failures are reported through <see cref="AvbVerifyResult.ErrorMessage"/> instead of throwing; a NONE algorithm is reported as unsigned.
    /// <para>返回仅使用内嵌公钥验证的 <see cref="AvbVerifyResult"/>。结构性失败与哈希/签名失败通过 <see cref="AvbVerifyResult.ErrorMessage"/> 报告而不抛出；NONE 算法被报告为未签名。</para>
    /// </returns>
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
    /// <param name="stream">
    /// Readable, seekable stream over the image; the caller keeps ownership, and the method repositions it (footer tail, then the VBMeta region) and leaves the position where the last read ended.
    /// <para>覆盖镜像的可读、可定位流；调用方保留所有权，方法会重新定位（尾部，然后 VBMeta 区域）并将位置停留在最后一次读取结束处。</para>
    /// </param>
    /// <param name="imageLength">
    /// Declared total image length in bytes; the footer is sought at <c>imageLength - AvbFooter.Size</c> and declared offsets are validated against this value.
    /// <para>声明的镜像总长度（字节）；尾部在 <c>imageLength - AvbFooter.Size</c> 处查找，声明的偏移会依据此值校验。</para>
    /// </param>
    /// <returns>
    /// An <see cref="AvbVerifyResult"/> verified against the embedded public key. Each metadata block is limited to 16 MiB before allocation.
    /// Footer-range, size-limit, header and algorithm failures are reported through <see cref="AvbVerifyResult.ErrorMessage"/>.
    /// <para>返回使用内嵌公钥验证的 <see cref="AvbVerifyResult"/>；分配前限制每个元数据块不超过 16 MiB。尾部范围、大小限制、头部与算法错误通过 <see cref="AvbVerifyResult.ErrorMessage"/> 报告。</para>
    /// </returns>
    /// <exception cref="EndOfStreamException">The stream holds fewer bytes than <paramref name="imageLength"/> declares.
    /// <para>流的实际字节数少于 <paramref name="imageLength"/> 声明的长度。</para></exception>
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


        var verified = VerifyRegionFromStream(stream, (long)footer.Value.VbmetaOffset, footer.Value.VbmetaSize, false);
        result.VBMetaHeader = verified.VBMetaHeader;
        result.IsValid = verified.IsValid;
        result.ErrorMessage = verified.ErrorMessage;
        return result;
    }

    /// <summary>
    /// Verifies a standalone VBMeta hash and signature from a seekable stream, reading only the bounded metadata region.
    /// <para>从可定位流验证独立 VBMeta 的哈希与签名，仅读取有界的元数据区域。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable, seekable stream over the VBMeta image; the method seeks to offset 0 and leaves the position after the last metadata read. The caller keeps ownership.
    /// <para>覆盖 VBMeta 镜像的可读、可定位流；方法定位到偏移 0，位置停留在最后一次元数据读取之后，调用方保留所有权。</para>
    /// </param>
    /// <param name="imageLength">
    /// Declared total image length in bytes; header and block ranges are validated against this value with trailing padding allowed.
    /// <para>声明的镜像总长度（字节）；头部与块范围依据此值校验，并允许尾部填充。</para>
    /// </param>
    /// <returns>
    /// An embedded-key verification result, without establishing external trust or verifying partition content.
    /// Each authentication or auxiliary block is limited to 16 MiB before allocation; trailing image padding is not read.
    /// <para>返回内嵌密钥验证结果，不建立外部信任，也不验证分区内容。分配前限制认证块和辅助块各不超过 16 MiB，不读取镜像尾部填充。</para>
    /// </returns>
    /// <exception cref="EndOfStreamException">The header or declared metadata bytes cannot be read completely.
    /// <para>无法完整读取头部或声明的元数据字节。</para></exception>
    public static AvbVerifyResult VerifyVBMetaFromStream(Stream stream, long imageLength)
    {
        if (imageLength < AvbVBMetaImageHeader.Size)
        {
            return new AvbVerifyResult { ErrorMessage = "Image too small to contain a VBMeta header." };
        }
        return VerifyRegionFromStream(stream, 0, (ulong)imageLength, true);
    }

    private static AvbVerifyResult VerifyRegionFromStream(Stream stream, long offset, ulong availableSize, bool allowTrailingPadding)
    {
        var result = new AvbVerifyResult();
        Span<byte> headerBytes = stackalloc byte[AvbVBMetaImageHeader.Size];
        stream.Position = offset;
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
        if (!TryValidateVbmetaRanges(header, availableSize, out var error, allowTrailingPadding))
        {
            result.ErrorMessage = error;
            return result;
        }

        if (header.AuthenticationDataBlockSize > MaximumMetadataBlockSize || header.AuxiliaryDataBlockSize > MaximumMetadataBlockSize)
        {
            result.ErrorMessage = "VBMeta metadata exceeds the 16 MiB limit per block.";
            return result;
        }

        var size = checked(AvbVBMetaImageHeader.Size + (int)header.AuthenticationDataBlockSize + (int)header.AuxiliaryDataBlockSize);
        var vbmeta = new byte[size];
        headerBytes.CopyTo(vbmeta);
        stream.ReadExactly(vbmeta.AsSpan(AvbVBMetaImageHeader.Size));
        return VerifyVBMeta(vbmeta);
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