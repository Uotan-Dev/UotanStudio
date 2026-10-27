using System.Security.Cryptography;

namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

/// <summary>
/// Outcome of verifying one signed partition image: footer geometry, VBMeta signature and the content checks
/// performed against every hash or hashtree descriptor embedded in that VBMeta block.
/// <para>单个已签名分区镜像的校验结果：尾部几何、VBMeta 签名，以及针对该 VBMeta 块中每个哈希或哈希树描述符执行的内容检查。</para>
/// </summary>
public sealed class AvbImageVerifyResult
{
    /// <summary>Whether the image carries a supported AVB footer. <para>镜像是否携带受支持的 AVB 尾部。</para></summary>
    /// <value><see langword="true"/> once the footer parses. <para>尾部解析成功时为 <see langword="true"/>。</para></value>
    public bool IsSigned { get; init; }

    /// <summary>Whether the VBMeta hash and RSA signature are valid.
    /// <para>VBMeta 哈希与 RSA 签名是否有效。</para></summary>
    /// <value><see langword="true"/> only when both checks pass; trust in the embedded key is not implied.
    /// <para>仅当两项检查都通过时为 <see langword="true"/>；不代表信任内嵌密钥。</para></value>
    public bool SignatureIsValid { get; init; }

    /// <summary>Whether every hash or hashtree descriptor matches the image bytes.
    /// <para>每个哈希或哈希树描述符是否与镜像字节匹配。</para></summary>
    /// <value><see langword="true"/> when at least one content descriptor exists and all of them match; an image without one keeps this <see langword="false"/>.
    /// <para>至少存在一个内容描述符且全部匹配时为 <see langword="true"/>；没有内容描述符时保持 <see langword="false"/>。</para></value>
    public bool ContentIsValid { get; init; }

    /// <summary>FEC parity outcome when a descriptor declares parity; otherwise <see langword="null"/>.
    /// <para>描述符声明校验码时的 FEC 校验结果；否则为 <see langword="null"/>。</para></summary>
    /// <value><see langword="true"/>, <see langword="false"/>, or <see langword="null"/> when parity was not checked.
    /// <para>校验通过、失败，或未检查时为 <see langword="null"/>。</para></value>
    public bool? FecIsValid { get; init; }

    /// <summary>Parsed footer; <see langword="null"/> when the image is unsigned.
    /// <para>解析出的尾部；镜像未签名时为 <see langword="null"/>。</para></summary>
    /// <value>The footer geometry. <para>尾部几何信息。</para></value>
    public AvbFooter? Footer { get; init; }

    /// <summary>Parsed VBMeta header; <see langword="null"/> when the metadata region was not reached.
    /// <para>解析出的 VBMeta 头部；未读取元数据区时为 <see langword="null"/>。</para></summary>
    /// <value>The header fields. <para>头部字段。</para></value>
    public AvbVBMetaImageHeader? VBMetaHeader { get; init; }

    /// <summary>Descriptors parsed from the embedded VBMeta image in wire order.
    /// <para>按线格式顺序从内嵌 VBMeta 镜像解析出的描述符。</para></summary>
    /// <value>An empty list when parsing failed. <para>解析失败时为空列表。</para></value>
    public IReadOnlyList<AvbDescriptorInfo> Descriptors { get; init; } = [];

    /// <summary>Bilingual diagnostic describing the first failed check; <see langword="null"/> on full success.
    /// <para>描述首个失败检查的双语诊断；完全成功时为 <see langword="null"/>。</para></summary>
    /// <value>The failure text. <para>失败文本。</para></value>
    public string? ErrorMessage { get; init; }

    /// <summary>Whether the footer, signature and content checks all passed.
    /// <para>尾部、签名与内容检查是否全部通过。</para></summary>
    /// <value>True only when all three checks passed. <para>仅当三项检查全部通过时为 true。</para></value>
    public bool IsValid => IsSigned && SignatureIsValid && ContentIsValid && FecIsValid != false;
}

/// <summary>
/// Verifies a signed partition image end to end: trailing AVB footer, embedded VBMeta signature and content
/// against the descriptors carried by that VBMeta block.
/// <para>端到端验证已签名分区镜像：末尾 AVB 尾部、内嵌 VBMeta 签名，以及按该 VBMeta 块携带的描述符校验内容。</para>
/// </summary>
public static class AvbImageVerifier
{
    private const ulong MaximumVbmetaBytes = 16 * 1024 * 1024;

