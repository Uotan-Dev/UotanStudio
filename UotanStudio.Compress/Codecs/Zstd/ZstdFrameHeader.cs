using System.Buffers.Binary;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Parses the declared window size out of a standalone zstd frame header per RFC 8878, so decoders can refuse
/// untrusted frames whose declared window exceeds a caller ceiling before any decode buffers are built.
/// <para>按 RFC 8878 从独立 zstd 帧头解析其声明的窗口尺寸，使解码器能在构建任何解码缓冲之前拒绝声明窗口超出
/// 调用方上限的不可信帧。</para>
/// </summary>
/// <remarks>
/// The managed zstd engine does not consult a window ceiling while decoding, so the bound must be read out of the
/// header and enforced by the caller. Only the fields that determine the window are decoded: the magic, the frame
/// header descriptor, the window descriptor (absent for single-segment frames, where the window equals the frame
/// content size) and the dictionary identifier and content size fields needed to reach the content size.
/// <para>托管 zstd 引擎在解码过程中不查看窗口上限，因此必须从帧头读出该值并由调用方强制。这里只解码与窗口相关的
/// 字段：魔数、帧头描述符、窗口描述符（单段帧没有它，此时窗口等于帧内容尺寸），以及为读到内容尺寸所需的字典
/// 标识与内容尺寸字段。</para>
/// </remarks>
internal static class ZstdFrameHeader
{
    /// <summary>Largest possible frame header in bytes. <para>帧头的最大字节数。</para></summary>
    internal const int MaximumHeaderLength = 22;

    /// <summary>Outcome of a frame header window parse. <para>帧头窗口解析的结果。</para></summary>
    internal enum ParseResult
    {
        /// <summary>The header is complete and the window was read. <para>帧头完整且已读出窗口。</para></summary>
        Parsed,
        /// <summary>More bytes are needed before the window can be read. <para>字节不足，尚不能读出窗口。</para></summary>
        NeedsMoreData,
        /// <summary>The bytes do not carry a parseable zstd frame header. <para>这些字节不含可解析的 zstd 帧头。</para></summary>
        Invalid,
    }

    /// <summary>
    /// Reads the window the frame declares out of the leading header bytes.
    /// <para>从帧的前导头字节中读出该帧声明的窗口。</para>
    /// </summary>
    /// <param name="source">Frame bytes starting at the magic; leading bytes may be fewer than the full header, in
    /// which case the parse reports that more data is needed rather than a parse failure.
    /// <para>从魔数开始的帧字节；前导字节可以少于完整帧头，此时解析报告需要更多数据而不是解析失败。</para></param>
    /// <param name="windowSize">Receives the declared window in bytes once the result is
    /// <see cref="ParseResult.Parsed"/>. <para>结果为 <see cref="ParseResult.Parsed"/> 时接收以字节计的声明窗口。</para></param>
    /// <returns>The parse outcome. <para>解析结果。</para></returns>
    internal static ParseResult TryReadWindow(ReadOnlySpan<byte> source, out long windowSize)
    {
        windowSize = 0;
        if (source.Length < 5)
        {
            return ParseResult.NeedsMoreData;
        }

        if (source[0] != 0x28 || source[1] != 0xB5 || source[2] != 0x2F || source[3] != 0xFD)
        {
            return ParseResult.Invalid;
        }

        var descriptor = source[4];
        if ((descriptor & 0x08) != 0)
        {
            return ParseResult.Invalid;
        }

        var offset = 5;
        if ((descriptor & 0x20) == 0)
        {
            if (source.Length < offset + 1)
            {
                return ParseResult.NeedsMoreData;
            }

            var windowDescriptor = source[offset];
            var exponent = windowDescriptor >> 3;
            var mantissa = windowDescriptor & 0x07;
            var windowLog = 10 + exponent;
            windowSize = (1L << windowLog) + ((long)mantissa << (windowLog - 3));
            return ParseResult.Parsed;
        }

        var dictionaryIdSize = (descriptor & 0x03) switch { 0 => 0, 1 => 1, 2 => 4, _ => 8 };
        var contentSizeField = (descriptor >> 6) switch { 0 => 1, 1 => 4, 2 => 8, _ => 2 };
        if (source.Length < offset + dictionaryIdSize + contentSizeField)
        {
            return ParseResult.NeedsMoreData;
        }

        var field = source[(offset + dictionaryIdSize)..];
        windowSize = contentSizeField switch
        {
            1 => field[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(field) + 256,
            4 => BinaryPrimitives.ReadUInt32LittleEndian(field),
            _ => unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(field)),
        };
        return ParseResult.Parsed;
    }
}