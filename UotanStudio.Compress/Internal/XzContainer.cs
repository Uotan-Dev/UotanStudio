using System.Buffers.Binary;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.XzCrc64;

namespace UotanStudio.Compress.Internal;

internal static class XzContainer
{
    private static readonly byte[] HeaderMagic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    private static readonly byte[] FooterMagic = [0x59, 0x5A];

    public const byte CheckTypeCrc32 = 0x01;

    public const byte CheckTypeCrc64 = 0x04;

    private const ulong FilterIdLzma2 = 0x21;

    /// <summary>Wraps a raw LZMA2 stream into a complete .xz file.
    /// <para>将 LZMA2 原始流封装为完整 .xz 文件。</para></summary>
    /// <param name="lzma2Data">Raw LZMA2 payload without any .xz framing; it is copied into the block body and padded to a four-byte boundary.
    /// <para>不含 .xz 封装的原始 LZMA2 载荷；写入块体并补齐到四字节边界。</para></param>
    /// <param name="originalData">Uncompressed bytes the block decodes back to; its length is recorded as the uncompressed size and its content is the input of the check field.
    /// <para>该块解压后得到的未压缩字节；其长度记录为未压缩大小，其内容作为校验字段的计算输入。</para></param>
    /// <param name="dictionarySize">Dictionary size in bytes advertised in the LZMA2 property byte, mapped through <see cref="DictionarySizeToIndex"/> to the smallest representable size that is not below it.
    /// <para>写入 LZMA2 属性字节的字典大小（字节），经 <see cref="DictionarySizeToIndex"/> 映射为不小于它的最小可表示值。</para></param>
    /// <param name="checkType">Check identifier written verbatim into the stream flags; <see cref="CheckTypeCrc64"/> selects a CRC-64 check and every other value, including the default <see cref="CheckTypeCrc32"/>, selects CRC-32, so a value outside those two constants describes a check the container does not carry.
    /// <para>原样写入流标志的校验标识；<see cref="CheckTypeCrc64"/> 选用 CRC-64， 其余取值（含默认的 <see cref="CheckTypeCrc32"/>）均选用 CRC-32，因此这两个常量之外的值会声明容器并未携带的校验类型。</para></param>
    /// <returns>The complete single-block .xz file bytes: header magic, stream flags, block header, LZMA2 payload with padding, check field, index, footer and footer magic.
    /// <para>完整的单块 .xz 文件字节：头部魔数、流标志、块头、LZMA2 载荷及其填充、校验字段、索引、尾部与尾部魔数。</para></returns>
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
    /// Wraps a raw LZMA2 stream into a complete .xz file and writes it to the stream.
    /// <para>把 LZMA2 原始流封装为完整 .xz 文件并写入流。</para>
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

        var streamFlags = WriteStreamHeader(output, checkType);
        var blockHeader = BuildBlockHeader(compressedSize, uncompressedSize, DictionarySizeToIndex(dictionarySize));

