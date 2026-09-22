namespace UotanStudio.Firmware.BoundedContexts.Compression.Internal.Xz;

internal static class XzContainer
{
    private static readonly byte[] HeaderMagic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    private static readonly byte[] FooterMagic = [0x59, 0x5A];

    public const byte CheckTypeCrc32 = 0x01;

    public const byte CheckTypeCrc64 = 0x04;

    private const ulong FilterIdLzma2 = 0x21;

    /// <summary>
    /// Wraps a raw LZMA2 stream into a complete .xz file.
    /// <para>将 LZMA2 原始流封装为完整 .xz 文件。</para>
    /// </summary>
    public static byte[] Wrap(byte[] lzma2Data, byte[] originalData, uint dictionarySize, byte checkType = CheckTypeCrc32)
    {
        var check = checkType == CheckTypeCrc64
            ? WriteUInt64LE(XzCrc64.HashToUInt64(originalData))
            : WriteUInt32LE(ComputeCrc32(originalData));

        using var output = new MemoryStream();
        WriteContainer(output, lzma2Data, (ulong)originalData.Length, check, dictionarySize, checkType);
        return output.ToArray();
    }

    /// <summary>
    /// Wraps a raw LZMA2 stream into a complete .xz file and writes it directly to the stream (used by streaming compression: the check and uncompressed size are precomputed by the caller, so the original data never needs full buffering).
    /// <para>将 LZMA2 原始流封装为完整 .xz 文件并直接写入流（供流式压缩使用： 校验与未压缩大小由调用方预先计算，无需整块缓冲原始数据）。</para>
    /// </summary>
    public static void WriteContainer(Stream output, byte[] lzma2Data, ulong uncompressedSize,
        byte[] check, uint dictionarySize, byte checkType = CheckTypeCrc32)
    {
        using var source = new MemoryStream(lzma2Data, writable: false);
        WriteContainer(output, source, lzma2Data.LongLength, uncompressedSize, check, dictionarySize, checkType);
    }

    /// <summary>
    /// Writes a sized LZMA2 source without materializing its compressed payload.
    /// <para>写入已知长度的 LZMA2 源流，不将压缩载荷整体物化。</para>
    /// </summary>
    internal static void WriteContainer(Stream output, Stream lzma2Data, long compressedSize, ulong uncompressedSize,
        byte[] check, uint dictionarySize, byte checkType = CheckTypeCrc32)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(compressedSize);

        output.Write(HeaderMagic, 0, HeaderMagic.Length);
        byte[] streamFlags = [0x00, checkType];
        output.Write(streamFlags, 0, streamFlags.Length);
        WriteCrc32(output, streamFlags);


        var dictIndex = DictionarySizeToIndex(dictionarySize);
        var blockHeader = BuildBlockHeader(compressedSize, uncompressedSize, dictIndex);


        var blockPadding = (4 - ((blockHeader.Length + compressedSize % 4) % 4)) % 4;

        output.Write(blockHeader, 0, blockHeader.Length);
        SharedKernel.Streams.StreamCopy.CopyExactly(lzma2Data, output, compressedSize);
        for (var i = 0; i < blockPadding; i++)
        {
            output.WriteByte(0);
        }


        output.Write(check, 0, check.Length);


        var unpaddedSize = checked((ulong)compressedSize + (ulong)blockHeader.Length + (ulong)check.Length);


        var index = BuildIndex(unpaddedSize, uncompressedSize);


        output.Write(index, 0, index.Length);


        var backwardSize = (uint)((index.Length / 4) - 1);
        var footerPrefix = new byte[6];
        footerPrefix[0] = (byte)(backwardSize & 0xFF);
        footerPrefix[1] = (byte)((backwardSize >> 8) & 0xFF);
        footerPrefix[2] = (byte)((backwardSize >> 16) & 0xFF);
        footerPrefix[3] = (byte)((backwardSize >> 24) & 0xFF);
        footerPrefix[4] = streamFlags[0];
        footerPrefix[5] = streamFlags[1];


