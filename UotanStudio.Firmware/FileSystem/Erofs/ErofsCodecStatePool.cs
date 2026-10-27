using System.Collections.Concurrent;
using UotanStudio.Compress.Codecs.Lzma;
using UotanStudio.Compress.Codecs.Zstd;

namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// The reusable encoder state one planning worker keeps for the duration of one payload: the LZ4 native
/// context rented from <see cref="ErofsLz4StatePool"/>, the ZSTD compressors of the level and window pairs
/// the node set uses, and one MicroLZMA encoder together with the buffer it encodes into. A planner probe
/// asks for the backend its node's algorithm needs, and each backend is created at most once per payload
/// instead of once per probe. LZ4 and ZSTD receive their full parameter set before each encode, and the
/// MicroLZMA coder properties are applied whenever the level and frame-capped dictionary change, so a reused
/// context emits exactly the bytes a freshly constructed one emits. Buffers and encoders whose retained
/// working set grew past the configured limits are released instead of recycled.
/// <para>规划工作者在一份负载期间保留的可复用编码器状态：从 <see cref="ErofsLz4StatePool"/> 借用的 LZ4 原生上下文、
/// 节点所用（级别，窗口对数）组合对应的 ZSTD 压缩器，以及一个 MicroLZMA 编码器与其编码缓冲。规划探测按节点算法
/// 取用对应后端，每个后端每份负载最多创建一次，而不是每次探测创建一次。LZ4 与 ZSTD 在每次编码前都会收到完整参数集，
/// MicroLZMA 编码属性在等级与按帧长封顶的字典变化时应用，因此复用上下文输出的字节与新构造的上下文完全一致；
/// 保留工作集超过既定上限的缓冲与编码器会被释放而不是回收。</para>
/// </summary>
internal sealed class ErofsEncoderContext : IDisposable
{
    private const int RetainedZstdConfigurations = 2;

    /// <summary>Capacity above which the MicroLZMA intermediate buffer is released instead of carried into
    /// the next payload, so a worker that encoded one large frame does not pin that allocation.
    /// <para>MicroLZMA 中间缓冲的保留上限；超过该容量时不再带入下一份负载，避免编码过一个大帧的工作者长期占住该分配。</para></summary>
    private const int LzmaOutputRetentionLimit = 256 * 1024;

    /// <summary>Dictionary size above which the retained MicroLZMA encoder is dropped on return. The
    /// LZMA-SDK match finder keeps structures proportional to the dictionary (roughly an order of magnitude
    /// larger than the window), so a dictionary this large would pin tens of megabytes per pooled worker.
    /// <para>归还时丢弃保留 MicroLZMA 编码器的字典大小阈值。LZMA-SDK 的匹配查找器持有与字典成正比的结构
    /// （约为窗口的一个数量级），如此大的字典会让每个池化工作者占住数十兆字节。</para></summary>
    private const int LargeLzmaDictionarySize = 256 * 1024;

    private readonly ErofsLz4StatePool _lz4States = new();
    private readonly Dictionary<(int Level, int WindowLog), ZstdEncoder> _zstd = [];
    private readonly Queue<(int Level, int WindowLog)> _zstdOrder = new();
    private ErofsLz4Context? _lz4Fast;
    private ErofsLz4Context? _lz4HighCompression;
    private LzmaEncoder? _lzma;
    private int _lzmaLevel = int.MinValue;
    private int _lzmaDictionarySize = int.MinValue;
    private MemoryStream? _lzmaOutput;

    /// <summary>
    /// Borrows the LZ4 context of the requested identity, renting it from the LZ4 pool on first use.
    /// <para>借出请求身份的 LZ4 上下文；首次使用时从 LZ4 池借用。</para>
    /// </summary>
    /// <param name="highCompression"><see langword="true"/> for the HC context.
    /// <para>为 <see langword="true"/> 时使用 HC 上下文。</para></param>
    /// <returns>The context this instance owns until it is disposed.
    /// <para>由本实例持有至其被释放的上下文。</para></returns>
    internal ErofsLz4Context RentLz4(bool highCompression)
        => highCompression
            ? _lz4HighCompression ??= _lz4States.Rent(true)
            : _lz4Fast ??= _lz4States.Rent(false);

