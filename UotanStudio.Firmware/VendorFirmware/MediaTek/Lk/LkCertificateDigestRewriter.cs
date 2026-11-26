namespace UotanStudio.Firmware.VendorFirmware.MediaTek.Lk;

/// <summary>
/// Rewrites the two SHA-256 digests embedded in a MediaTek <c>cert2</c> DER structure: the digest of the
/// partition header (index 14 of the inner sequence) and the digest of the padded partition data (index 10).
/// The signature itself is left untouched, so the certificate is no longer self-consistent — this is the
/// documented certificate-bypass behaviour and must stay opt-in.
/// <para>重写 MediaTek <c>cert2</c> DER 结构中嵌入的两处 SHA-256 摘要：分区头部摘要（内层序列第 14 项）与
/// 填充后分区数据摘要（第 10 项）。签名本身不改写，因此证书不再自洽——这是有记录的证书旁路行为，必须保持显式开启。</para>
/// </summary>
public static class LkCertificateDigestRewriter
{
    private const int ImageHashIndex = 10;
    private const int HeaderHashIndex = 14;
    private const int DigestLength = 32;
    private const byte BitStringTag = 0x03;

    /// <summary>
    /// Replaces the embedded digests in place while preserving the total length.
    /// <para>在保持总长度不变的前提下替换嵌入摘要。</para>
    /// </summary>
    /// <param name="der">Raw certificate bytes.<para>原始证书字节。</para></param>
    /// <param name="headerHash">New header digest (32 bytes).<para>新的头部摘要（32 字节）。</para></param>
    /// <param name="imageHash">New padded data digest (32 bytes).<para>新的填充数据摘要（32 字节）。</para></param>
    /// <returns>A new buffer with both digests replaced.<para>两处摘要均已替换的新缓冲区。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.<para>某个参数为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">A digest is not 32 bytes.<para>某个摘要不是 32 字节。</para></exception>
    /// <exception cref="InvalidDataException">The DER layout is not a supported <c>cert2</c> structure.<para>DER 布局不是受支持的 <c>cert2</c> 结构。</para></exception>
    public static byte[] ReplaceDigests(ReadOnlySpan<byte> der, byte[] headerHash, byte[] imageHash)
    {
        ArgumentNullException.ThrowIfNull(headerHash);
        ArgumentNullException.ThrowIfNull(imageHash);
        if (headerHash.Length != DigestLength || imageHash.Length != DigestLength)
        {
            throw new ArgumentException("Both digests must be 32 bytes (SHA-256).");
        }

        var result = der.ToArray();
        var outer = ReadElement(result, 0, result.Length);
        if (outer.Tag != 0x30)
        {
            throw new InvalidDataException("The certificate does not start with a DER sequence.");
        }

        var inner = ReadElement(result, outer.ContentStart, outer.ContentLength);
        if (inner.Tag != 0x30)
        {
            throw new InvalidDataException("The certificate's first member is not a sequence.");
        }

        var position = inner.ContentStart;
        var end = inner.ContentStart + inner.ContentLength;
        var index = 0;
        var headerWritten = false;
        var imageWritten = false;
        while (position < end)
        {
            var element = ReadElement(result, position, end);
            if (index is ImageHashIndex or HeaderHashIndex)
            {
                if (element.Tag != BitStringTag)
                {
                    throw new InvalidDataException("The certificate digest slot is not a BIT STRING.");
                }

                var span = result.AsSpan(element.ContentStart, element.ContentLength);
                if (span.Length != DigestLength + 1 || span[0] != 0)
                {
                    throw new InvalidDataException("The certificate digest slot does not carry an aligned 32-byte digest.");
                }

                var digest = index == HeaderHashIndex ? headerHash : imageHash;
                digest.CopyTo(span[1..]);
                if (index == HeaderHashIndex)
                {
                    headerWritten = true;
                }
                else
                {
                    imageWritten = true;
                }
            }

            position = element.ContentStart + element.ContentLength;
            index++;
        }

        if (!headerWritten || !imageWritten)
        {
            throw new InvalidDataException("The certificate does not expose both expected digest slots.");
        }

        return result;
    }

    private static DerElement ReadElement(ReadOnlySpan<byte> source, int offset, int limit)
    {
        if (offset < 0 || offset + 2 > limit)
        {
            throw new InvalidDataException("A DER element header is truncated.");
        }

        var tag = source[offset];
        var first = source[offset + 1];
        int length;
        var contentStart = offset + 2;
        if (first < 0x80)
        {
            length = first;
        }
        else
        {
            var count = first & 0x7F;
            if (count is 0 or > 4)
            {
                throw new InvalidDataException("A DER length uses an unsupported form.");
            }

            if (offset + 2 + count > limit)
            {
                throw new InvalidDataException("A DER length is truncated.");
            }

            length = 0;
            for (var index = 0; index < count; index++)
            {
                length = (length << 8) | source[offset + 2 + index];
            }

            contentStart += count;
        }

        if (length < 0 || contentStart > limit || length > limit - contentStart)
        {
            throw new InvalidDataException("A DER element overruns its container.");
        }

        return new DerElement(tag, contentStart, length);
    }

    private readonly record struct DerElement(byte Tag, int ContentStart, int ContentLength);
}