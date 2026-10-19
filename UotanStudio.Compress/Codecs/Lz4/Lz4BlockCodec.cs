using K4os.Compression.LZ4;

namespace UotanStudio.Compress.Codecs.Lz4;

/// <summary>
/// Frameless LZ4 block primitives over spans, backed by the K4os block codec, for filesystem formats that store raw
/// blocks instead of a complete LZ4 frame.
/// <para>基于 K4os 块编解码器、以 span 交换数据的无帧 LZ4 块原语，供存储原始块而非完整 LZ4 帧的文件系统格式使用。</para>
/// </summary>
/// <remarks>
/// <para>Failure travels through the return value rather than a <c>bool Try…(out int)</c> shape, because the consumers
/// use two different predicates: the F2FS cluster writer rejects <c>written &lt;= 0</c> so that a block which produced
/// nothing never reaches disk, while the F2FS and SquashFS readers reject only a negative count and treat a short but
/// valid decode as recoverable data whose length they still have to read. Folding both cases into a single
/// <see langword="false"/> would throw that length away, so the negative-return contract is preserved deliberately.</para>
/// <para>This is not <see cref="Lz4DestinationCodec"/>. That codec is a managed liblz4 port with its own match
/// selection and destination-size limits, only the EROFS paths consume it, and it emits different bytes from the K4os
/// encoder for the same input; a format must keep using the codec its clusters were written with.</para>
/// <para>Lengths and capacities are assumed already bounded by the format reader that calls in — this class adds no
/// ceiling of its own, it only forwards spans, so oversized or negative lengths are the caller's check.</para>
/// </remarks>
internal static class Lz4BlockCodec
{
    /// <summary>
    /// Worst-case size of the LZ4 block produced from a source of the given length, token and literal-run overhead included.
    /// <para>指定长度源数据所产生的 LZ4 块的最大字节数，含 token 与字面量段的额外开销。</para>
    /// </summary>
    /// <param name="sourceLength">Length in bytes of the block to compress; it must already be a non-negative value
    /// bounded by the calling format, nothing here re-validates it.
    /// <para>待压缩块的字节长度；调用方须保证其为非负且受自身格式上限约束的值，此处不做二次校验。</para></param>
    /// <returns>The upper bound on the bytes <see cref="Encode"/> can write, identical to the value the K4os codec
    /// reports, so a destination sized from it always holds a full block.
    /// <para><see cref="Encode"/> 可能写出的字节数上界，与 K4os 编解码器的返回值一致，因此按其长度分配的目标缓冲区必然容得下整块输出。</para></returns>
    internal static int MaximumOutputSize(int sourceLength) => LZ4Codec.MaximumOutputSize(sourceLength);

    /// <summary>
    /// Compresses one frameless LZ4 block.
    /// <para>压缩单个无帧 LZ4 块。</para>
    /// </summary>
    /// <param name="source">Block payload to compress; overlapping <paramref name="source"/> and
    /// <paramref name="destination"/> is not supported and must be staged by the caller.
    /// <para>待压缩的块载荷；不支持 <paramref name="source"/> 与 <paramref name="destination"/> 重叠，需由调用方分阶段缓冲。</para></param>
    /// <param name="destination">Destination span, normally sized by <see cref="MaximumOutputSize"/>; no byte is
    /// written beyond its length.
    /// <para>目标 span，通常按 <see cref="MaximumOutputSize"/> 分配；不会写入超出其长度的位置。</para></param>
    /// <param name="level">Compression level on the K4os <see cref="LZ4Level"/> scale, where <c>0</c> is the fast
    /// searcher and <c>3</c>, <c>6</c>, <c>9</c> and <c>12</c> are the high-complexity ones. Translating a format's own
    /// level scale into that scale is the caller's job and stays in the caller: this signature exposes no K4os type.
    /// Integers outside the five named anchors are handed to K4os unchanged, so an intermediate level keeps exactly the
    /// meaning it had at the call sites this codec was extracted from.
    /// <para>采用 K4os <see cref="LZ4Level"/> 尺度的压缩级别：<c>0</c> 为快速搜索，<c>3</c>、<c>6</c>、<c>9</c>、<c>12</c>
    /// 走高复杂度搜索。把各格式自身的级别尺度翻译成该尺度是调用方的职责，也留在调用方：本签名不暴露任何 K4os 类型。
    /// 五个具名锚点之外的整数原样交给 K4os，因此中间级别与本编解码器抽取自的调用点保持完全相同的含义。</para></param>
    /// <returns>Number of bytes written into <paramref name="destination"/>, or a negative K4os error code when the
    /// destination is too small or the block exceeds the codec limits. A zero count is not translated into an error
    /// here: whether an empty result is a storable block is the caller's decision, and the cluster writers reject it.
    /// <para>写入 <paramref name="destination"/> 的字节数；目标缓冲区过小或块超出编解码器上限时返回 K4os 的负值错误码。
    /// 此处的 0 不被转换为错误：空结果能否作为块存储由调用方判定，而簇写入方会将其拒绝。</para></returns>
    internal static int Encode(ReadOnlySpan<byte> source, Span<byte> destination, int level)
        => LZ4Codec.Encode(source, destination, ToLevel(level));