    /// <summary>
    /// Returns the ZSTD compressor configured for the requested level and window log, keeping the compressors
    /// of the last few distinct pairs so switching between the compression options of two nodes does not
    /// rebuild them. A compressor is never reconfigured: the level is fixed for a compressor's lifetime and
    /// a window log left over from another level would silently change the emitted frame, so each pair gets
    /// its own instance and the least recently used one is disposed when the retained set is full.
    /// <para>返回按请求级别与窗口对数配置的 ZSTD 压缩器，并保留最近若干个不同组合的压缩器，使两个节点的压缩选项
    /// 交替时不致重建。压缩器从不被重新配置：级别在其生命周期内固定，且另一级别遗留的窗口对数会悄悄改变输出的帧，
    /// 因此每个组合各持一个实例，保留集满时释放最久未用的那个。</para>
    /// </summary>
    /// <param name="level">Compression level already clamped to the range ZstdSharp accepts.
    /// <para>已截断到 ZstdSharp 接受区间的压缩级别。</para></param>
    /// <param name="windowLog">Frame window log to apply, or <c>0</c> for the level's own default.
    /// <para>要写入的帧窗口对数；为 <c>0</c> 时使用该级别自身的默认值。</para></param>
    /// <returns>A compressor owned by this context. <para>由本上下文持有的压缩器。</para></returns>
    internal ZstdEncoder RentZstd(int level, int windowLog)
    {
        var key = (level, windowLog);
        if (_zstd.TryGetValue(key, out var retained))
        {
            return retained;
        }

        var compressor = new ZstdEncoder(level, windowLog: windowLog);

        _zstd[key] = compressor;
        _zstdOrder.Enqueue(key);
        if (_zstdOrder.Count > RetainedZstdConfigurations
            && _zstd.Remove(_zstdOrder.Dequeue(), out var evicted))
        {
            evicted.Dispose();
        }

        return compressor;
    }

    /// <summary>
    /// Returns the retained MicroLZMA encoder configured for the requested level and frame-capped dictionary
    /// size. The coder properties are re-applied only when that pair changes, because the planning loop
    /// probes spans that often share a dictionary size; re-applying unchanged values is equivalent to
    /// skipping the call, so this does not alter the emitted bytes. The LZMA-SDK match finder still rebuilds
    /// its dictionary-sized structures when the pair does change (the planner caps the dictionary to each
    /// frame, so alternating frame lengths do), which is the setup cost this reuse cannot remove without a
    /// native encoder.
    /// <para>返回按请求等级与按帧长封顶的字典大小配置的保留 MicroLZMA 编码器。仅当该组合变化时才重新应用编码属性，
    /// 因为规划循环探测的跨度往往共享同一字典大小；应用相同取值等价于跳过调用，因此不会改变输出字节。组合确实变化时
    /// LZMA-SDK 的匹配查找器仍会重建与字典等大的结构（规划器按每个帧封顶字典，因此帧长交替时就会发生），这是不留用
    /// 原生编码器时无法消除的准备成本。</para>
    /// </summary>
    /// <param name="compressionLevel">Level selecting the LZMA algorithm and fast-byte count.
    /// <para>选择 LZMA 算法与 fast-bytes 数量的等级。</para></param>
    /// <param name="dictionarySize">Dictionary size in bytes already capped to the frame length.
    /// <para>已按帧长封顶的字典字节数。</para></param>
    /// <returns>The encoder this context owns until it is disposed.
    /// <para>由本上下文持有至其被释放的编码器。</para></returns>
    internal LzmaEncoder RentLzma(int compressionLevel, int dictionarySize)
    {
        var encoder = _lzma;
        if (encoder is null)
        {
            encoder = new LzmaEncoder();
            ErofsCompression.ConfigureLzmaEncoder(encoder, compressionLevel, dictionarySize);
            _lzma = encoder;
            _lzmaLevel = compressionLevel;
            _lzmaDictionarySize = dictionarySize;
            return encoder;
        }

        if (compressionLevel != _lzmaLevel || dictionarySize != _lzmaDictionarySize)
        {
            ErofsCompression.ConfigureLzmaEncoder(encoder, compressionLevel, dictionarySize);
            _lzmaLevel = compressionLevel;
            _lzmaDictionarySize = dictionarySize;
        }

        return encoder;
    }

    /// <summary>
    /// Returns the retained buffer the MicroLZMA encoder writes its intermediate stream into, so the encoder
    /// reuses one allocation instead of one growable buffer per encode. Its content is discarded and its
    /// length reset before every encode, which is why a reused buffer emits the same bytes.
    /// <para>返回 MicroLZMA 编码器写入中间流的保留缓冲，使编码器复用一个分配而不是每次编码一个新增长缓冲。
    /// 每次编码前都会丢弃其内容并重置长度，因此复用缓冲输出同样的字节。</para>
    /// </summary>
    internal MemoryStream LzmaOutput => _lzmaOutput ??= new MemoryStream();

