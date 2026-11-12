using System.Buffers;
using System.IO.Compression;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Reusable Zstandard frame decoder that carries an optional external dictionary and a window ceiling, so formats
/// that decode one frame per block reuse a single engine context instead of building one per operation.
/// <para>可复用的 Zstandard 帧解码器，承载可选外部字典与窗口上限，使按块解码帧的格式复用同一引擎上下文，
/// 而不必每条操作新建。</para>
/// </summary>
/// <remarks>
/// The window ceiling is enforced by this type: the managed engine does not consult a configured ceiling while
/// decoding, so the declared window is read out of the frame header and refused before the engine sees the frame.
/// The pump path stages the leading bytes of a frame in a small buffer until the header is complete, so a frame
/// that arrives in chunks smaller than the header is still refused before any decode work. <see cref="Reset"/>
/// re-establishes a clean frame boundary and re-arms the ceiling check without losing the dictionary or the
/// ceiling. One instance is not thread-safe.
/// <para>窗口上限由本类型强制：托管引擎在解码过程中不查看配置的上限，因此先从帧头读出声明的窗口，并在引擎见到
/// 该帧之前拒绝超限帧。泵送路径会把一帧的前导字节暂存到小缓冲中直到帧头完整，因此以小于帧头的分块到达的帧
/// 依然会在任何解码工作开始前被拒绝。<see cref="Reset"/> 重建干净的帧边界并重新武装上限检查，但不丢失字典与
/// 上限。单个实例并非线程安全。</para>
/// </remarks>
public sealed class ZstdDecoder : IDisposable
{
    private const int HeaderBufferLength = ZstdFrameHeader.MaximumHeaderLength + 2;
    private readonly ReadOnlyMemory<byte> _dictionary;
    private readonly int _maximumWindowLog;
    private readonly byte[] _headerBuffer = new byte[HeaderBufferLength];
    private ZstandardDecoder? _decoder;
    private ZstandardDictionary? _dictionaryHandle;
    private int _headerLength;
    private bool _frameChecked;
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
        if (_maximumWindowLog > 0)
        {
            RefuseOversizedWindow(source);
        }

        _frameChecked = true;
        var decoder = Rent();
        decoder.Reset();
        var consumed = 0;
        var written = 0;
        while (true)
        {
            var status = decoder.Decompress(
                source[consumed..],
                destination[written..],
                out var stepConsumed,
                out var stepWritten);
            consumed += stepConsumed;
            written += stepWritten;
            switch (status)
            {
                case OperationStatus.Done:
                    if (consumed != source.Length)
                    {
                        throw new CompressionException(
                            "ZSTD frame decompression failed: the frame carries trailing data. ZSTD 帧解压失败：帧携带了附加数据。");
                    }

                    return written;
                case OperationStatus.NeedMoreData:
                    if (consumed == 0)
                    {
                        throw new CompressionException(
                            "ZSTD frame decompression failed: the frame is truncated. ZSTD 帧解压失败：帧被截断。");
                    }

                    break;
                case OperationStatus.DestinationTooSmall:
                    throw new CompressionException(
                        $"ZSTD frame decompression failed: the destination holds {destination.Length} bytes and is too small. "
                        + $"ZSTD 帧解压失败：目标区间仅有 {destination.Length} 字节，容量不足。");
                case OperationStatus.InvalidData:
                    throw new CompressionException(
                        "ZSTD frame decompression failed: the frame is malformed. ZSTD 帧解压失败：帧无效。");
                default:
                    throw new CompressionException(
                        $"Unexpected Zstd decoder status: {status}. Zstd 解码器返回了意外的状态 {status}。");
            }
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
    /// <exception cref="CompressionException">The frame declares a window above the ceiling configured at
    /// construction, or its header is invalid. <para>帧声明的窗口超出构造时配置的上限，或其帧头无效。</para></exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_maximumWindowLog > 0 && !_frameChecked)
        {
            return DecompressWithPrecheck(source, destination, out consumed, out written);
        }

        var status = Rent().Decompress(source, destination, out var stepConsumed, out var stepWritten);
        consumed = stepConsumed;
        written = stepWritten;
        return status;
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
        _decoder?.Reset();
        _headerLength = 0;
        _frameChecked = false;
    }

