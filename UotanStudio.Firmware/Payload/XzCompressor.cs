using SharpCompress.Compressors.LZMA;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.Payload;

/// <summary>
/// Minimal XZ stream writer producing a single-block XZ stream with a LZMA2 filter
/// and no integrity check, compatible with the xz-embedded reader used by Android.
/// <para>生成单块 XZ 流（LZMA2 过滤器、无完整性校验）的最小 XZ 写入器，兼容 Android 使用的 xz-embedded 读取器。</para>
/// </summary>
internal class XzCompressor
{
    private static readonly byte[] StreamHeaderMagic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static readonly byte[] StreamFooterMagic = [0x59, 0x5A];
    private const byte CheckTypeNone = 0x00;
    private const byte Lzma2FilterId = 0x21;

    /// <summary>
    /// Compresses the given raw data into an XZ stream.
    /// <para>将给定原始数据压缩为 XZ 流。</para>
    /// </summary>
    /// <param name="data">The raw data to compress.
    /// <para>要压缩的原始数据。</para>
    /// </param>
    /// <returns>The compressed XZ stream bytes.
    /// <para>压缩后的 XZ 流字节。</para>
    /// </returns>
    public byte[] Compress(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            throw new ArgumentException("Data cannot be null or empty.", nameof(data));
        }

