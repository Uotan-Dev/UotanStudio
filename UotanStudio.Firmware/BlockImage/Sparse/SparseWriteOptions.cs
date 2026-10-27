namespace UotanStudio.Firmware.BlockImage.Sparse;

/// <summary>
/// Optional geometry knobs for the sparse write path. The defaults are a <c>4096</c>-byte block,
/// no RAW block cap beyond the implicit 32-bit payload limit, no trailing CRC32 chunk and no
/// total-size alignment padding. Every consumer treats <see langword="null"/>
/// options as identical to a freshly created instance.
/// <para>稀疏写出路径的可选几何参数。默认值为 <c>4096</c> 字节块、除隐式 32 位载荷上限外不限制
/// RAW 块、不追加 CRC32 尾块、不做总大小对齐填充。所有消费者都把 <see langword="null"/> 视为与新建实例等价。</para>
/// </summary>
public sealed record SparseWriteOptions
{
    /// <summary>Minimum accepted block size in bytes. <para>接受的最小块大小（字节）。</para></summary>
    public const int MinimumBlockSize = 1024;

    /// <summary>Maximum accepted block size in bytes. <para>接受的最大块大小（字节）。</para></summary>
    public const int MaximumBlockSize = 65536;

    /// <summary>Maximum accepted total-size alignment in blocks. <para>接受的总大小对齐上限（块数）。</para></summary>
    public const uint MaximumFileAlignment = 1048576;

    /// <summary>
    /// Gets the logical block size the pack path classifies zero, fill and raw runs with.
    /// <para>获取打包路径用于判定零块、填充块与原始块的逻辑块大小。</para>
    /// </summary>
    /// <value>
    /// A power of two from <see cref="MinimumBlockSize"/> through <see cref="MaximumBlockSize"/>; defaults to <c>4096</c>.
    /// <para><see cref="MinimumBlockSize"/> 至 <see cref="MaximumBlockSize"/> 之间 2 的幂；默认 <c>4096</c>。</para>
    /// </value>
    public uint BlockSize { get; init; } = 4096;

    /// <summary>
    /// Gets an optional cap on the block count of one emitted RAW chunk.
    /// <para>获取单个输出 RAW 块块数的可选上限。</para>
    /// </summary>
    /// <value>
    /// When set, oversized RAW runs split into chunks of at most this many blocks; the format already
    /// splits at the largest count whose payload plus header still fits a 32-bit total size, so a cap
    /// below that only forces earlier splits. Must be at least <c>1</c>; <see langword="null"/> keeps
    /// the default 32-bit fitting.
    /// <para>设置后，超大 RAW 连续区会拆分为最多该块数的块；格式本身已在“载荷加头部仍适配 32 位总大小”的最大块数处
    /// 拆分，因此更低的值只是强制更早拆分。至少为 <c>1</c>；<see langword="null"/> 保持默认的 32 位适配拆分。</para>
    /// </value>
    public uint? MaxRawChunkBlocks { get; init; }

    /// <summary>
    /// Gets a value indicating whether a trailing CRC32 chunk over the fully expanded image is emitted.
    /// <para>获取是否追加覆盖完整展开镜像的 CRC32 尾块。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> appends the chunk; the default <see langword="false"/> keeps byte output
    /// identical to the historical writer. The reader tolerates both shapes.
    /// <para>为 <see langword="true"/> 时追加该块；默认 <see langword="false"/> 使字节输出与历史写入器完全一致。读取器两种形态都能接受。</para>
    /// </value>
    public bool EmitCrc { get; init; }

    /// <summary>
    /// Gets the block count the declared total size is rounded up to before writing.
    /// <para>获取写出前将声明总大小向上取整到的块数倍数。</para>
    /// </summary>
    /// <value>
    /// A total-block multiple from <c>1</c> through <see cref="MaximumFileAlignment"/>; the default
    /// <c>1</c> adds no padding. Values above <c>1</c> append a trailing don't-care chunk so the image
    /// length is a multiple of the alignment in blocks.
    /// <para><c>1</c> 至 <see cref="MaximumFileAlignment"/> 的总块数倍数；默认 <c>1</c> 不添加填充。大于 <c>1</c>
    /// 时追加 don't-care 尾块，使镜像长度成为该对齐（以块计）的倍数。</para>
    /// </value>
    public uint FileAlignment { get; init; } = 1;

    /// <summary>
    /// Validates every dimension against the sparse format's accepted bounds.
    /// <para>按稀疏格式接受的边界校验全部维度。</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="BlockSize"/> is not a power of two within the accepted range, <see cref="FileAlignment"/>
    /// is below <c>1</c> or above <see cref="MaximumFileAlignment"/>, or <see cref="MaxRawChunkBlocks"/> is zero.
    /// <para><see cref="BlockSize"/> 不是允许范围内的 2 的幂、<see cref="FileAlignment"/> 低于 <c>1</c> 或高于
    /// <see cref="MaximumFileAlignment"/>，或 <see cref="MaxRawChunkBlocks"/> 为 0。</para>
    /// </exception>
    public void Validate()
    {
        if (BlockSize < MinimumBlockSize || BlockSize > MaximumBlockSize || (BlockSize & (BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockSize),
                $"Sparse block size must be a power of two between {MinimumBlockSize} and {MaximumBlockSize}.");
        }
        if (FileAlignment is 0 or > MaximumFileAlignment)
        {
            throw new ArgumentOutOfRangeException(nameof(FileAlignment),
                "Sparse file alignment must be between 1 and 1048576 blocks.");
        }
        if (MaxRawChunkBlocks is 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRawChunkBlocks),
                "The sparse RAW chunk block cap must be at least 1.");
        }
    }
}