    /// <summary>Releases the zstd decoder context and any dictionary copy it holds.
    /// <para>释放 zstd 解码器上下文及其持有的字典副本。</para></summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _decoder?.Dispose();
        _decoder = null;
        _dictionaryHandle?.Dispose();
        _dictionaryHandle = null;
    }

    /// <summary>
    /// Validates the leading bytes of a frame against the window ceiling before the engine sees them, staging
    /// bytes across pump steps until the header is complete, then feeds the staged bytes and the rest of the step.
    /// <para>在引擎见到帧的前导字节之前按窗口上限校验它们，跨泵送步骤暂存字节直到帧头完整，然后把已暂存的字节
    /// 与本步剩余字节交给引擎。</para>
    /// </summary>
    /// <param name="source">Frame bytes available in this pump step.
    /// <para>本次泵送可用的帧字节。</para></param>
    /// <param name="destination">Plaintext buffer for this step. <para>本次的明文缓冲。</para></param>
    /// <param name="consumed">Bytes of <paramref name="source"/> accepted by the decoder.
    /// <para>解码器接受的 <paramref name="source"/> 字节数。</para></param>
    /// <param name="written">Plaintext bytes produced into <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 的明文字节数。</para></param>
    /// <returns>The zstd status for this step. <para>本次的 zstd 状态。</para></returns>
    /// <exception cref="CompressionException">The header exceeds the ceiling, cannot parse within the staging
    /// budget, or the engine rejected the staged bytes. <para>帧头超出上限、在暂存预算内无法解析，或引擎拒绝了
    /// 已暂存的字节。</para></exception>
    private OperationStatus DecompressWithPrecheck(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        consumed = 0;
        written = 0;
        var staged = Math.Min(source.Length, HeaderBufferLength - _headerLength);
        source[..staged].CopyTo(_headerBuffer.AsSpan(_headerLength));
        _headerLength += staged;
        var parse = ZstdFrameHeader.TryReadWindow(_headerBuffer.AsSpan(0, _headerLength), out var declaredWindow);
        if (parse == ZstdFrameHeader.ParseResult.NeedsMoreData)
        {
            if (_headerLength == HeaderBufferLength)
            {
                throw new CompressionException(
                    "ZSTD frame decompression failed: the frame header is malformed. ZSTD 帧解压失败：帧头无效。");
            }

            consumed = staged;
            return OperationStatus.NeedMoreData;
        }

        if (parse == ZstdFrameHeader.ParseResult.Invalid)
        {
            throw new CompressionException(
                "ZSTD frame decompression failed: the frame header is malformed. ZSTD 帧解压失败：帧头无效。");
        }

        var ceiling = _maximumWindowLog >= 64 ? long.MaxValue : 1L << _maximumWindowLog;
        if (declaredWindow > ceiling)
        {
            throw new CompressionException(
                $"ZSTD frame declares a {declaredWindow} byte window above the {ceiling} byte ceiling. "
                + $"ZSTD 帧声明的窗口为 {declaredWindow} 字节，超出 {ceiling} 字节的上限。");
        }

        _frameChecked = true;
        var decoder = Rent();
        var status = decoder.Decompress(_headerBuffer.AsSpan(0, _headerLength), destination, out var stagedConsumed, out written);
        if (stagedConsumed != _headerLength)
        {
            throw new CompressionException(
                "ZSTD frame decompression failed: the frame header was rejected. ZSTD 帧解压失败：帧头被拒绝。");
        }

        consumed = staged;
        if (status == OperationStatus.Done && consumed == source.Length)
        {
            return status;
        }

        status = decoder.Decompress(source[consumed..], destination[written..], out var stepConsumed, out var stepWritten);
        consumed += stepConsumed;
        written += stepWritten;
        return status;
    }

    /// <summary>
    /// Reads the window the frame declares out of its header and refuses the frame when that window exceeds the
    /// configured ceiling, leaving the destination untouched.
    /// <para>从帧头读出该帧声明的窗口，并在其超出配置上限时拒绝该帧，目标缓冲区保持未被写入。</para>
    /// </summary>
    /// <param name="source">Complete frame bytes to inspect. <para>待检查的完整帧字节。</para></param>
    /// <exception cref="CompressionException">The declared window is larger than the ceiling.
    /// <para>所声明的窗口大于上限。</para></exception>
    private void RefuseOversizedWindow(ReadOnlySpan<byte> source)
    {
        if (ZstdFrameHeader.TryReadWindow(source, out var declaredWindow) != ZstdFrameHeader.ParseResult.Parsed)
        {
            return;
        }

        var ceiling = _maximumWindowLog >= 64 ? long.MaxValue : 1L << _maximumWindowLog;
        if (declaredWindow > ceiling)
        {
            throw new CompressionException(
                $"ZSTD frame declares a {declaredWindow} byte window above the {ceiling} byte ceiling. "
                + $"ZSTD 帧声明的窗口为 {declaredWindow} 字节，超出 {ceiling} 字节的上限。");
        }
    }

    /// <summary>
    /// Builds the engine decoder owned by this instance, registering the dictionary once, so later frames reuse it.
    /// <para>构建本实例持有的引擎解码器并一次性登记字典，供后续帧复用。</para>
    /// </summary>
    /// <returns>The engine decoder this instance owns. <para>由本实例持有的引擎解码器。</para></returns>
    /// <exception cref="CompressionException">The engine rejected the dictionary or the ceiling.
    /// <para>引擎拒绝了字典或上限。</para></exception>
    private ZstandardDecoder Rent()
    {
        if (_decoder is not null)
        {
            return _decoder;
        }

        try
        {
            if (!_dictionary.IsEmpty)
            {
                _dictionaryHandle = ZstandardDictionary.Create(_dictionary.Span);
                _decoder = _maximumWindowLog > 0
                    ? new ZstandardDecoder(_dictionaryHandle, _maximumWindowLog)
                    : new ZstandardDecoder(_dictionaryHandle);
                return _decoder;
            }

            _decoder = _maximumWindowLog > 0 ? new ZstandardDecoder(_maximumWindowLog) : new ZstandardDecoder();
            return _decoder;
        }
        catch (Exception ex)
        {
            _dictionaryHandle?.Dispose();
            _dictionaryHandle = null;
            throw new CompressionException("ZSTD frame decoder construction failed. ZSTD 帧解码器构建失败。", ex);
        }
    }
}