        WriteCrc32(output, footerPrefix);
        output.Write(footerPrefix, 0, footerPrefix.Length);
        output.Write(FooterMagic, 0, FooterMagic.Length);
    }

    /// <summary>
    /// Builds the block header: size byte + flags + sizes + filter + padding + CRC32.
    /// <para>构建块头：块头大小字节 + 块标志 + 压缩/未压缩大小 + 过滤器 + 填充 + CRC32。</para>
    /// </summary>
    private static byte[] BuildBlockHeader(long compressedSize, ulong uncompressedSize, byte dictIndex)
    {
        using var body = new MemoryStream();


        body.WriteByte(0xC0);

        WriteVli(body, (ulong)compressedSize);
        WriteVli(body, uncompressedSize);


        WriteVli(body, FilterIdLzma2);
        WriteVli(body, 1);
        body.WriteByte(dictIndex);

        var bodyBytes = body.ToArray();


        var realSize = 1 + bodyBytes.Length + 4;
        var padding = (4 - (realSize % 4)) % 4;
        realSize += padding;

        var encodedHeaderSize = (byte)((realSize / 4) - 1);

        using var header = new MemoryStream();
        header.WriteByte(encodedHeaderSize);
        header.Write(bodyBytes, 0, bodyBytes.Length);
        for (var i = 0; i < padding; i++)
        {
            header.WriteByte(0);
        }

        var headerBytes = header.ToArray();
        WriteCrc32(header, headerBytes);

        return header.ToArray();
    }

    /// <summary>
    /// Builds the index: indicator + record count + records + padding + CRC32.
    /// <para>构建索引：指示符 + 记录数 + 记录 + 填充 + CRC32。</para>
    /// </summary>
    private static byte[] BuildIndex(ulong unpaddedSize, ulong uncompressedSize)
    {
        using var index = new MemoryStream();

        index.WriteByte(0x00);
        WriteVli(index, 1);
        WriteVli(index, unpaddedSize);
        WriteVli(index, uncompressedSize);

        var body = index.ToArray();


        var padding = (4 - ((body.Length + 4) % 4)) % 4;
        for (var i = 0; i < padding; i++)
        {
            index.WriteByte(0);
        }

        var indexBytes = index.ToArray();
        WriteCrc32(index, indexBytes);

        return index.ToArray();
    }

    /// <summary>
    /// Converts a dictionary size into the XZ spec 0-40 index (1-bit mantissa + 5-bit exponent encoding).
    /// <para>将字典大小转换为 XZ 规范的 0-40 索引（含 1 位尾数 + 5 位指数编码）。</para>
    /// </summary>
    public static byte DictionarySizeToIndex(uint dictionarySize)
    {


        for (var bits = 0; bits < 40; bits++)
        {
            var decoded = (ulong)(2 | (bits & 1)) << ((bits / 2) + 11);
            if (decoded >= dictionarySize)
            {
                return (byte)bits;
            }
        }
        return 40;
    }

    /// <summary>
    /// Writes a little-endian CRC32 checksum to the stream.
    /// <para>向流写入小端序 CRC32 校验和。</para>
    /// </summary>
    private static void WriteCrc32(Stream stream, byte[] data)
    {
        var crc = ComputeCrc32(data);
        stream.WriteByte((byte)(crc & 0xFF));
        stream.WriteByte((byte)((crc >> 8) & 0xFF));
        stream.WriteByte((byte)((crc >> 16) & 0xFF));
        stream.WriteByte((byte)((crc >> 24) & 0xFF));
    }

    /// <summary>
    /// Computes the standard CRC-32 (initial value 0xFFFFFFFF, final XOR 0xFFFFFFFF).
    /// <para>计算标准 CRC-32（初始值 0xFFFFFFFF，最终异或 0xFFFFFFFF）。</para>
    /// </summary>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = new System.IO.Hashing.Crc32();
        crc.Append(data);
        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Returns an 8-byte little-endian value.
    /// <para>返回小端序 8 字节值。</para>
    /// </summary>
    private static byte[] WriteUInt64LE(ulong value)
    {
        var buf = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            buf[i] = (byte)((value >> (8 * i)) & 0xFF);
        }

        return buf;
    }

    /// <summary>
    /// Returns a 4-byte little-endian value.
    /// <para>返回小端序 4 字节值。</para>
    /// </summary>
    private static byte[] WriteUInt32LE(uint value)
    {
        var buf = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            buf[i] = (byte)((value >> (8 * i)) & 0xFF);
        }

        return buf;
    }

    /// <summary>
    /// Writes an XZ variable-length integer (1-9 bytes, 7 bits per group, high bit = continuation).
    /// <para>写 XZ 变长整数（1-9 字节，7 位一组，高位为续位）。</para>
    /// </summary>
    private static void WriteVli(Stream stream, ulong value)
    {

        Span<byte> buf = stackalloc byte[9];
        var i = 0;
        while (value >= 0x80)
        {
            buf[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buf[i++] = (byte)value;
        for (var j = 0; j < i; j++)
        {
            stream.WriteByte(buf[j]);
        }
    }
}