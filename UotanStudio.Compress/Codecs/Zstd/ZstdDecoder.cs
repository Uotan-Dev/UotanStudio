using System.Buffers;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Reusable Zstandard frame decoder that carries an optional external dictionary and a window ceiling, so formats
/// that decode one frame per block reuse a single native context instead of renting ZstdSharp types directly.
/// <para>可复用的 Zstandard 帧解码器，承载可选外部字典与窗口上限，使按块解码帧的格式复用同一原生上下文，
/// 而不必直接租借 ZstdSharp 类型。</para>
/// </summary>
/// <remarks>
/// <para>The window ceiling comes from construction because ZstdSharp's one-shot frame decode never consults
/// <c>ZSTD_d_windowLogMax</c>; the bound is set on the context here so every later frame inherits it, and
/// <see cref="Reset"/> re-establishes a clean frame boundary without losing it. One instance is not thread-safe.</para>
/// <para>窗口上限在构造时确定，因为 ZstdSharp 的一次性帧解码从不查看 <c>ZSTD_d_windowLogMax</c>；此处把它设在上下文上，
/// 使后续每帧都继承它，<see cref="Reset"/> 只重建干净的帧边界而不丢失该上限。单个实例并非线程安全。</para>
/// </remarks>
public sealed class ZstdDecoder : IDisposable
{
    private readonly ReadOnlyMemory<byte> _dictionary;
    private readonly int _maximumWindowLog;
    private Decompressor? _decompressor;
    private bool _disposed;

    /// <summary>
    /// Creates a decoder for frames that were produced against the given dictionary and window ceiling.
    /// <para>为据给定字典与窗口上限产出的帧创建解码器。</para>
    /// </summary>
    /// <param name="dictionary">External raw content dictionary, empty for dictionary-free frames.
    /// <para>外部原始内容字典；无字典帧时为空。</para></param>
    /// <param name="maximumWindowLog">Window ceiling in log2 bytes; <c>0</c> keeps the frame's declared window.
    /// <para>以 2 为底的窗口上限字节对数；<c>0</c> 表示沿用帧内声明的窗口。</para></param>
    /// <exception cref="CompressionException">The dictionary or the window ceiling was rejected.
    /// <para>字典或窗口上限被拒绝。</para></exception>
    public ZstdDecoder(ReadOnlyMemory<byte> dictionary = default, int maximumWindowLog = 0)
    {
        _dictionary = dictionary;
        _maximumWindowLog = maximumWindowLog;
    }

    /// <summary>
    /// Decompresses one complete frame into a caller-owned span.
    /// <para>把一个完整帧解压到调用方持有的区间。</para>
    /// </summary>
    /// <param name="source">Frame bytes, treated as untrusted input.
    /// <para>帧字节，按不可信输入处理。</para></param>
    /// <param name="destination">Decode capacity; the frame must fit entirely.
    /// <para>解码容量；帧必须整体容纳。</para></param>
    /// <returns>Number of plaintext bytes written, which may be smaller than the destination length.
    /// <para>写入的明文字节数，可能小于目标区间长度。</para></returns>
    /// <exception cref="ObjectDisposedException">This decoder was released.
    /// <para>本解码器已被释放。</para></exception>
    /// <exception cref="CompressionException">The frame is invalid, truncated or exceeds the window ceiling.
    /// <para>帧非法、被截断或超出窗口上限。</para></exception>
    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return Rent().Unwrap(source, destination);
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD frame decompression failed. ZSTD 帧解压失败。", ex);
        }
    }

    /// <summary>
    /// Decompresses as much of one frame as the destination holds and reports the transfer counts, for callers
    /// that pump frames through bounded buffers.
    /// <para>尽可能多地把一帧解压到目标区间并报告传输计数，供以有界缓冲泵送帧的调用方使用。</para>
    /// </summary>
    /// <param name="source">Frame bytes available in this pump step.
    /// <para>本次泵送可用的帧字节。</para></param>
    /// <param name="destination">Plaintext buffer for this step.
    /// <para>本次的明文缓冲。</para></param>
    /// <param name="consumed">Bytes of <paramref name="source"/> accepted by the decoder.
    /// <para>解码器接受的 <paramref name="source"/> 字节数。</para></param>
    /// <param name="written">Plaintext bytes produced into <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 的明文字节数。</para></param>
    /// <returns>The zstd status for this step: done, needs more data, destination too small or invalid data.
    /// <para>本次的 zstd 状态：完成、需要更多数据、目标过小或数据非法。</para></returns>
    /// <exception cref="ObjectDisposedException">This decoder was released.
    /// <para>本解码器已被释放。</para></exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Rent().UnwrapStream(source, destination, out consumed, out written);
    }

    /// <summary>
    /// Resets the frame session so the next frame starts from a clean boundary, keeping the dictionary and the
    /// window ceiling.
    /// <para>重置帧会话，使下一帧从干净边界开始，并保留字典与窗口上限。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">This decoder was released.
    /// <para>本解码器已被释放。</para></exception>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _decompressor?.ResetStream();
    }

    /// <summary>Releases the native zstd decompression context.
    /// <para>释放原生 zstd 解压上下文。</para></summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _decompressor?.Dispose();
        _decompressor = null;
    }

    private Decompressor Rent()
    {
        if (_decompressor is not null)
        {
            return _decompressor;
        }

        var decompressor = new Decompressor();
        try
        {
            if (_maximumWindowLog > 0)
            {
                decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, _maximumWindowLog);
            }

            if (!_dictionary.IsEmpty)
            {
                decompressor.LoadDictionary(_dictionary.Span);
            }
        }
        catch (ZstdException ex)
        {
            decompressor.Dispose();
            throw new CompressionException("ZSTD frame decoder construction failed. ZSTD 帧解码器构建失败。", ex);
        }

        _decompressor = decompressor;
        return decompressor;
    }
}