using SevenZip.Compression.LZMA;

namespace UotanStudio.Compress.Codecs.Lzma;

/// <summary>
/// LZMA coder over the LZMA-SDK decoder, seeded with the coder-properties byte a format recorded instead of a
/// container header it reads back, and framed by nothing: reading a size field, prepending the byte MicroLZMA
/// dropped or capping the destination is the format's own business.
/// <para>LZMA coder，基于 LZMA-SDK 解码器，以格式记录的编码器属性字节初始化（而不是从容器头读回），且不做任何
/// 封装：读取大小字段、补回 MicroLZMA 丢掉的字节、限制输出目标容量都是格式层自己的职责。</para>
/// </summary>
/// <remarks>
/// The properties are forwarded to the SDK verbatim, so the SDK's own range checks and failure reporting stay
/// exactly as a format observed them before this type existed; its rejection uses an exception type the SDK does not
/// expose.
/// <para>属性字节原样转发给 SDK，因此 SDK 自身的范围检查与失败上报与引入本类型之前格式所观察到的完全一致；
/// 其拒绝路径使用 SDK 不对外暴露的异常类型。</para>
/// </remarks>
public sealed class LzmaDecoder
{
    private readonly Decoder _coder = new();

    /// <summary>Creates a decoder whose coder properties the caller applies before the first decode, which is how a pooled decoder is built once and reconfigured per frame.
    /// <para>创建一个由调用方在首次解码前应用编码器属性的解码器，用于池化解码器只构建一次、逐帧重新配置的场景。</para></summary>
    public LzmaDecoder()
    {
    }

    /// <summary>Swaps the coder properties of a retained decoder, so a pooled decoder serves the next frame.
    /// <para>替换保留解码器的编码器属性，使池化解码器服务下一帧。</para></summary>
    /// <param name="coderProperties">The five LZMA-alone property bytes. <para>5 字节的 LZMA-alone 属性。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="coderProperties"/> is <see langword="null"/>.
    /// <para><paramref name="coderProperties"/> 为 <see langword="null"/>。</para></exception>
    public void SetProperties(byte[] coderProperties)
    {
        ArgumentNullException.ThrowIfNull(coderProperties);
        _coder.SetDecoderProperties(coderProperties);
    }

    /// <summary>Creates a decoder for the given five LZMA-alone property bytes.
    /// <para>按给定的 5 字节 LZMA-alone 属性创建解码器。</para></summary>
    /// <param name="coderProperties">The five coder-properties byte: the lc/lp/pb packing in the first byte and the
    /// little-endian dictionary size that follows, exactly as the writer stored them. <para>5 字节的编码器属性：首字节的
    /// lc/lp/pb 打包，随后是小端字典大小，与写入方存储的完全一致。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="coderProperties"/> is <see langword="null"/>.
    /// <para><paramref name="coderProperties"/> 为 <see langword="null"/>。</para></exception>
    public LzmaDecoder(byte[] coderProperties)
    {
        ArgumentNullException.ThrowIfNull(coderProperties);
        _coder.SetDecoderProperties(coderProperties);
    }

    /// <summary>Decodes the input stream into the output stream.
    /// <para>把输入流解码写入输出流。</para></summary>
    /// <param name="input">Stream supplying the encoded bytes from its current position; it is left open.
    /// <para>从其当前位置提供编码字节的流，调用后保持打开。</para></param>
    /// <param name="output">Stream receiving the plaintext at its current position and left open; a stream that bounds its own capacity reports the overflow itself.
    /// <para>在其当前位置接收明文且本调用不会关闭的流；自带容量限制的流会自行报告越界。</para></param>
    /// <param name="inSize">Number of encoded bytes the SDK may read, or <c>-1</c> for "unknown", which is the SDK convention for draining the whole stream.
    /// <para>SDK 可以读取的编码字节数；<c>-1</c> 表示未知，这是 SDK 用于读完整个流的约定。</para></param>
    /// <param name="outSize">Number of plaintext bytes the SDK may produce, or <c>-1</c> for "unknown"; a format that stores the decompressed size passes it here so a short decode is observable on the destination stream.
    /// <para>SDK 可以产出的明文字节数；<c>-1</c> 表示未知；存储解压后大小的格式把该值传到这里，从而可在目标流上观察到解码长度不足。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    public void Code(Stream input, Stream output, long inSize, long outSize)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _coder.Code(input, output, inSize, outSize, null);
    }
}