    /// <summary>
    /// Releases the MicroLZMA intermediate buffer once it grew past <see cref="LzmaOutputRetentionLimit"/>, so
    /// a worker that encoded one large frame does not carry that allocation for the rest of its life. Smaller
    /// buffers are kept for reuse.
    /// <para>MicroLZMA 中间缓冲超过 <see cref="LzmaOutputRetentionLimit"/> 后将其释放，使编码过一次大帧的工作者
    /// 不会在余生一直携带该分配；更小的缓冲保留复用。</para>
    /// </summary>
    internal void TrimLzmaOutput()
    {
        var output = _lzmaOutput;
        if (output is null || output.Capacity <= LzmaOutputRetentionLimit)
        {
            return;
        }

        output.SetLength(0);
        output.Capacity = 0;
    }

    /// <summary>
    /// Drops the retained MicroLZMA encoder when its last dictionary was large enough to keep a multi-megabyte
    /// match-finder working set. The next encode creates a fresh encoder and applies that frame's own level and
    /// dictionary, so this changes only the retained working set, never the emitted bytes.
    /// <para>当保留 MicroLZMA 编码器上一次使用的字典大到会留下数兆字节的匹配查找器工作集时将其丢弃。下一次编码会新建
    /// 编码器并应用该帧自身的等级与字典，因此改变的只是保留的工作集，绝不会改变输出字节。</para>
    /// </summary>
    internal void TrimLargeLzmaState()
    {
        TrimLzmaOutput();
        if (_lzmaDictionarySize < LargeLzmaDictionarySize)
        {
            return;
        }

        _lzma = null;
        _lzmaLevel = int.MinValue;
        _lzmaDictionarySize = int.MinValue;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_lz4Fast is not null)
        {
            _lz4States.Return(_lz4Fast);
            _lz4Fast = null;
        }

        if (_lz4HighCompression is not null)
        {
            _lz4States.Return(_lz4HighCompression);
            _lz4HighCompression = null;
        }

        _lz4States.Dispose();
        foreach (var compressor in _zstd.Values)
        {
            compressor.Dispose();
        }

        _zstd.Clear();
        _zstdOrder.Clear();
        _lzma = null;
        _lzmaLevel = int.MinValue;
        _lzmaDictionarySize = int.MinValue;
        _lzmaOutput?.Dispose();
        _lzmaOutput = null;
    }
}

/// <summary>
/// Recycles <see cref="ErofsEncoderContext"/> instances between the parallel planning workers of one build, so
/// a worker that finishes a payload hands its ZSTD and MicroLZMA state to the next payload instead of paying
/// the workspace construction again. A context is owned by one thread at a time and never shared.
/// <para>在同一次构建的并行规划工作者之间回收 <see cref="ErofsEncoderContext"/>。工作者完成一份负载后把 ZSTD 与
/// MicroLZMA 状态交给下一份负载，而不必再次付出工作区构造代价。上下文同一时刻只属于一个线程，绝不被共享。</para>
/// </summary>
internal sealed class ErofsCodecStatePool : IDisposable
{
    private readonly ConcurrentQueue<ErofsEncoderContext> _idle = new();

    /// <summary>
    /// Borrows an idle encoder context, or allocates one when the pool is empty.
    /// <para>借用空闲的编码器上下文；池为空时新建一个。</para>
    /// </summary>
    /// <returns>A context the caller owns until <see cref="Return"/>.
    /// <para>由调用方持有至 <see cref="Return"/> 为止的上下文。</para></returns>
    internal ErofsEncoderContext Rent() => _idle.TryDequeue(out var context) ? context : new ErofsEncoderContext();

    /// <summary>
    /// Returns a borrowed context, disposing it when the pool already holds one per worker. A context that
    /// grew a large MicroLZMA working set is trimmed first, so a payload with big frames cannot pin that
    /// allocation in the pool.
    /// <para>归还借出的上下文；池中已按工作者数量存满时直接释放。曾增长出大 MicroLZMA 工作集的上下文会先被裁剪，
    /// 因此含大帧的负载不会把该分配长期留在池中。</para>
    /// </summary>
    /// <param name="context">Context obtained from <see cref="Rent"/>.
    /// <para>由 <see cref="Rent"/> 取得的上下文。</para></param>
    internal void Return(ErofsEncoderContext context)
    {
        if (_idle.Count >= Core.FirmwareParallelism.MaximumWorkers)
        {
            context.Dispose();
            return;
        }

        context.TrimLargeLzmaState();
        _idle.Enqueue(context);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        while (_idle.TryDequeue(out var context))
        {
            context.Dispose();
        }
    }
}

/// <summary>
/// The reusable decoder state one read context keeps while it decodes a compressed EROFS image: one ZSTD
/// decompressor and one MicroLZMA decoder. Both hold the workspace a fresh instance would allocate per
/// pcluster, which is the dominant cost of decoding small pclusters; neither carries state between frames,
/// because every EROFS pcluster is an independent frame that starts a new decode.
/// <para>读取上下文在解码压缩 EROFS 镜像期间保留的可复用解码器状态：一个 ZSTD 解压器与一个 MicroLZMA 解码器。
/// 两者都持有新建实例会为每个 pcluster 重新分配的工作区，而这是解码小 pcluster 的主要成本；帧之间不携带状态，
/// 因为每个 EROFS pcluster 都是一次独立解码的新帧。</para>
/// </summary>
internal sealed class ErofsDecoderContext
{
    private ZstdDecoder? _zstd;
    private int _zstdWindowLog = int.MinValue;
    private LzmaDecoder? _lzma;

