using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Reusable Zstandard frame encoder that carries a compression level, an optional external dictionary and the
/// frame checksum flag, so a format that emits one frame per block does not build its own ZstdSharp context.
/// <para>可复用的 Zstandard 帧编码器，承载压缩级别、可选外部字典与帧校验标志，使按块输出帧的格式无需自行构建
/// ZstdSharp 上下文。</para>
/// </summary>
/// <remarks>
/// Context parameters are fixed at construction, which is why the checksum flag lives here rather than on the
/// method: the payload writer records a checksum in every frame it emits, and a per-call parameter would let a later
/// call silently drop it. One instance is not thread-safe; create one per worker.
/// <para>上下文参数在构造时固定，因此校验标志放在这里而不是方法上：载荷写入器在每帧中记录校验和，若改为逐调用参数，
/// 后续调用就可能静默丢掉它。单个实例并非线程安全，请按工作者各建一个。</para>
/// </remarks>
public sealed class ZstdEncoder : IDisposable
{
    private readonly Compressor _compressor;
    private bool _disposed;

    /// <summary>
    /// Creates an encoder with the given level, dictionary and checksum flag.
    /// <para>按给定级别、字典与校验标志创建编码器。</para>
    /// </summary>
    /// <param name="level">Zstandard level clamped into the <c>-5</c> to <c>22</c> range; <c>3</c> matches the
    /// library default. <para>夹取到 <c>-5</c> 至 <c>22</c> 区间的 Zstandard 级别；<c>3</c> 与库默认值一致。</para></param>
    /// <param name="dictionary">External raw content dictionary, empty for dictionary-free frames.
    /// <para>外部原始内容字典；无字典帧时为空。</para></param>
    /// <param name="checksum">Whether every emitted frame records the content checksum.
    /// <para>是否在每帧中记录内容校验和。</para></param>
    /// <param name="windowLog">Window log written into every frame header when it is at least <c>10</c>, the
    /// smallest window zstd encodes; <c>0</c> keeps the window the level picks.
    /// <para>不小于 <c>10</c>（zstd 可编码的最小窗口）时写入每个帧头的窗口对数；<c>0</c> 保持级别自身选定的窗口。</para></param>
    /// <exception cref="CompressionException">The zstd context rejected the level, the dictionary or the flag.
    /// <para>zstd 上下文拒绝了级别、字典或标志。</para></exception>
    public ZstdEncoder(int level = 3, ReadOnlyMemory<byte> dictionary = default, bool checksum = false, int windowLog = 0)
    {
        try
        {
            _compressor = new Compressor(Math.Clamp(level, -5, 22));
            _compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, checksum ? 1 : 0);
            if (windowLog >= 10)
            {
                _compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
            }

            if (!dictionary.IsEmpty)
            {
                _compressor.LoadDictionary(dictionary.Span);
            }
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD frame encoder construction failed. ZSTD 帧编码器构建失败。", ex);
        }
    }

    /// <summary>
    /// Upper bound on the bytes <see cref="Compress"/> can write for a source of the given length.
    /// <para>指定长度源数据经 <see cref="Compress"/> 可能写出的字节数上界。</para>
    /// </summary>
    /// <param name="sourceLength">Source length in bytes, already bounded by the calling format.
    /// <para>源数据的字节长度，已由调用方格式限定。</para></param>
    /// <returns>The zstd compress bound, including frame header and checksum overhead.
    /// <para>zstd 压缩上界，含帧头与校验和开销。</para></returns>
    public static int MaximumCompressedSize(int sourceLength) => Compressor.GetCompressBound(sourceLength);

    /// <summary>
    /// Compresses one block into a single standalone frame.
    /// <para>把一块数据压缩为一个独立帧。</para>
    /// </summary>
    /// <param name="source">Block payload; an empty payload produces an empty frame body of zero bytes.
    /// <para>块载荷；空载荷产出零字节的空帧体。</para></param>
    /// <param name="destination">Destination span sized from <see cref="MaximumCompressedSize"/>.
    /// <para>按 <see cref="MaximumCompressedSize"/> 分配的目标区间。</para></param>
    /// <returns>Number of frame bytes written at the start of <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 起始处的帧字节数。</para></returns>
    /// <exception cref="ObjectDisposedException">This encoder was released.
    /// <para>本编码器已被释放。</para></exception>
    /// <exception cref="CompressionException">The frame did not fit or the encoder failed.
    /// <para>帧无法容纳或编码器出错。</para></exception>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return _compressor.Wrap(source, destination);
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD frame compression failed. ZSTD 帧压缩失败。", ex);
        }
    }

    /// <summary>Releases the native zstd compression context.
    /// <para>释放原生 zstd 压缩上下文。</para></summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compressor.Dispose();
    }
}