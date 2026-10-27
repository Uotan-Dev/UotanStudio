using System.Buffers;
using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Zstd;

namespace UotanStudio.Firmware.OtaDelta.Payload;

internal static class ZstdReplaceDecoder
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumWindowLog = 26;
    private const long MaximumDictionaryBytes = 64L * 1024 * 1024;

    internal static void Decode(Stream input, Stream output, CancellationToken cancellationToken)
        => DecodeCore(input, output, null, cancellationToken);

    /// <summary>
    /// Decodes a Zstandard REPLACE frame compressed against an external raw content dictionary.
    /// <para>解压基于外部原始内容字典压缩的 Zstandard REPLACE 帧。</para>
    /// </summary>
    /// <param name="input">Frame bytes to decode. <para>待解压的帧字节。</para></param>
    /// <param name="output">Plaintext sink. <para>明文字节接收流。</para></param>
    /// <param name="dictionary">
    /// Exact raw dictionary bytes registered with the compressor; empty reproduces the dictionary-free path.
    /// <para>与压缩器登记的完全一致的原始字典字节；为空时复现无字典路径。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels between buffer refills. <para>在缓冲区重填之间响应取消。</para></param>
    /// <remarks>
    /// External dictionaries are gated by the payload properties <c>max_zstd_dict_raw_size</c> (byte ceiling
    /// for the dictionary a frame may use) and <c>max_zstd_dict_trust</c>; those keys live in
    /// <c>payload_properties.txt</c> and are parsed into <see cref="Models.PayloadProperties"/>.
    /// <see cref="PayloadExtractor"/> resolves a per-partition dictionary, rejects one above its configured ceiling
    /// and threads it into REPLACE_ZSTD decoding; callers without a manifest ceiling keep the 64 MiB bound here.
    /// <para>外部字典由载荷属性 <c>max_zstd_dict_raw_size</c>（一帧可用字典的字节上限）与 <c>max_zstd_dict_trust</c>
    /// 管控；这些键位于 <c>payload_properties.txt</c>，由 <see cref="Models.PayloadProperties"/> 解析。
    /// <see cref="PayloadExtractor"/> 按分区解析字典、拒绝超出所配置上限的字典，并把字典贯穿到 REPLACE_ZSTD 解码；
    /// 未声明清单上限的调用方沿用本解码器的 64 MiB 上界。</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dictionary"/> exceeds the 64 MiB ceiling. <para><paramref name="dictionary"/> 超过 64 MiB 上限。</para>
    /// </exception>
    internal static void Decode(Stream input, Stream output, ReadOnlyMemory<byte> dictionary, CancellationToken cancellationToken)
    {
        if (dictionary.Length > MaximumDictionaryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(dictionary), "Zstd external dictionary exceeds the 64 MiB ceiling.");
        }

        DecodeCore(input, output, dictionary.IsEmpty ? null : dictionary, cancellationToken);
    }

    /// <summary>
    /// Decodes one Zstandard REPLACE frame through a caller-owned <see cref="ZstdDecodeContext"/>, so the decoder
    /// state and the transfer buffers are reused across frames instead of being rebuilt per operation.
    /// <para>通过调用方持有的 <see cref="ZstdDecodeContext"/> 解码一个 Zstandard REPLACE 帧，使解码器状态与传输缓冲
    /// 跨帧复用，而不必每条操作重建。</para>
    /// </summary>
    /// <param name="input">Frame bytes to decode. <para>待解压的帧字节。</para></param>
    /// <param name="output">Plaintext sink. <para>明文字节接收流。</para></param>
    /// <param name="context">
    /// Worker-owned decode state created with the dictionary this frame was compressed against; it must be used by
    /// exactly one thread at a time. <para>工作者持有的解码状态，创建时登记的字典必须与该帧压缩所用字典一致；
    /// 任一时刻只能被一个线程使用。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels between buffer refills. <para>在缓冲区重填之间响应取消。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.
    /// <para><paramref name="context"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The context's dictionary exceeds the 64 MiB ceiling. <para>上下文所持字典超过 64 MiB 上限。</para>
    /// </exception>
    internal static void Decode(Stream input, Stream output, ZstdDecodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Dictionary.Length > MaximumDictionaryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(context), "Zstd external dictionary exceeds the 64 MiB ceiling.");
        }

        context.Decode(input, output, cancellationToken);
    }

    private static void DecodeCore(Stream input, Stream output, ReadOnlyMemory<byte>? dictionary, CancellationToken cancellationToken)
    {
        using var context = new ZstdDecodeContext(dictionary);
        context.Decode(input, output, cancellationToken);
    }

    /// <summary>
    /// Per-worker REPLACE_ZSTD decode state: the external dictionary bytes, the two pooled transfer buffers and the
    /// reusable <see cref="ZstdDecoder"/> stay allocated for as many frames as the owning worker decodes instead of
    /// being built and released once per operation. A caller that decodes concurrently creates one instance per
    /// worker and disposes each of them; one instance is not thread-safe, and the decoder's window bound
    /// (26 bits, i.e. 64 MiB) is what a worker count should be budgeted against.
    /// <para>每个解码工作者的 REPLACE_ZSTD 解码状态：外部字典字节、两个池化传输缓冲以及可复用的
    /// <see cref="ZstdDecoder"/> 会持续保留，供该工作者解码尽可能多的帧，而不再每条操作重建与释放。
    /// 并发解码的调用方应为每个工作者各创建一个实例并逐个释放；单个实例并非线程安全，而解码器窗口上限
    /// （26 位，即 64 MiB）正是收口工作者数量时应参照的每工作者字节预算。</para>
    /// </summary>
    /// <remarks>
    /// The <see cref="ZstdDecoder"/> is created on the first frame rather than in the constructor, so a worker that
    /// never meets a REPLACE_ZSTD operation never pays for a decoder window. Every frame begins with
    /// <see cref="ZstdDecoder.Reset"/> followed by the window bound, which is what makes reuse safe: a frame
    /// that was abandoned part way through by cancellation, truncation or invalid data leaves the session mid-flight,
    /// and the reset at the start of the next frame is what restores a clean boundary. <see cref="ZstdDecoder"/>
    /// holds no state shared with other instances, so correctness depends entirely on the caller keeping one
    /// instance to one thread at a time.
    /// <para><see cref="ZstdDecoder"/> 在首帧才创建而不是在构造函数中创建，因此从未遇到 REPLACE_ZSTD 操作的工作者
    /// 不会为解码器窗口付出代价。每一帧都以 <see cref="ZstdDecoder.Reset"/> 加窗口上限设置开始，这正是复用
    /// 之所以安全的原因：被取消、截断或非法数据中途放弃的帧会把会话留在半途，而下一帧开头的重置正是恢复干净
    /// 状态边界的操作。<see cref="ZstdDecoder"/> 不持有与其他实例共享的状态，因此正确性完全取决于调用方保证
    /// 任一时刻一个实例只属于一个线程。</para>
    /// </remarks>
    internal sealed class ZstdDecodeContext : IDisposable
    {
        private readonly ReadOnlyMemory<byte>? _dictionary;
        private readonly byte[] _inputBuffer;
        private readonly byte[] _outputBuffer;
        private ZstdDecoder? _decompressor;
        private bool _disposed;

        /// <summary>
        /// Records the dictionary bytes and rents the transfer buffers for one worker.
        /// <para>登记字典字节并为单个工作者租借传输缓冲。</para>
        /// </summary>
        /// <param name="dictionary">
        /// External raw dictionary bytes, or <see langword="null"/> for dictionary-free frames.
        /// <para>外部原始字典字节；无字典帧时为 <see langword="null"/>。</para>
        /// </param>
        internal ZstdDecodeContext(ReadOnlyMemory<byte>? dictionary)
        {
            _dictionary = dictionary is { Length: > 0 } source ? source : null;
            _inputBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            _outputBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        }

        /// <summary>
        /// Raw dictionary bytes this context decodes against, empty when the frames carry no external dictionary.
        /// <para>本上下文据以解码的原始字典字节；帧不带外部字典时为空。</para>
        /// </summary>
        /// <value>
        /// The dictionary recorded at construction, or an empty span when no dictionary was supplied.
        /// <para>构造时登记的字典；未提供字典时为空区间。</para>
        /// </value>
        internal ReadOnlyMemory<byte> Dictionary => _dictionary ?? ReadOnlyMemory<byte>.Empty;

        /// <summary>Decodes one REPLACE_ZSTD frame from <paramref name="input"/> into <paramref name="output"/>.
        /// <para>从 <paramref name="input"/> 解码一个 REPLACE_ZSTD 帧并写入 <paramref name="output"/>。</para></summary>
        /// <param name="input">Frame bytes to decode. <para>待解压的帧字节。</para></param>
        /// <param name="output">Plaintext sink. <para>明文字节接收流。</para></param>
        /// <param name="cancellationToken">Cancels between buffer refills. <para>在缓冲区重填之间响应取消。</para></param>
        /// <exception cref="ObjectDisposedException">This context was released. <para>本上下文已被释放。</para></exception>
        /// <exception cref="PartitionExtractionException">
        /// The frame is truncated, invalid for the supplied dictionary, or accepts trailing data.
        /// <para>帧被截断、对给定字典非法，或接受了尾部附加数据。</para>
        /// </exception>
        internal void Decode(Stream input, Stream output, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var decompressor = _decompressor ??= CreateDecompressor();

            var inputOffset = 0;
            var inputLength = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (inputOffset == inputLength)
                    {
                        inputLength = input.Read(_inputBuffer, 0, BufferSize);
                        inputOffset = 0;
                        if (inputLength == 0)
                        {
                            throw new PartitionExtractionException("Zstd REPLACE frame is truncated.", string.Empty);
                        }
                    }

                    var status = decompressor.Decompress(
                        _inputBuffer.AsSpan(inputOffset, inputLength - inputOffset),
                        _outputBuffer,
                        out var consumed,
                        out var written);
                    inputOffset = checked(inputOffset + consumed);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (written > 0)
                    {
                        output.Write(_outputBuffer, 0, written);
                    }

                    switch (status)
                    {
                        case OperationStatus.Done:
                            {
                                if (inputOffset != inputLength || input.ReadByte() != -1)
                                {
                                    throw new PartitionExtractionException(
                                        "Zstd REPLACE accepts exactly one frame without trailing data.", string.Empty);
                                }

                                return;
                            }
                        case OperationStatus.NeedMoreData:
                        case OperationStatus.DestinationTooSmall:
                            break;
                        case OperationStatus.InvalidData:
                            throw new PartitionExtractionException(
                                _dictionary is null
                                    ? "Invalid Zstd REPLACE frame. External dictionaries are not supported."
                                    : "Invalid Zstd REPLACE frame for the supplied external dictionary.", string.Empty);
                        default:
                            throw new PartitionExtractionException($"Unexpected Zstd decoder status: {status}.", string.Empty);
                    }
                }
            }
            catch (CompressionException ex)
            {
                ReleaseDecompressor();
                throw new PartitionExtractionException(
                    "Zstd REPLACE frame exceeds the 64 MiB window limit or is invalid.", string.Empty, ex);
            }
        }

        /// <summary>
        /// Builds the decoder owned by this worker and registers the dictionary once, so later frames reuse it.
        /// <para>构建本工作者持有的解码器并一次性登记字典，供后续帧复用。</para>
        /// </summary>
        /// <returns>
        /// A live decoder the context disposes. <para>由本上下文释放的活跃解码器。</para>
        /// </returns>
        private ZstdDecoder CreateDecompressor()
        {
            if (_dictionary is { } externalDictionary)
            {
                return new ZstdDecoder(externalDictionary, MaximumWindowLog);
            }

            return new ZstdDecoder(ReadOnlyMemory<byte>.Empty, MaximumWindowLog);
        }

        /// <summary>
        /// Drops the decoder so the next frame rebuilds it from a known-clean state.
        /// <para>丢弃解码器，使下一帧从已知的干净状态重新构建它。</para>
        /// </summary>
        private void ReleaseDecompressor()
        {
            _decompressor?.Dispose();
            _decompressor = null;
        }

        /// <summary>Releases the codec state and returns the pooled transfer buffers.
        /// <para>释放编解码状态并把池化传输缓冲归还池中。</para></summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseDecompressor();
            ArrayPool<byte>.Shared.Return(_inputBuffer);
            ArrayPool<byte>.Shared.Return(_outputBuffer);
        }
    }
}