    /// <summary>
    /// Returns the ZSTD decompressor constrained to the requested window log, replacing the retained one when
    /// the log changes. A larger window log than the previous frame's would otherwise be clamped by the stale
    /// maximum and reject a valid frame.
    /// <para>返回按请求窗口对数约束的 ZSTD 解压器；对数变化时替换保留的解压器。否则上一次的过小上限会把新帧的窗口
    /// 截断并拒绝一个合法帧。</para>
    /// </summary>
    /// <param name="windowLog">Maximum window log to apply, or <c>0</c> for the decoder default.
    /// <para>要施加的最大窗口对数；为 <c>0</c> 时使用解码器默认值。</para></param>
    /// <returns>A decompressor owned by this context. <para>由本上下文持有的解压器。</para></returns>
    internal ZstdDecoder RentZstd(int windowLog)
    {
        if (_zstd is null || _zstdWindowLog != windowLog)
        {
            _zstd?.Dispose();
            _zstd = new ZstdDecoder(maximumWindowLog: windowLog);

            _zstdWindowLog = windowLog;
        }

        return _zstd;
    }

    /// <summary>
    /// Returns the retained MicroLZMA decoder. Its properties are applied per decode, because the caller may
    /// resolve a different dictionary size for each pcluster.
    /// <para>返回保留的 MicroLZMA 解码器。调用方可能为每个 pcluster 解析不同的字典大小，因此解码属性逐次应用。</para>
    /// </summary>
    internal LzmaDecoder LzmaDecoder => _lzma ??= new LzmaDecoder();

    /// <summary>
    /// The five-byte property block handed to <see cref="LzmaDecoder.SetProperties(byte[])"/>. It is
    /// rewritten from the frame's own header before every decode, so the retained array never carries the
    /// previous pcluster's dictionary size.
    /// <para>交给 <see cref="LzmaDecoder.SetProperties(byte[])"/> 的五字节属性块。每次解码前都由当前帧自身
    /// 的头部重写，因此保留的数组不会沿用上一个 pcluster 的字典大小。</para>
    /// </summary>
    internal byte[] LzmaProperties { get; } = new byte[5];

    /// <summary>Releases the retained ZSTD decompressor. <para>释放保留的 ZSTD 解压器。</para></summary>
    internal void Dispose()
    {
        _zstd?.Dispose();
        _zstd = null;
    }
}

/// <summary>
/// Recycles <see cref="ErofsDecoderContext"/> instances among the workers that decode one image, so a thread
/// that finishes a pcluster hands its ZSTD and MicroLZMA workspace to the next pcluster instead of rebuilding
/// it. Borrowing is a single lock-free queue operation against a decode that costs tens of microseconds.
/// <para>在解码同一镜像的工作者之间回收 <see cref="ErofsDecoderContext"/>。完成一个 pcluster 的线程把 ZSTD 与
/// MicroLZMA 工作区交给下一个 pcluster，而不必重建。借出只是一次无锁队列操作，而单次解码耗时数十微秒。</para>
/// </summary>
internal sealed class ErofsDecoderStatePool : IDisposable
{
    private readonly ConcurrentQueue<ErofsDecoderContext> _idle = new();

    /// <summary>
    /// Borrows an idle decoder context, or allocates one when the pool is empty.
    /// <para>借用空闲的解码器上下文；池为空时新建一个。</para>
    /// </summary>
    /// <returns>A context the caller owns until <see cref="Return"/>.
    /// <para>由调用方持有至 <see cref="Return"/> 为止的上下文。</para></returns>
    internal ErofsDecoderContext Rent() => _idle.TryDequeue(out var context) ? context : new ErofsDecoderContext();

    /// <summary>
    /// Returns a borrowed context, disposing it when the pool already holds one per worker.
    /// <para>归还借出的上下文；池中已按工作者数量存满时直接释放。</para>
    /// </summary>
    /// <param name="context">Context obtained from <see cref="Rent"/>.
    /// <para>由 <see cref="Rent"/> 取得的上下文。</para></param>
    internal void Return(ErofsDecoderContext context)
    {
        if (_idle.Count >= Core.FirmwareParallelism.MaximumWorkers)
        {
            context.Dispose();
            return;
        }

        _idle.Enqueue(context);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        while (_idle.TryDequeue(out var context))
        {
            context.Dispose();
        }
    }
}