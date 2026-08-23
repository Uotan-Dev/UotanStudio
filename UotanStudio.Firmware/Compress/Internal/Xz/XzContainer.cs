namespace UotanStudio.Firmware.Compress.Internal.Xz;

/// <summary>
/// XZ 容器封装：将 LZMA2 原始流包装为完整 .xz 文件（流头 + 块 + 索引 + 页脚）。
/// <para>XZ container: wraps a raw LZMA2 stream into a complete .xz file (stream header + block + index + footer).</para>
/// <para>遵循 XZ 文件格式规范 v1.2.1（https://tukaani.org/xz/xz-file-format.txt）。</para>
/// <para>Follows the XZ file format specification v1.2.1.</para>
/// </summary>
internal static class XzContainer
{
    /// <summary>XZ 魔数（流头）。</summary>
    private static readonly byte[] HeaderMagic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    /// <summary>XZ 魔数（流尾）。</summary>
    private static readonly byte[] FooterMagic = [0x59, 0x5A];

    /// <summary>校验类型：CRC32。</summary>
    public const byte CheckTypeCrc32 = 0x01;

    /// <summary>校验类型：CRC64。</summary>
    public const byte CheckTypeCrc64 = 0x04;

    /// <summary>LZMA2 过滤器 ID。</summary>
    private const ulong FilterIdLzma2 = 0x21;

    /// <summary>
    /// 将 LZMA2 原始流封装为完整 .xz 文件。
    /// <para>Wraps a raw LZMA2 stream into a complete .xz file.</para>
    /// </summary>
    /// <param name="lzma2Data">LZMA2 原始流（含结束标记）。<para>Raw LZMA2 stream (including end marker).</para></param>
    /// <param name="originalData">原始未压缩数据，用于计算校验。<para>Original uncompressed data, used for the check.</para></param>
    /// <param name="dictionarySize">LZMA2 字典大小，写入块头过滤器属性。<para>LZMA2 dictionary size, written into the block header filter properties.</para></param>
    /// <param name="checkType">校验类型（0x01=CRC32，0x04=CRC64）。<para>Check type (0x01=CRC32, 0x04=CRC64).</para></param>
    public static byte[] Wrap(byte[] lzma2Data, byte[] originalData, uint dictionarySize, byte checkType = CheckTypeCrc32)
    {
        var check = checkType == CheckTypeCrc64
            ? WriteUInt64LE(Crc64.Compute(originalData))
            : WriteUInt32LE(Crc32.Compute(originalData));

        using var output = new MemoryStream();
        WriteContainer(output, lzma2Data, (ulong)originalData.Length, check, dictionarySize, checkType);
        return output.ToArray();
    }

    /// <summary>
    /// 将 LZMA2 原始流封装为完整 .xz 文件并直接写入流（供流式压缩使用：
    /// 校验与未压缩大小由调用方预先计算，无需整块缓冲原始数据）。
    /// <para>Wraps a raw LZMA2 stream into a complete .xz file and writes it directly to
    /// the stream (used by streaming compression: the check and uncompressed size are
    /// precomputed by the caller, so the original data never needs full buffering).</para>
    /// </summary>
    public static void WriteContainer(Stream output, byte[] lzma2Data, ulong uncompressedSize,
        byte[] check, uint dictionarySize, byte checkType = CheckTypeCrc32)
    {
        // Stream header / 流头
        output.Write(HeaderMagic, 0, HeaderMagic.Length);
        byte[] streamFlags = [0x00, checkType];
        output.Write(streamFlags, 0, streamFlags.Length);
        WriteCrc32(output, streamFlags);

        // Block header / 块头
        var dictIndex = DictionarySizeToIndex(dictionarySize);
        var blockHeader = BuildBlockHeader(lzma2Data.Length, uncompressedSize, dictIndex);

        // 块总大小（块头 + 压缩数据 + 块填充 + 校验）必须是 4 的倍数。
        var blockPadding = (4 - ((blockHeader.Length + lzma2Data.Length) % 4)) % 4;

        output.Write(blockHeader, 0, blockHeader.Length);
        output.Write(lzma2Data, 0, lzma2Data.Length);
        for (var i = 0; i < blockPadding; i++)
        {
            output.WriteByte(0);
        }

        // Check / 校验（调用方预计算，长度 4 或 8）。
        output.Write(check, 0, check.Length);

        // 未填充块大小 = 块头 + 压缩数据 + 校验（不含块填充）。
        var unpaddedSize = (ulong)(blockHeader.Length + lzma2Data.Length + check.Length);

        // Index / 索引
        var index = BuildIndex(unpaddedSize, uncompressedSize);

        // Stream footer / 流尾
        output.Write(index, 0, index.Length);

        // Backward Size = (索引总大小 / 4) - 1，包含索引自身的 CRC32。
        var backwardSize = (uint)((index.Length / 4) - 1);
        var footerPrefix = new byte[6];
        footerPrefix[0] = (byte)(backwardSize & 0xFF);
        footerPrefix[1] = (byte)((backwardSize >> 8) & 0xFF);
        footerPrefix[2] = (byte)((backwardSize >> 16) & 0xFF);
        footerPrefix[3] = (byte)((backwardSize >> 24) & 0xFF);
        footerPrefix[4] = streamFlags[0];
        footerPrefix[5] = streamFlags[1];

        // CRC32 覆盖 Backward Size + Stream Flags。
        WriteCrc32(output, footerPrefix);
        output.Write(footerPrefix, 0, footerPrefix.Length);
        output.Write(FooterMagic, 0, FooterMagic.Length);
    }