        output.Write(blockHeader, 0, blockHeader.Length);
        ExactCopy.Exactly(lzma2Data, output, compressedSize);
        WriteContainerTail(output, compressedSize, blockHeader.Length, uncompressedSize, check, streamFlags);
    }

    /// <summary>
    /// Writes the 12-byte stream header and returns the two stream-flag bytes it advertises.
    /// <para>写出 12 字节流头并返回其宣告的两个流标志字节。</para>
    /// </summary>
    /// <param name="output">Writable destination receiving the header; it is not flushed or closed.
    /// <para>接收流头的可写目标；不会刷新或关闭。</para></param>
    /// <param name="checkType">Check identifier stored in the stream flags and later repeated by the footer.
    /// <para>写入流标志、随后由尾部重复的校验类型标识。</para></param>
    /// <returns>The two stream-flag bytes, which the footer has to repeat verbatim.
    /// <para>两个流标志字节，尾部必须原样重复。</para></returns>
    internal static byte[] WriteStreamHeader(Stream output, byte checkType)
    {
        output.Write(HeaderMagic, 0, HeaderMagic.Length);
        byte[] streamFlags = [0x00, checkType];
        output.Write(streamFlags, 0, streamFlags.Length);
        WriteCrc32(output, streamFlags);
        return streamFlags;
    }

    /// <summary>
    /// Writes the single-block tail: block padding, check, index, footer and footer magic.
    /// <para>写出单块尾部：块填充、校验、索引、尾部与尾部魔数。</para>
    /// </summary>
    /// <param name="output">Writable destination positioned immediately after the LZMA2 payload; it is not flushed or closed.
    /// <para>定位在 LZMA2 载荷之后的可写目标；不会刷新或关闭。</para></param>
    /// <param name="compressedSize">Byte length of the LZMA2 payload already written.
    /// <para>已写出的 LZMA2 载荷字节长度。</para></param>
    /// <param name="blockHeaderLength">Byte length of the block header already written.
    /// <para>已写出的块头字节长度。</para></param>
    /// <param name="uncompressedSize">Decoded byte count recorded in the block header and index.
    /// <para>记录在块头与索引中的解码字节数。</para></param>
    /// <param name="check">Check bytes computed over the decoded data; four bytes for CRC-32 and eight for CRC-64.
    /// <para>对解码数据计算的校验字节；CRC-32 为四字节，CRC-64 为八字节。</para></param>
    /// <param name="streamFlags">The two stream-flag bytes returned by <see cref="WriteStreamHeader"/>.
    /// <para>由 <see cref="WriteStreamHeader"/> 返回的两个流标志字节。</para></param>
    internal static void WriteContainerTail(Stream output, long compressedSize, int blockHeaderLength,
        ulong uncompressedSize, byte[] check, byte[] streamFlags)
    {
        var blockPadding = (4 - ((blockHeaderLength + compressedSize % 4) % 4)) % 4;
        for (var i = 0; i < blockPadding; i++)
        {
            output.WriteByte(0);
        }

        output.Write(check, 0, check.Length);

        var unpaddedSize = checked((ulong)compressedSize + (ulong)blockHeaderLength + (ulong)check.Length);
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
    /// The largest block header this writer can build, reached when both recorded sizes take nine VLI bytes.
    /// <para>本写出器可构建的最大块头，在压缩与未压缩大小都占九字节变长整数时达到。</para>
    /// </summary>
    internal const int MaximumBlockHeaderSize = 28;

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

        var headerBodySize = MaximumBlockHeaderSize - 4;
        var realSize = 1 + bodyBytes.Length + 4;
        if (realSize > headerBodySize)
        {
            throw new InvalidOperationException("The XZ block header body exceeded its fixed size.");
        }

        var encodedHeaderSize = (byte)((MaximumBlockHeaderSize / 4) - 1);

        using var header = new MemoryStream();
        header.WriteByte(encodedHeaderSize);
        header.Write(bodyBytes, 0, bodyBytes.Length);
        while (header.Length < headerBodySize)
        {
            header.WriteByte(0);
        }

        var paddedBytes = header.ToArray();
        WriteCrc32(header, paddedBytes);

        return header.ToArray();
    }

    /// <summary>
    /// Builds the block header for the given sizes, mapping the dictionary size through <see cref="DictionarySizeToIndex"/>.
    /// <para>按给定大小构建块头，并把字典大小经 <see cref="DictionarySizeToIndex"/> 映射。</para>
    /// </summary>
    /// <param name="compressedSize">LZMA2 payload byte length; must not be negative.
    /// <para>LZMA2 载荷字节长度；不得为负。</para></param>
    /// <param name="uncompressedSize">Decoded byte count recorded in the header.
    /// <para>记录在块头中的解码字节数。</para></param>
    /// <param name="dictionarySize">Dictionary size in bytes recorded in the LZMA2 filter properties.
    /// <para>记录在 LZMA2 过滤器属性中的字典大小（字节）。</para></param>
    /// <returns>The complete block header with its CRC-32, at most <see cref="MaximumBlockHeaderSize"/> bytes long.
    /// <para>含 CRC-32 的完整块头，长度不超过 <see cref="MaximumBlockHeaderSize"/> 字节。</para></returns>
    internal static byte[] BuildBlockHeader(long compressedSize, ulong uncompressedSize, uint dictionarySize)
        => BuildBlockHeader(compressedSize, uncompressedSize, DictionarySizeToIndex(dictionarySize));

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
        => Crc32Ieee.Compute(data);

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
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
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