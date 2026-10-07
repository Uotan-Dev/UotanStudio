using System.Buffers;
using ZstdSharp;
using ZstdSharp.Unsafe;

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
    /// AOSP gates external dictionaries with the payload properties <c>max_zstd_dict_raw_size</c> (byte ceiling
    /// for the dictionary a frame may use) and <c>max_zstd_dict_trust</c>; those keys live in
    /// <c>payload_properties.txt</c> and are parsed into <see cref="Payload.Models.PayloadProperties"/>.
    /// <see cref="PayloadExtractor"/> resolves a per-partition dictionary, rejects one above its configured ceiling
    /// and threads it into REPLACE_ZSTD decoding; callers without a manifest ceiling keep the 64 MiB bound here.
    /// <para>AOSP 通过载荷属性 <c>max_zstd_dict_raw_size</c>（一帧可用字典的字节上限）与 <c>max_zstd_dict_trust</c>
    /// 管控外部字典；这些键位于 <c>payload_properties.txt</c>，由 <see cref="Payload.Models.PayloadProperties"/> 解析。
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

    private static void DecodeCore(Stream input, Stream output, ReadOnlyMemory<byte>? dictionary, CancellationToken cancellationToken)
    {
        using var context = new ZstdDecodeContext(dictionary);
        context.Decode(input, output, cancellationToken);
    }

    /// <summary>
    /// Per-worker REPLACE_ZSTD decode state: the external dictionary bytes and the two pooled transfer
    /// buffers stay allocated for as many frames as the owning worker decodes instead of being
    /// built and released once per operation. A caller that decodes concurrently creates one instance per
    /// worker and disposes each of them; one instance is not thread-safe, and the decoder's window bound
    /// (26 bits, i.e. 64 MiB) is what a worker count should be budgeted against.
    /// <para>每个解码工作者的 REPLACE_ZSTD 解码状态：外部字典字节与两个池化传输缓冲会持续保留，
    /// 供该工作者解码尽可能多的帧，而不再每条操作重建与释放。并发解码的调用方应为每个工作者各创建一个
    /// 实例并逐个释放；单个实例并非线程安全，而解码器窗口上限（26 位，即 64 MiB）正是
    /// 收口工作者数量时应参照的每工作者字节预算。</para>
    /// </summary>
    internal sealed class ZstdDecodeContext : IDisposable
    {
        private readonly ReadOnlyMemory<byte>? _dictionary;
        private readonly byte[] _inputBuffer;
        private readonly byte[] _outputBuffer;
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
            using var decompressor = new Decompressor();
            decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaximumWindowLog);
            if (_dictionary is { } externalDictionary)
            {
                decompressor.LoadDictionary(externalDictionary.Span);
            }

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

                    var status = decompressor.UnwrapStream(
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
            catch (ZstdException ex)
            {
                throw new PartitionExtractionException(
                    "Zstd REPLACE frame exceeds the 64 MiB window limit or is invalid.", string.Empty, ex);
            }
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
            ArrayPool<byte>.Shared.Return(_inputBuffer);
            ArrayPool<byte>.Shared.Return(_outputBuffer);
        }
    }
}