    /// <summary>Verifies a raw or Android Sparse partition image.
    /// <para>验证原始或 Android Sparse 分区镜像。</para></summary>
    /// <param name="imagePath">Image carrying a footer and an embedded VBMeta block.
    /// <para>携带尾部与内嵌 VBMeta 块的镜像。</para></param>
    /// <param name="trustedPublicKey">Optional public key that must verify the embedded signature; when absent the embedded key is used.
    /// <para>可选公钥，用于校验内嵌签名；省略时使用内嵌公钥。</para></param>
    /// <param name="cancellationToken">Cancels reads and content hashing. <para>取消读取与内容哈希。</para></param>
    /// <returns>Structurally complete result; rejected layouts are reported through <see cref="AvbImageVerifyResult.ErrorMessage"/> instead of thrown.
    /// <para>结构完整的结果；被拒绝的布局通过 <see cref="AvbImageVerifyResult.ErrorMessage"/> 报告而不抛出。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="imagePath"/> is empty or whitespace.
    /// <para><paramref name="imagePath"/> 为空或仅含空白。</para></exception>
    /// <exception cref="FileNotFoundException">The image does not exist. <para>镜像不存在。</para></exception>
    public static async Task<AvbImageVerifyResult> VerifyAsync(
        string imagePath,
        RSA? trustedPublicKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var image = await AvbImageSource.OpenAsync(imagePath, cancellationToken).ConfigureAwait(false);
            var stream = image.Stream;
            if (stream.Length < AvbFooter.Size)
            {
                return Fail("Image too small to contain an AVB footer.");
            }

            var footerBytes = new byte[AvbFooter.Size];
            stream.Position = stream.Length - AvbFooter.Size;
            await stream.ReadExactlyAsync(footerBytes, cancellationToken).ConfigureAwait(false);
            if (AvbFooter.TryLoad(footerBytes) is not { } footer)
            {
                return Fail("No supported AVB footer was found.");
            }

            var end = checked((ulong)(stream.Length - AvbFooter.Size));
            if (footer.OriginalImageSize > footer.VbmetaOffset || footer.VbmetaOffset > end
                || footer.VbmetaSize < AvbVBMetaImageHeader.Size || footer.VbmetaSize > end - footer.VbmetaOffset
                || footer.VbmetaSize > MaximumVbmetaBytes)
            {
                return Fail("The AVB footer references an invalid or oversized VBMeta region.", footer);
            }

            var vbmeta = new byte[(int)footer.VbmetaSize];
            stream.Position = checked((long)footer.VbmetaOffset);
            await stream.ReadExactlyAsync(vbmeta, cancellationToken).ConfigureAwait(false);
            var signature = AvbSignatureVerifier.Verify(vbmeta, trustedPublicKey);
            var signatureIsValid = signature.HashIsValid && signature.SignatureIsValid;
            var contentIsValid = true;
            var hasContentDescriptor = false;
            bool? fecIsValid = null;
            string? failure = signatureIsValid ? null : signature.ErrorMessage ?? "VBMeta hash or signature verification failed.";
            foreach (var descriptor in signature.Descriptors)
            {
                if (descriptor.Tag is not (1 or 2))
                {
                    continue;
                }

                hasContentDescriptor = true;
                var content = await AvbPartitionVerifier.VerifyWithFecAsync(stream, descriptor, cancellationToken)
                    .ConfigureAwait(false);
                if (!content.ContentIsValid)
                {
                    contentIsValid = false;
                    failure ??= content.ErrorMessage ?? "Partition content does not match the descriptor.";
                }

                if (content.FecIsValid == false)
                {
                    contentIsValid = false;
                    failure ??= content.ErrorMessage ?? "Partition FEC parity does not match.";
                }

                if (content.FecPresent)
                {
                    fecIsValid = fecIsValid != false && content.FecIsValid != false;
                }
            }

            if (!hasContentDescriptor)
            {
                contentIsValid = false;
                failure ??= "The VBMeta block carries no hash or hashtree descriptor for the partition content.";
            }

            AvbVBMetaImageHeader? header = null;
            try
            {
                header = AvbVBMetaImageHeader.FromBytes(vbmeta);
            }
            catch (InvalidDataException)
            {
            }

            return new AvbImageVerifyResult
            {
                IsSigned = true,
                SignatureIsValid = signatureIsValid,
                ContentIsValid = contentIsValid,
                FecIsValid = fecIsValid,
                Footer = footer,
                VBMetaHeader = header,
                Descriptors = signature.Descriptors,
                ErrorMessage = failure,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or CryptographicException
            or ArgumentException or OverflowException)
        {
            return Fail(ex.Message);
        }
    }

    private static AvbImageVerifyResult Fail(string message, AvbFooter? footer = null) => new()
    {
        IsSigned = footer is not null,
        Footer = footer,
        ErrorMessage = message,
    };
}