        using var ms = new MemoryStream();
        WriteStreamHeader(ms);
        var unpaddedBlockSize = WriteBlock(ms, data);
        var indexSize = WriteIndex(ms, unpaddedBlockSize, (ulong)data.Length);
        WriteStreamFooter(ms, indexSize);
        return ms.ToArray();
    }

    private static void WriteStreamHeader(Stream stream)
    {
        stream.Write(StreamHeaderMagic, 0, StreamHeaderMagic.Length);
        byte[] streamFlags = [0x00, CheckTypeNone];
        stream.Write(streamFlags, 0, streamFlags.Length);
        WriteCrc32Le(stream, streamFlags);
    }

    private static ulong WriteBlock(Stream stream, byte[] data)
    {
        var (lzma2PropsByte, lzma2Data) = Lzma2Compress(data);

        using var headerBuf = new MemoryStream();
        headerBuf.WriteByte(0x00);
        WriteVli(headerBuf, Lzma2FilterId);
        WriteVli(headerBuf, 1);
        headerBuf.WriteByte(lzma2PropsByte);
        var headerContent = headerBuf.ToArray();

        var headerSizeWithoutCrc = (1 + headerContent.Length + 3) / 4 * 4;
        var totalHeaderSize = headerSizeWithoutCrc + 4;
        var paddingNeeded = headerSizeWithoutCrc - 1 - headerContent.Length;
        var encodedSize = (byte)((totalHeaderSize / 4) - 1);

        stream.WriteByte(encodedSize);
        stream.Write(headerContent, 0, headerContent.Length);
        for (var i = 0; i < paddingNeeded; i++)
        {
            stream.WriteByte(0x00);
        }

        var crcStart = stream.Position - headerSizeWithoutCrc;
        var savedPos = stream.Position;
        stream.Seek(crcStart, SeekOrigin.Begin);
        var headerForCrc = new byte[headerSizeWithoutCrc];
        stream.ReadExactly(headerForCrc);
        stream.Seek(savedPos, SeekOrigin.Begin);
        WriteCrc32Le(stream, headerForCrc);

        stream.Write(lzma2Data, 0, lzma2Data.Length);

        var blockPadding = (4 - (int)(stream.Position % 4)) % 4;
        for (var i = 0; i < blockPadding; i++)
        {
            stream.WriteByte(0x00);
        }

        var unpaddedSize = (ulong)(totalHeaderSize + lzma2Data.Length);
        return unpaddedSize;
    }

    private static int WriteIndex(Stream stream, ulong unpaddedBlockSize, ulong uncompressedSize)
    {
        var indexStart = stream.Position;

        stream.WriteByte(0x00);
        WriteVli(stream, 1);
        WriteVli(stream, unpaddedBlockSize);
        WriteVli(stream, uncompressedSize);

        var indexPadding = (4 - (int)(stream.Position % 4)) % 4;
        for (var i = 0; i < indexPadding; i++)
        {
            stream.WriteByte(0x00);
        }

        var indexEnd = stream.Position;
        var indexSizeBeforeCrc = (int)(indexEnd - indexStart);
        stream.Seek(indexStart, SeekOrigin.Begin);
        var indexData = new byte[indexSizeBeforeCrc];
        stream.ReadExactly(indexData);
        stream.Seek(indexEnd, SeekOrigin.Begin);
        WriteCrc32Le(stream, indexData);

        return indexSizeBeforeCrc + 4;
    }

    private static void WriteStreamFooter(Stream stream, int indexSize)
    {
        var backwardSize = (uint)(indexSize / 4) - 1;
        var backwardSizeBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(backwardSizeBytes, backwardSize);
        byte[] streamFlags = [0x00, CheckTypeNone];

        using var footerData = new MemoryStream();
        footerData.Write(backwardSizeBytes, 0, 4);
        footerData.Write(streamFlags, 0, 2);
        var footerContent = footerData.ToArray();
        WriteCrc32Le(stream, footerContent);
        stream.Write(footerContent, 0, footerContent.Length);
        stream.Write(StreamFooterMagic, 0, StreamFooterMagic.Length);
    }

    private static (byte Lzma2PropsByte, byte[] Lzma2Data) Lzma2Compress(byte[] data)
    {
        var encoderProps = new LzmaEncoderProperties(eos: false);
        using var inputStream = new MemoryStream(data);
        using var outputStream = new MemoryStream();
        byte[] lzmaProps;
        using (var lzmaStream = LzmaStream.Create(encoderProps, false, outputStream))
        {
            inputStream.CopyTo(lzmaStream);
            lzmaProps = lzmaStream.Properties;
        }

        var compressedData = outputStream.ToArray();
        var lzma1PropsByte = lzmaProps[0];
        var dictSize = BitConverter.ToUInt32(lzmaProps, 1);
        var lzma2PropsByte = EncodeLzma2DictionarySize(dictSize);

        compressedData = TrimToConsumedSize(lzmaProps, compressedData, data.Length);

        using var lzma2Stream = new MemoryStream();
        var uncompressedSize = (ulong)data.Length;
        var compressedSize = (ulong)compressedData.Length;
        var uncompSizeMinus1 = uncompressedSize - 1;
        var compSizeMinus1 = compressedSize - 1;

        // A single LZMA2 compressed chunk (control 0xE0..0xFF) is only valid while
        // the uncompressed size fits in 21 bits (max 2 MiB) and the compressed size
        // fits in 16 bits (max 64 KiB). The chunk header is: control byte carrying the
        // top 5 bits of (uncompressedSize - 1), 2 bytes uncompressedSize - 1, 2 bytes
        // compressedSize - 1, the LZMA properties byte, then the raw LZMA1 data.
        // Whenever the input cannot be stored in such a chunk (e.g. large or
        // incompressible partitions), fall back to valid uncompressed LZMA2 chunks
        // (0x01 with dictionary reset, then 0x02), each carrying up to 64 KiB.
        // <para>单个 LZMA2 压缩块（控制字节 0xE0..0xFF）仅在未压缩大小可放入 21 位（最大 2 MiB）
        // 且压缩大小可放入 16 位（最大 64 KiB）时才有效。块头为：携带 (uncompressedSize - 1) 高 5 位的
        // 控制字节、2 字节 uncompressedSize - 1、2 字节 compressedSize - 1、LZMA 属性字节，随后是原始
        // LZMA1 数据。当输入无法放入这样的块时（例如较大或不可压缩的分区），回退为有效的未压缩 LZMA2
        // 块（0x01 字典重置，随后 0x02），每块最多携带 64 KiB。</para>
        var singleChunkFits = compressedSize >= 1 && compSizeMinus1 <= 0xFFFF && uncompSizeMinus1 <= 0x1FFFFF;
        if (singleChunkFits)
        {
            var control = (byte)(0xE0 | ((uncompSizeMinus1 >> 16) & 0x1F));
            lzma2Stream.WriteByte(control);
            lzma2Stream.WriteByte((byte)((uncompSizeMinus1 >> 8) & 0xFF));
            lzma2Stream.WriteByte((byte)(uncompSizeMinus1 & 0xFF));
            lzma2Stream.WriteByte((byte)((compSizeMinus1 >> 8) & 0xFF));
            lzma2Stream.WriteByte((byte)(compSizeMinus1 & 0xFF));
            lzma2Stream.WriteByte(lzma1PropsByte);
            lzma2Stream.Write(compressedData, 0, compressedData.Length);
        }
        else
        {
            var offset = 0;
            var first = true;
            while (offset < data.Length)
            {
                var chunkLength = Math.Min(65536, data.Length - offset);
                lzma2Stream.WriteByte(first ? (byte)0x01 : (byte)0x02);
                var chunkSizeMinus1 = chunkLength - 1;
                lzma2Stream.WriteByte((byte)((chunkSizeMinus1 >> 8) & 0xFF));
                lzma2Stream.WriteByte((byte)(chunkSizeMinus1 & 0xFF));
                lzma2Stream.Write(data, offset, chunkLength);
                offset += chunkLength;
                first = false;
            }
        }

        lzma2Stream.WriteByte(0x00);

        return (lzma2PropsByte, lzma2Stream.ToArray());
    }

    private static byte[] TrimToConsumedSize(byte[] lzmaProps, byte[] compressedData, int uncompressedSize)
    {
        try
        {
            var decoder = new Decoder();
            decoder.SetDecoderProperties(lzmaProps);
            using var inputMs = new MemoryStream(compressedData);
            using var outputMs = new MemoryStream();
            decoder.Code(inputMs, outputMs, compressedData.Length, uncompressedSize, null);
            var consumed = (int)inputMs.Position;
            if (consumed < compressedData.Length)
            {
                var trimmed = new byte[consumed];
                Array.Copy(compressedData, 0, trimmed, 0, consumed);
                return trimmed;
            }
        }
        catch
        {
        }

        return compressedData;
    }

    private static byte EncodeLzma2DictionarySize(uint dictSize)
    {
        if (dictSize <= 2048)
        {
            return 0;
        }

        for (byte p = 0; p < 40; p++)
        {
            var shift = (p >> 1) + 11;
            if (shift >= 31)
            {
                return p;
            }

            var size = (uint)(2 | (p & 1)) << shift;
            if (size >= dictSize)
            {
                return p;
            }
        }

        return 40;
    }

    private static void WriteVli(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteCrc32Le(Stream stream, byte[] data)
    {
        var crc = 0xFFFFFFFF;
        for (var i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (var j = 0; j < 8; j++)
            {
                crc = (crc >> 1) ^ (0xEDB88320U & (uint)-(int)(crc & 1));
            }
        }

        crc = ~crc;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }
}