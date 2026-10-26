using System.IO.Compression;

namespace UotanStudio.Compress.Codecs.Deflate;

/// <summary>
/// Raw RFC 1951 DEFLATE over the in-box codec, for formats that store a bare deflate stream with no zlib header,
/// footer or checksum, and that select the encoder strategy themselves.
/// <para>基于内置编解码器的原始 RFC 1951 DEFLATE，供存储不含 zlib 头、尾与校验和的裸 deflate 流、
/// 且自行选择编码策略的格式使用。</para>
/// </summary>
/// <remarks>
/// <para>The strategy axis exists for semi-compressed clusters: <see cref="RawDeflateStrategy.HuffmanOnly"/> emits
/// literals only, which is what a format asks for when it declares a window too small to hold matches. Choosing the
/// strategy from a window size is the caller's format rule and stays with the caller; this codec only carries the
/// choice down to the encoder.</para>
/// <para>策略维度是为半压缩簇存在的：<see cref="RawDeflateStrategy.HuffmanOnly"/> 只输出字面量，这正是格式声明的
/// 窗口小得装不下匹配时所要求的编码形态。按窗口大小决定策略属于调用方的格式规则，留在调用方；本编解码器只把该选择
/// 传给编码器。</para>
/// <para>Decompression hands back the in-box inflate reader instead of a byte-count API, so a format keeps its own
/// read loop, its own capacity ceiling and its own trailing-data judgement. Corruption surfaces as
/// <see cref="InvalidDataException"/> from the in-box decoder and is deliberately not re-wrapped: a format that
/// treats a corrupt stream as "not this codec" relies on that exact type.</para>
/// <para>解压返回内置 inflate 读取流而非字节数 API，使格式得以保留自己的读取循环、自己的容量上限与自己的尾随数据判定。
/// 损坏数据仍以内置解码器的 <see cref="InvalidDataException"/> 呈现，刻意不再包装：把损坏流当作「不是本编解码器」的格式
/// 依赖的正是这个类型本身。</para>
/// </remarks>
public static class RawDeflateCodec
{
    private const int MinimumLevel = 0;
    private const int MaximumLevel = 9;

    /// <summary>Compresses a whole source stream into a raw DEFLATE stream written to the destination.
    /// <para>把整个源流压缩为写入目标的原始 DEFLATE 流。</para></summary>
    /// <param name="source">Source positioned at the bytes to encode; read fully and left open.
    /// <para>定位在待编码字节处的可读源；读取到末尾且不会被本调用关闭。</para></param>
    /// <param name="destination">Writable sink, left open; it receives a finished DEFLATE stream.
    /// <para>写入目标，本调用返回后保持打开；收到的是完整流而非半成品。</para></param>
    /// <param name="compressionLevel">zlib-style level, clamped to the encoder range <c>0</c> to <c>9</c> before use.
    /// <para>zlib 风格级别，创建流之前夹取到内置编码器接受的 <c>0</c> 至 <c>9</c> 区间。</para></param>
    /// <param name="strategy">Encoder strategy selected by the calling format.
    /// <para>由调用格式选定的编码策略。</para></param>
    /// <exception cref="InvalidDataException">The in-box encoder rejected the stream state during the write.
    /// <para>内置编码器在写出完整流的过程中拒绝了流状态。</para></exception>
    /// <exception cref="IOException">Source or destination stream failed, including refusal over budget.
    /// <para>源或目标流出错，含目标拒绝超出其既定预算的字节。</para></exception>
    public static void Compress(
        Stream source,
        Stream destination,
        int compressionLevel,
        RawDeflateStrategy strategy = RawDeflateStrategy.Default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var options = new ZLibCompressionOptions
        {
            CompressionLevel = Math.Clamp(compressionLevel, MinimumLevel, MaximumLevel),
            CompressionStrategy = strategy == RawDeflateStrategy.HuffmanOnly
                ? ZLibCompressionStrategy.HuffmanOnly
                : ZLibCompressionStrategy.Default,
        };

        using var deflate = new DeflateStream(destination, options, leaveOpen: true);
        source.CopyTo(deflate);
    }

    /// <summary>
    /// Opens an in-box raw DEFLATE reader over the source stream.
    /// <para>在源流上打开内置的原始 DEFLATE 读取流。</para>
    /// </summary>
    /// <param name="source">Untrusted stored DEFLATE bytes at the stream start; left open.
    /// <para>存储的原始 DEFLATE 字节，按不可信输入处理并定位在流首；返回流释放时不关闭它。</para></param>
    /// <returns>Decoded bytes; the caller sets the ceiling and judges trailing bytes.
    /// <para>产出解码字节的只读流；容量上限与「声明输出之后是否还有残留字节」的判定都由调用方掌握。</para></returns>
    /// <exception cref="InvalidDataException">Raised while reading, when the stored stream is corrupt or
    /// truncated. <para>读取期间，存储流损坏或被截断时抛出。</para></exception>
    public static Stream CreateDecompressionStream(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
    }
}

/// <summary>
/// The encoder strategy a format asks <see cref="RawDeflateCodec"/> to use for one raw DEFLATE stream.
/// <para>格式要求 <see cref="RawDeflateCodec"/> 对单条原始 DEFLATE 流采用的编码器策略。</para>
/// </summary>
public enum RawDeflateStrategy
{
    /// <summary>
    /// Normal matching: literals and back-references, the strategy a full-window cluster uses.
    /// <para>常规匹配：字面量与回指，即整窗口簇使用的策略。</para>
    /// </summary>
    Default = 0,

    /// <summary>
    /// Huffman-only encoding: literals without any back-reference, which is what a cluster whose declared window
    /// cannot hold a match must be written with.
    /// <para>仅 Huffman 编码：只有字面量、不产生任何回指，即声明窗口小到装不下匹配的簇必须采用的写入方式。</para>
    /// </summary>
    HuffmanOnly = 1,
}