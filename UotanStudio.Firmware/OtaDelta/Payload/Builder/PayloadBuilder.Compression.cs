using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    private static void CompressStream(Stream inputStream, Stream outputStream, CompressionType compressionType)
    {
        var format = compressionType switch
        {
            CompressionType.None => Compress.CompressionFormat.None,
            CompressionType.BZip2 => Compress.CompressionFormat.Bzip2,
            CompressionType.Xz => Compress.CompressionFormat.Xz,
            CompressionType.Zstd => throw new ArgumentException("Zstd REPLACE compression requires the dictionary-aware overload.", nameof(compressionType)),
            _ => throw new PayloadGenerationException("Unsupported payload REPLACE compression.")
        };
        Compress.CompressionService.Compress(inputStream, outputStream, format,
            new Compress.CompressionOptions
            {
                MaxDegreeOfParallelism = 1,
                DictionarySize = 1u << 20
            });
    }

    /// <summary>
    /// Creates one Zstandard context prepared for payload REPLACE frames: a content checksum is enabled and the raw
    /// dictionary, when supplied, is copied into the context. The context is single-threaded and holds no shared
    /// state, so exactly one instance belongs to one worker at a time and is never handed to another slot.
    /// Reusing it over many frames preserves bytes because every frame call resets the compression session, keeps
    /// the configured parameters and re-inserts the same dictionary before encoding. That equivalence depends on the
    /// selected strategy not using the row match finder: Zstandard advances an internal hash salt on each session
    /// reset once the strategy is <c>greedy</c>, <c>lazy</c> or <c>lazy2</c>, and a salted reused context may then
    /// emit different bytes than a freshly created one. Payload builds pin level <c>3</c>, whose strategy is
    /// <c>dfast</c>, so reused and freshly created contexts agree there.
    /// <para>创建为载荷 REPLACE 帧准备就绪的单个 Zstandard 上下文：启用内容校验和，并在提供原始字典时把字典复制进上下文。
    /// 上下文为单线程且不持有共享状态，因此同一时刻只属于一个工作者，绝不交给其他槽位。跨帧复用它可保持字节不变，因为
    /// 每次帧调用都会重置压缩会话、保留已配置的参数，并在编码前重新插入同一字典。该等价性要求所选策略不使用行匹配器：
    /// 一旦策略为 <c>greedy</c>、<c>lazy</c> 或 <c>lazy2</c>，Zstandard 会在每次会话重置时推进内部哈希盐值，此时复用的上下文
    /// 可能输出与新建上下文不同的字节。载荷构建固定使用级别 <c>3</c>，其策略为 <c>dfast</c>，因此复用与新建上下文在该级别
    /// 结果一致。</para>
    /// </summary>
    /// <param name="dictionary">
    /// Exact raw dictionary bytes, or empty for a dictionary-free frame.
    /// <para>完全一致的原始字典字节，空则表示无字典帧。</para>
    /// </param>
    /// <param name="level">
    /// Zstandard compression level; the payload default is <c>3</c>. <para>Zstandard 压缩级别；载荷默认 <c>3</c>。</para>
    /// </param>
    /// <returns>
    /// A live context the caller must dispose. <para>需要由调用方释放的活跃上下文。</para>
    /// </returns>
    /// <exception cref="PayloadGenerationException">
    /// The compressor rejected the dictionary or the context parameters. <para>压缩器拒绝字典或上下文参数。</para>
    /// </exception>
    internal static Compressor CreateZstdFrameContext(ReadOnlyMemory<byte> dictionary, int level = 3)
    {
        var compressor = new Compressor(level);
        try
        {
            compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            if (!dictionary.IsEmpty)
            {
                compressor.LoadDictionary(dictionary.Span);
            }

            return compressor;
        }
        catch (ZstdException ex)
        {
            compressor.Dispose();
            throw new PayloadGenerationException($"Zstd REPLACE compression failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Compresses one chunk into <paramref name="destination"/> with a caller-owned context, so no frame is
    /// materialised in a temporary buffer on the way to the payload's data blob.
    /// <para>使用调用方持有的上下文把单个块压缩到 <paramref name="destination"/>，使帧数据不再经过临时缓冲
    /// 中转就进入载荷的数据段。</para>
    /// </summary>
    /// <param name="compressor">
    /// Context from <see cref="CreateZstdFrameContext"/>; it must not be shared across threads.
    /// <para>由 <see cref="CreateZstdFrameContext"/> 创建的上下文；不得跨线程共享。</para>
    /// </param>
    /// <param name="source">Chunk plaintext. <para>块明文。</para></param>
    /// <param name="destination">
    /// Destination wide enough for <see cref="Compressor.GetCompressBound(int)"/> bytes of <paramref name="source"/>.
    /// <para>容量足以容纳 <paramref name="source"/> 的 <see cref="Compressor.GetCompressBound(int)"/> 字节的目标区间。</para>
    /// </param>
    /// <returns>
    /// The number of frame bytes written at the start of <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 起始处的帧字节数。</para>
    /// </returns>
    /// <exception cref="PayloadGenerationException">
    /// The frame did not fit <paramref name="destination"/> or the compressor failed.
    /// <para>帧无法放入 <paramref name="destination"/>，或压缩器出错。</para>
    /// </exception>
    internal static int CompressZstdFrame(Compressor compressor, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        try
        {
            return compressor.Wrap(source, destination);
        }
        catch (ZstdException ex)
        {
            throw new PayloadGenerationException($"Zstd REPLACE compression failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Compresses one REPLACE chunk with Zstandard, optionally against an external raw content dictionary.
    /// Frames carry a content checksum so a wrong or missing dictionary fails loudly instead of
    /// silently yielding garbage. This helper creates a fresh context per call; compression loops that feed
    /// many chunks reuse a context obtained from <see cref="CreateZstdFrameContext"/> instead.
    /// <para>使用 Zstandard 压缩单个 REPLACE 块，可选地带外部原始内容字典。帧内附带内容校验和，
    /// 使字典缺失或错误时明确失败而不是静默产出错误数据。本辅助方法每次调用创建新上下文；需要连续处理多个块的
    /// 压缩循环应改为复用 <see cref="CreateZstdFrameContext"/> 返回的上下文。</para>
    /// </summary>
    /// <param name="inputStream">Chunk plaintext, read to the end. <para>块明文，读取到末尾。</para></param>
    /// <param name="outputStream">Single-frame zstd output sink. <para>单帧 zstd 输出接收流。</para></param>
    /// <param name="dictionary">
    /// Exact raw dictionary bytes, or empty for a dictionary-free frame. Payloads that use a non-empty
    /// dictionary are gated by the <c>max_zstd_dict_raw_size</c> property in
    /// <c>payload_properties.txt</c>; callers must record that key when publishing such a payload.
    /// <para>完全一致的原始字典字节，空则表示无字典帧。使用非空字典的载荷由
    /// <c>payload_properties.txt</c> 中的 <c>max_zstd_dict_raw_size</c> 属性管控；发布此类载荷的调用方必须记录该键。</para>
    /// </param>
    /// <param name="level">
    /// Zstandard compression level; the payload default is <c>3</c>. <para>Zstandard 压缩级别；载荷默认 <c>3</c>。</para>
    /// </param>
    /// <exception cref="PayloadGenerationException">
    /// The compressor rejected the dictionary or the frame could not be produced. <para>压缩器拒绝字典或无法生成帧。</para>
    /// </exception>
    internal static void CompressZstdChunk(Stream inputStream, Stream outputStream, ReadOnlyMemory<byte> dictionary, int level = 3)
    {
        using var buffer = new MemoryStream();
        inputStream.CopyTo(buffer);
        var plain = buffer.ToArray();
        using var compressor = CreateZstdFrameContext(dictionary, level);
        var frame = new byte[Compressor.GetCompressBound(plain.Length)];
        var written = CompressZstdFrame(compressor, plain, frame);
        outputStream.Write(frame, 0, written);
    }
}