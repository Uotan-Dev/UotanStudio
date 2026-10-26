using SevenZip;
using SevenZip.Compression.LZMA;

namespace UotanStudio.Compress.Codecs.Lzma;

/// <summary>
/// LZMA coder over the LZMA-SDK encoder, configured with exactly the coder-property axes the caller supplies: an
/// axis that is left out keeps the SDK's own default, because writing a default of ours would change the bytes a
/// format already committed to disk.
/// <para>LZMA coder，基于 LZMA-SDK 编码器，只按调用方实际提供的编码器属性轴配置：未提供的属性轴保持 SDK 自身的
/// 默认值，因为由本库写入默认值会改变格式已经写入磁盘的字节。</para>
/// </summary>
/// <remarks>
/// <para>Container framing deliberately stays with the caller. The single property byte, the LZMA-alone eight-byte
/// size field, the negated property byte MicroLZMA stores and the properties-then-size order a container writes are
/// format contracts; this type produces only the payload those frames wrap, and it never rewrites a property the
/// caller did not name.</para>
/// <para>容器封装刻意留给调用方。单个属性字节、LZMA-alone 的 8 字节大小字段、MicroLZMA 存储的属性取反字节，
/// 以及容器先写属性再写大小的顺序都是格式契约；本类型只产出这些封装所包裹的载荷，也不会改写调用方未点名的属性。</para>
/// <para>The SDK applies the axes independently, so the order in which they are listed here does not affect the
/// encoded result; only which axes are present does.</para>
/// <para>SDK 会彼此独立地应用各属性轴，因此此处列出它们的顺序不影响编码结果，影响的只是哪些属性轴被提供。</para>
/// <para>The SDK rejects an out-of-range axis with an exception type it does not expose, so a format that has to
/// bound a value validates it before constructing the encoder.</para>
/// <para>SDK 会用一个不对外暴露的异常类型拒绝越界的属性轴，因此需要限定取值范围的格式层应在构造编码器之前自行校验。</para>
/// </remarks>
public sealed class LzmaEncoder
{
    private readonly Encoder _coder = new();

    /// <summary>Creates an LZMA-SDK encoder and hands it only the property axes that were supplied.
    /// <para>创建 LZMA-SDK 编码器，并只把实际提供的属性轴交给它。</para></summary>
    /// <param name="dictionarySize">Match-finder dictionary window in bytes, handed to the SDK as a 32-bit integer.
    /// <para>匹配查找器的字典窗口大小（字节），以 32 位整数交给 SDK。</para></param>
    /// <param name="posStateBits">Position state bits (pb) of the probability model.
    /// <para>概率模型的位置状态位数（pb）。</para></param>
    /// <param name="litContextBits">Literal context bits (lc) of the probability model.
    /// <para>概率模型的字面量上下文位数（lc）。</para></param>
    /// <param name="litPosBits">Literal position bits (lp) of the probability model.
    /// <para>概率模型的字面量位置位数（lp）。</para></param>
    /// <param name="algorithm">SDK algorithm selector, whose accepted range and meaning the SDK owns.
    /// <para>SDK 的算法选择器，其取值范围与含义由 SDK 决定。</para></param>
    /// <param name="numFastBytes">Number of bytes the match finder tries to extend, whose accepted range the SDK owns.
    /// <para>匹配查找器尝试延伸的字节数，其取值范围由 SDK 决定。</para></param>
    /// <param name="endMarker">Whether the encoded stream terminates with an end marker instead of a size declared outside this coder.
    /// <para>编码流是否以结束标记收尾，而不是依赖本 coder 之外声明的大小。</para></param>
    public LzmaEncoder(
        int? dictionarySize = null,
        int? posStateBits = null,
        int? litContextBits = null,
        int? litPosBits = null,
        int? algorithm = null,
        int? numFastBytes = null,
        bool? endMarker = null)
    {
        var identifiers = new List<CoderPropID>(7);
        var values = new List<object>(7);

        Add(CoderPropID.DictionarySize, dictionarySize);
        Add(CoderPropID.PosStateBits, posStateBits);
        Add(CoderPropID.LitContextBits, litContextBits);
        Add(CoderPropID.LitPosBits, litPosBits);
        Add(CoderPropID.Algorithm, algorithm);
        Add(CoderPropID.NumFastBytes, numFastBytes);
        Add(CoderPropID.EndMarker, endMarker);

        _coder.SetCoderProperties([.. identifiers], [.. values]);

        void Add(CoderPropID identifier, object? value)
        {
            if (value is null)
            {
                return;
            }

            identifiers.Add(identifier);
            values.Add(value);
        }
    }

    /// <summary>
    /// Writes the five coder-properties byte the SDK derives from the configured axes.
    /// <para>写出 SDK 依据已配置属性轴得出的 5 字节编码器属性。</para>
    /// </summary>
    /// <param name="output">Stream receiving the five bytes at its current position and left open by this
    /// call. <para>在其当前位置接收这 5 个字节、且本调用不会关闭的流。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    public void WriteCoderProperties(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _coder.WriteCoderProperties(output);
    }

    /// <summary>Encodes the input stream into the output stream with the configured axes.
    /// <para>按已配置的属性轴把输入流编码写入输出流。</para> </summary>
    /// <param name="input">Stream supplying the plaintext; it is consumed from its current position and left open.
    /// <para>提供明文的流；从其当前位置开始消费，调用后保持打开。</para> </param>
    /// <param name="output">Stream receiving the encoded bytes at its current position and left open.
    /// <para>在其当前位置接收编码字节、且本调用不会关闭的流。</para> </param>
    /// <param name="inSize">Number of plaintext bytes the SDK may read, or <c>-1</c> for "unknown", which is the SDK convention for draining the whole stream and is what an end-marked stream needs.
    /// <para>SDK 可以读取的明文字节数；<c>-1</c> 表示未知，这是 SDK 用于读完整个流的约定，也是带结束标记的流所要求的。</para> </param>
    /// <param name="outSize">Number of plaintext bytes the SDK is allowed to encode, or <c>-1</c> for "unknown"; a fixed-size stream without an end marker passes the exact input length here.
    /// <para>允许 SDK 编码的明文字节数；<c>-1</c> 表示未知；不带结束标记的定长流在此传入精确的输入长度。</para> </param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para> </exception>
    public void Code(Stream input, Stream output, long inSize, long outSize)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _coder.Code(input, output, inSize, outSize, null);
    }
}