    /// <summary>
    /// Decompresses one frameless LZ4 block.
    /// <para>解压单个无帧 LZ4 块。</para>
    /// </summary>
    /// <param name="source">Stored block bytes, treated as untrusted; they are consumed through the span and never
    /// copied into an internal buffer.
    /// <para>存储的块字节，按不可信输入处理；仅经 span 消费，不会复制到内部缓冲区。</para></param>
    /// <param name="destination">Decode capacity; its length is the hard ceiling the block has to fit into.
    /// <para>解码容量；其长度即块输出必须容纳的硬上限。</para></param>
    /// <returns>Number of bytes produced, which is smaller than <paramref name="destination"/> whenever the stored
    /// block decodes to fewer bytes than the offered capacity. A negative K4os error code means the bitstream is
    /// corrupt or demands more room than <paramref name="destination"/> provides. Both cases surface through the return
    /// value instead of an exception, so each caller picks its own predicate and still receives the produced count
    /// when the decode succeeded but came up short.
    /// <para>产生的字节数；当存储块的实际解压结果少于给定容量时会小于 <paramref name="destination"/> 的长度。负值 K4os
    /// 错误码表示位流损坏，或其所需空间超出 <paramref name="destination"/>。两种情形都通过返回值而非异常呈现，
    /// 因此调用方可自行选择判定条件，并在解压成功但长度不足时仍拿到实际字节数。</para></returns>
    internal static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
        => LZ4Codec.Decode(source, destination);

    /// <summary>
    /// Decompresses as much of one frameless LZ4 block as fits into the destination and reports how many bytes that was.
    /// <para>尽可能多地解压单个无帧 LZ4 块到目标缓冲区，并报告实际产出的字节数。</para>
    /// </summary>
    /// <param name="source">Stored block bytes, treated as untrusted. <para>存储的块字节，按不可信输入处理。</para></param>
    /// <param name="destination">Decode capacity; the sequence stops once it is full, so the produced bytes are the
    /// prefix of the block's plaintext rather than an error.
    /// <para>解码容量；填满即停止，因此产出的是块明文的开头一段而非错误。</para></param>
    /// <returns>Number of bytes written, which may equal <paramref name="destination"/>'s length when the block continues
    /// past it. A negative value means the bitstream ran out of input or is corrupt.
    /// <para>写入的字节数；块继续超出目标长度时等于 <paramref name="destination"/> 的长度。负值表示位流输入耗尽或已损坏。</para></returns>
    /// <remarks>
    /// <para>Formats whose stored block may be a prefix of the plaintext they describe — the LZ4DIFF source of an OTA
    /// delta patch — need this instead of <see cref="Decode"/>: a full decode would report an error for a block that is
    /// simply longer than the destination, while the prefix it does produce is exactly the payload those formats store.
    /// The partner of <see cref="Decode"/> stays K4os so the partial path and the strict path read the same bitstream
    /// dialect.</para>    /// <para>存储块可能只是其明文前缀的格式——OTA 增量补丁的 LZ4DIFF 源——需要本方法而非 <see cref="Decode"/>：
    /// 完整解码会把「块本身长于目标」报成错误，而它实际产出的前缀正是这类格式所存储的载荷。与 <see cref="Decode"/>
    /// 同样落在 K4os 上，使部分解码与严格解码读的是同一种位流方言。</para>
    /// </remarks>
    internal static int PartialDecode(ReadOnlySpan<byte> source, Span<byte> destination)
        => LZ4Codec.PartialDecode(source, destination);

    /// <summary>
    /// Projects an integer on the K4os level scale onto the enumeration the block codec consumes.
    /// <para>把 K4os 级别尺度上的整数投影为块编解码器使用的枚举值。</para>
    /// </summary>
    /// <param name="level">Level as supplied by the caller. <para>调用方给出的级别。</para></param>
    /// <returns>The matching <see cref="LZ4Level"/>; a value that is not one of the named anchors is reinterpreted as
    /// <see cref="LZ4Level"/> verbatim, which is exactly what the pre-extraction call sites handed to K4os.
    /// <para>对应的 <see cref="LZ4Level"/>；不属于具名锚点的取值原样重解释为 <see cref="LZ4Level"/>，与抽取前的调用点传给 K4os 的结果一致。</para></returns>
    private static LZ4Level ToLevel(int level) => level switch
    {
        0 => LZ4Level.L00_FAST,
        3 => LZ4Level.L03_HC,
        6 => LZ4Level.L06_HC,
        9 => LZ4Level.L09_HC,
        12 => LZ4Level.L12_MAX,
        _ => (LZ4Level)level,
    };
}