using System.Buffers.Binary;

namespace UotanStudio.Firmware.VendorFirmware.MediaTek;

/// <summary>Explicit, bounded EMI extraction from a preloader image or a standalone parameter block.
/// <para>从 preloader 镜像或独立参数块中显式、有界地提取 EMI 数据。</para></summary>
public static class MtkEmiReader
{
    private const int MaximumBlockLength = 1024 * 1024;

    /// <summary>Extracts the unsigned trailing EMI block from an explicitly located MMM preloader header.
    /// <para>从显式定位的 MMM preloader 头部中提取未签名的尾部 EMI 块。</para></summary>
    /// <param name="source">A caller-owned readable, seekable stream whose position is restored.
    /// <para>调用方持有的可读、可寻址流，调用结束后位置被还原。</para></param>
    /// <param name="headerOffset">The absolute MMM header offset; no implicit scanning is performed.
    /// <para>MMM 头部的绝对偏移；不会执行任何隐式扫描。</para></param>
    /// <param name="layout">The payload convention required by the selected agent.
    /// <para>所选 agent 要求的负载约定。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>本次操作的取消令牌。</para></param>
    /// <returns>A bounded copy of the selected parameter bytes with source provenance.
    /// <para>所选参数字节的有界副本，带有来源信息。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> cannot be read or seeked. <para><paramref name="source"/> 不可读或不可寻址。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="layout"/> is not a defined enum value. <para><paramref name="layout"/> 不是已定义的枚举值。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested while reading. <para>读取期间收到了取消请求。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A selected range exceeds the source, the offset holds no supported MMM header, the unsigned
    /// range is invalid, or the EMI length is empty, excessive or overlapping.
    /// <para>所选范围超出源数据、该偏移不含受支持的 MMM 头部、未签名范围无效，或 EMI 长度为空、过大或与头部重叠。</para>
    /// </exception>
    public static MtkEmiParameters ReadPreloader(Stream source, long headerOffset, MtkEmiPayloadLayout layout,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(source, layout);
        var original = source.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckRange(source, headerOffset, 56);
            source.Position = headerOffset;
            Span<byte> header = stackalloc byte[56];
            source.ReadExactly(header);
            if (!header[..8].SequenceEqual(new byte[] { 0x4D, 0x4D, 0x4D, 1, 0x38, 0, 0, 0 }))
            {
                throw new InvalidDataException("The selected offset does not contain a supported MMM preloader header.");
            }
            var imageLength = BinaryPrimitives.ReadUInt32LittleEndian(header[0x20..]);
            var signatureLength = BinaryPrimitives.ReadUInt32LittleEndian(header[0x2C..]);
            CheckRange(source, headerOffset, imageLength);
            if (signatureLength > imageLength || imageLength - signatureLength < 60)
            {
                throw new InvalidDataException("The preloader's unsigned range is invalid.");
            }
            var unsignedEnd = checked(headerOffset + imageLength - signatureLength);
            var padding = 0;
            var blockLength = ReadLength(source, unsignedEnd - 4, cancellationToken);
            if (blockLength == 0)
            {
                padding = 0x800;
                unsignedEnd -= padding;
                if (unsignedEnd < headerOffset + 60)
                {
                    throw new InvalidDataException("The padded preloader trailer exceeds the unsigned range.");
                }
                blockLength = ReadLength(source, unsignedEnd - 4, cancellationToken);
            }
            if (blockLength == 0 || blockLength > MaximumBlockLength
                || blockLength > unsignedEnd - headerOffset - 60)
            {
                throw new InvalidDataException("The preloader EMI length is empty, excessive or overlaps its header.");
            }
            return ReadBlockCore(source, unsignedEnd - 4 - blockLength, (int)blockLength, layout, padding, cancellationToken);
        }
        finally
        {
            source.Position = original;
        }
    }

    /// <summary>Reads an explicitly bounded standalone MTK_BLOADER_INFO block.
    /// <para>读取显式限定范围的独立 MTK_BLOADER_INFO 块。</para></summary>
    /// <param name="source">A caller-owned readable, seekable stream whose position is restored.
    /// <para>调用方持有的可读、可寻址流，调用结束后位置被还原。</para></param>
    /// <param name="offset">The absolute block offset.
    /// <para>块在整个源中的绝对偏移。</para></param>
    /// <param name="length">The block byte count, limited to 1 MiB.
    /// <para>块字节数，上限为 1 MiB。</para></param>
    /// <param name="layout">The selected agent's payload convention.
    /// <para>所选 agent 的负载约定。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>本次操作的取消令牌。</para></param>
    /// <returns>A bounded copy of the selected bytes.
    /// <para>所选字节的有界副本。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> cannot be read or seeked. <para><paramref name="source"/> 不可读或不可寻址。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="layout"/> is not a defined enum value. <para><paramref name="layout"/> 不是已定义的枚举值。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested while reading. <para>读取期间收到了取消请求。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The range exceeds the source, the length is outside 1 byte to 1 MiB, the block lacks a
    /// leading bootloader-info header with a numeric version, or the MTK_BIN payload is missing,
    /// truncated or ambiguous.
    /// <para>范围超出源数据、长度不在 1 字节到 1 MiB 之间、块开头缺少带数字版本的 bootloader-info 头部，或 MTK_BIN 负载缺失、被截断、存在歧义。</para>
    /// </exception>
    public static MtkEmiParameters ReadBlock(Stream source, long offset, int length, MtkEmiPayloadLayout layout,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(source, layout);
        var original = source.Position;
        try
        {
            return ReadBlockCore(source, offset, length, layout, 0, cancellationToken);
        }
        finally
        {
            source.Position = original;
        }
    }

    private static MtkEmiParameters ReadBlockCore(Stream source, long offset, int length,
        MtkEmiPayloadLayout layout, int padding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (length <= 0 || length > MaximumBlockLength)
        {
            throw new InvalidDataException("The EMI block length exceeds supported bounds.");
        }
        CheckRange(source, offset, length);
        source.Position = offset;
        var bytes = new byte[length];
        source.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        var marker = "MTK_BLOADER_INFO_v"u8;
        if (bytes.Length < marker.Length + 2 || !bytes.AsSpan().StartsWith(marker))
        {
            throw new InvalidDataException("The selected EMI block has no leading bootloader-info header.");
        }
        var first = bytes[marker.Length];
        var second = bytes[marker.Length + 1];
        if (first is < (byte)'0' or > (byte)'9'
            || (second != 0 && second is < (byte)'0' or > (byte)'9'))
        {
            throw new InvalidDataException("The EMI version field is not numeric.");
        }
        var version = second == 0 ? first - '0' : (first - '0') * 10 + second - '0';
        if (layout == MtkEmiPayloadLayout.BootloaderInfo)
        {
            return new MtkEmiParameters(version, offset, padding, layout, bytes);
        }
        var binaryMarker = "MTK_BIN"u8;
        var start = bytes.AsSpan(marker.Length + 2).IndexOf(binaryMarker);
        if (start < 0)
        {
            throw new InvalidDataException("The EMI block has no MTK_BIN payload header.");
        }
        start += marker.Length + 2;
        if (start + 12 >= bytes.Length
            || bytes.AsSpan(start + binaryMarker.Length).IndexOf(binaryMarker) >= 0)
        {
            throw new InvalidDataException("The MTK_BIN payload is empty, truncated or ambiguous.");
        }
        return new MtkEmiParameters(version, checked(offset + start + 12), padding, layout, bytes[(start + 12)..]);
    }

    private static uint ReadLength(Stream source, long offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        source.Position = offset;
        Span<byte> bytes = stackalloc byte[4];
        source.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static void CheckRange(Stream source, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > source.Length || length > source.Length - offset)
        {
            throw new InvalidDataException("The selected EMI/preloader range exceeds the source.");
        }
    }

    private static void ValidateSource(Stream source, MtkEmiPayloadLayout layout)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("EMI extraction requires a readable, seekable source.", nameof(source));
        }
        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }
}