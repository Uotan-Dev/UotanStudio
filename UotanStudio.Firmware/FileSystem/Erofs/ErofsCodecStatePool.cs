using System.Collections.Concurrent;
using SevenZip.Compression.LZMA;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// The reusable encoder state one planning worker keeps for the duration of one payload: the LZ4 native
/// context rented from <see cref="ErofsLz4StatePool"/>, one ZSTD compressor, and one MicroLZMA encoder.
/// A planner probe asks for the backend its node's algorithm needs, and each backend is created at most once
/// per payload instead of once per probe. Every backend receives its full parameter set before each encode,
/// so a reused context emits exactly the bytes a freshly constructed one emits.
/// <para>规划工作者在一份负载期间保留的可复用编码器状态：从 <see cref="ErofsLz4StatePool"/> 借用的 LZ4 原生上下文、
/// 一个 ZSTD 压缩器与一个 MicroLZMA 编码器。规划探测按节点算法取用对应后端，每个后端每份负载最多创建一次，
/// 而不是每次探测创建一次。每个后端在每次编码前都会收到完整参数集，因此复用上下文输出的字节与新构造的上下文完全一致。</para>
/// </summary>
internal sealed class ErofsEncoderContext : IDisposable
{
    private readonly ErofsLz4StatePool _lz4States = new();
    private ErofsLz4Context? _lz4Fast;
    private ErofsLz4Context? _lz4HighCompression;
    private Compressor? _zstd;
    private int _zstdLevel = int.MinValue;
    private int _zstdWindowLog = int.MinValue;
    private Encoder? _lzma;

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
    /// Returns the ZSTD compressor configured for the requested level and window log, replacing the retained
    /// compressor when either parameter changes. A replaced compressor is disposed rather than reconfigured,
    /// because the level is fixed for a compressor's lifetime and a window log left over from another level
    /// would silently change the emitted frame.
    /// <para>返回按请求级别与窗口对数配置的 ZSTD 压缩器；任一参数变化时替换保留的压缩器。被替换的压缩器会被释放而不是
    /// 重新配置，因为级别在压缩器生命周期内固定，且另一级别遗留的窗口对数会悄悄改变输出的帧。</para>
    /// </summary>
    /// <param name="level">Compression level already clamped to the range ZstdSharp accepts.
    /// <para>已截断到 ZstdSharp 接受区间的压缩级别。</para></param>
    /// <param name="windowLog">Frame window log to apply, or <c>0</c> for the level's own default.
    /// <para>要写入的帧窗口对数；为 <c>0</c> 时使用该级别自身的默认值。</para></param>
    /// <returns>A compressor owned by this context. <para>由本上下文持有的压缩器。</para></returns>
    internal Compressor RentZstd(int level, int windowLog)
    {
        if (_zstd is null || _zstdLevel != level || _zstdWindowLog != windowLog)
        {
            _zstd?.Dispose();
            _zstd = new Compressor(level);
            if (windowLog > 0)
            {
                _zstd.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
            }

            _zstdLevel = level;
            _zstdWindowLog = windowLog;
        }

        return _zstd;
    }

    /// <summary>
    /// Returns the retained MicroLZMA encoder. Its coder properties are applied per encode, because the
    /// planner resolves a dictionary size from each candidate span, so the retained object never assumes the
    /// previous call's properties.
    /// <para>返回保留的 MicroLZMA 编码器。规划器会按每个候选跨度推导字典大小，因此编码属性逐次在编码前应用，
    /// 保留的对象从不沿用上一次调用的属性。</para>
    /// </summary>
    internal Encoder LzmaEncoder => _lzma ??= new Encoder();

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
        _zstd?.Dispose();
        _zstd = null;
        _lzma = null;
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
    /// Returns a borrowed context, disposing it when the pool already holds one per worker.
    /// <para>归还借出的上下文；池中已按工作者数量存满时直接释放。</para>
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
    private Decompressor? _zstd;
    private int _zstdWindowLog = int.MinValue;
    private Decoder? _lzma;

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
    internal Decompressor RentZstd(int windowLog)
    {
        if (_zstd is null || _zstdWindowLog != windowLog)
        {
            _zstd?.Dispose();
            _zstd = new Decompressor();
            if (windowLog > 0)
            {
                _zstd.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, windowLog);
            }

            _zstdWindowLog = windowLog;
        }

        return _zstd;
    }

    /// <summary>
    /// Returns the retained MicroLZMA decoder. Its properties are applied per decode, because the caller may
    /// resolve a different dictionary size for each pcluster.
    /// <para>返回保留的 MicroLZMA 解码器。调用方可能为每个 pcluster 解析不同的字典大小，因此解码属性逐次应用。</para>
    /// </summary>
    internal Decoder LzmaDecoder => _lzma ??= new Decoder();

    /// <summary>
    /// The five-byte property block handed to <see cref="Decoder.SetDecoderProperties(byte[])"/>. It is
    /// rewritten from the frame's own header before every decode, so the retained array never carries the
    /// previous pcluster's dictionary size.
    /// <para>交给 <see cref="Decoder.SetDecoderProperties(byte[])"/> 的五字节属性块。每次解码前都由当前帧自身
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