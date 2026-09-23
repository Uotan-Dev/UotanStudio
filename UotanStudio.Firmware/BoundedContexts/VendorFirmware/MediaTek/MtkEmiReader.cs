using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>Explicit, bounded EMI extraction from a preloader image or a standalone parameter block.</summary>
public static class MtkEmiReader
{
    private const int MaximumBlockLength = 1024 * 1024;

    /// <summary>Extracts the unsigned trailing EMI block from an explicitly located MMM preloader header.</summary>
    /// <param name="source">A caller-owned readable, seekable stream whose position is restored.</param>
    /// <param name="headerOffset">The absolute MMM header offset; no implicit scanning is performed.</param>
    /// <param name="layout">The payload convention required by the selected agent.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A bounded copy of the selected parameter bytes with source provenance.</returns>
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

    /// <summary>Reads an explicitly bounded standalone MTK_BLOADER_INFO block.</summary>
    /// <param name="source">A caller-owned readable, seekable stream whose position is restored.</param>
    /// <param name="offset">The absolute block offset.</param>
    /// <param name="length">The block byte count, limited to 1 MiB.</param>
    /// <param name="layout">The selected agent's payload convention.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A bounded copy of the selected bytes.</returns>
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