    /// <summary>
    /// 构建块头：块头大小字节 + 块标志 + 压缩/未压缩大小 + 过滤器 + 填充 + CRC32。
    /// <para>Builds the block header: size byte + flags + sizes + filter + padding + CRC32.</para>
    /// </summary>
    private static byte[] BuildBlockHeader(long compressedSize, ulong uncompressedSize, byte dictIndex)
    {
        using var body = new MemoryStream();

        // 块标志：1 个过滤器，压缩大小与未压缩大小均存在。
        body.WriteByte(0xC0);

        WriteVli(body, (ulong)compressedSize);
        WriteVli(body, uncompressedSize);

        // 过滤器 0：LZMA2，属性大小 1，属性字节 = 字典大小索引。
        WriteVli(body, FilterIdLzma2);
        WriteVli(body, 1);
        body.WriteByte(dictIndex);

        var bodyBytes = body.ToArray();

        // 总块头大小（含大小字节、填充、CRC32）必须是 4 的倍数，且 >= 8。
        // real_size = 1(大小字节) + body + padding + 4(CRC32)
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
    /// 构建索引：指示符 + 记录数 + 记录 + 填充 + CRC32。
    /// <para>Builds the index: indicator + record count + records + padding + CRC32.</para>
    /// </summary>
    private static byte[] BuildIndex(ulong unpaddedSize, ulong uncompressedSize)
    {
        using var index = new MemoryStream();

        index.WriteByte(0x00); // Index Indicator
        WriteVli(index, 1);    // Number of Records
        WriteVli(index, unpaddedSize);
        WriteVli(index, uncompressedSize);

        var body = index.ToArray();

        // 索引总大小（含指示符、记录、填充、CRC32）必须是 4 的倍数。
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
    /// 将字典大小转换为 XZ 规范的 0-40 索引（含 1 位尾数 + 5 位指数编码）。
    /// <para>Converts a dictionary size into the XZ spec 0-40 index (1-bit mantissa + 5-bit exponent encoding).</para>
    /// </summary>
    public static byte DictionarySizeToIndex(uint dictionarySize)
    {
        // dictionary_size = (2 | (bits & 1)) << (bits / 2 + 11)，bits ∈ [0, 40]。
        // 选择解码结果 >= 请求大小的最小 bits。
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

    /// <summary>写小端序 CRC32 到流中。</summary>
    private static void WriteCrc32(Stream stream, byte[] data)
    {
        var crc = Crc32.Compute(data);
        stream.WriteByte((byte)(crc & 0xFF));
        stream.WriteByte((byte)((crc >> 8) & 0xFF));
        stream.WriteByte((byte)((crc >> 16) & 0xFF));
        stream.WriteByte((byte)((crc >> 24) & 0xFF));
    }

    /// <summary>返回小端序 8 字节值。<para>Returns an 8-byte little-endian value.</para></summary>
    private static byte[] WriteUInt64LE(ulong value)
    {
        var buf = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            buf[i] = (byte)((value >> (8 * i)) & 0xFF);
        }

        return buf;
    }

    /// <summary>返回小端序 4 字节值。<para>Returns a 4-byte little-endian value.</para></summary>
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
    /// 写 XZ 变长整数（1-9 字节，7 位一组，高位为续位）。
    /// <para>Writes an XZ variable-length integer (1-9 bytes, 7 bits per group, high bit = continuation).</para>
    /// </summary>
    private static void WriteVli(Stream stream, ulong value)
    {
        // 规范 1.2：低 7 位在前，除最后一字节外高位为 1。
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