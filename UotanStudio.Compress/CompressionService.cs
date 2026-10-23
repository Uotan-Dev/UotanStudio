using UotanStudio.Compress.Compressors;
using UotanStudio.Compress.Internal;

namespace UotanStudio.Compress;

/// <summary>
/// Entry point that routes a <see cref="CompressionFormat"/> to the matching codec, offering whole-buffer and streaming conversions over the same format registry.
/// <para>按 <see cref="CompressionFormat"/> 分发到对应编解码器的入口，在同一份格式注册表上提供整块缓冲与流式两种转换。</para>
/// </summary>
/// <example>
/// <code>
/// using var source = File.OpenRead(payloadPath);
/// using var target = File.Create(extractPath);
/// CompressionService.Decompress(source, target, CompressionFormat.Xz, 512L * 1024 * 1024, cancellationToken);
/// </code>
/// </example>
public static class CompressionService
{
    /// <summary>Compresses data.<para>压缩数据。</para></summary>
    /// <param name="data">Plaintext buffer to compress; an empty array produces the codec's empty-stream encoding.
    /// <para>待压缩的明文字节；空数组会生成该编解码器的空流编码。</para></param>
    /// <param name="format">Target container, resolved through the format registry.
    /// <para>目标容器格式，通过格式注册表解析。</para></param>
    /// <param name="options">Optional level, dictionary size and parallelism tuning; <see langword="null"/> keeps every codec default.
    /// <para>可选的等级、字典大小与并行度调优；为 <see langword="null"/> 时全部采用编解码器默认值。</para></param>
    /// <returns>The compressed bytes. For <see cref="CompressionFormat.None"/> the input array is returned unchanged and by reference.
    /// <para>压缩后的字节；<see cref="CompressionFormat.None"/> 按引用原样返回输入数组。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is unknown or cannot compress, for example <see cref="CompressionFormat.Zopfli"/>.
    /// <para><paramref name="format"/> 未知或不支持压缩，例如 <see cref="CompressionFormat.Zopfli"/>。</para></exception>
    /// <exception cref="CompressionException">The codec failed while compressing; the failure is kept as <see cref="Exception.InnerException"/>.
    /// <para>编解码器压缩失败；原始异常保留在 <see cref="Exception.InnerException"/> 中。</para></exception>
    public static byte[] Compress(byte[] data, CompressionFormat format, CompressionOptions? options = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ValidateForCompression(format);
        return CompressCore(data, format, options);
    }

    /// <summary>Decompresses data.<para>解压数据。</para></summary>
    /// <param name="data">Complete compressed payload, treated as untrusted input.
    /// <para>完整的压缩数据，按不可信输入处理。</para></param>
    /// <param name="format">Container the payload is decoded with.<para>用于解码该数据的容器格式。</para></param>
    /// <returns>The decompressed bytes, whose size equals whatever the payload expands to; no output cap is applied. For <see cref="CompressionFormat.None"/> the input array is returned unchanged and by reference.
    /// <para>解压后的字节，大小完全取决于数据的展开结果，不施加输出上限；<see cref="CompressionFormat.None"/> 按引用原样返回输入数组。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is unknown or cannot decompress.
    /// <para><paramref name="format"/> 未知或不支持解压。</para></exception>
    /// <exception cref="CompressionException">The payload is malformed, truncated or corrupt for the selected format.
    /// <para>数据相对于所选格式损坏、截断或格式非法。</para></exception>
    public static byte[] Decompress(byte[] data, CompressionFormat format)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ValidateForDecompression(format);
        return DecompressCore(data, format);
    }


    /// <summary>Streaming compression.<para>流式压缩。</para></summary>
    /// <param name="input">Readable source positioned where the plaintext starts; it is read to end of stream and never closed by this call.
    /// <para>定位在明文起始处的可读源流，读取到流末尾且不会被本调用关闭。</para></param>
    /// <param name="output">Writable destination for the encoded bytes; it is flushed and finalized by the codec but left open for the caller.
    /// <para>编码字节的写入目标；由编解码器刷新并写出结尾结构，但保持打开交由调用方处理。</para></param>
    /// <param name="format">Target container. <see cref="CompressionFormat.None"/> copies the input verbatim; gzip, zlib, deflate, Brotli, bzip2 and LZ4 frame go through their streaming encoders, while XZ, LZMA, Zstandard, LZ4 legacy and LZOP use their dedicated block-frame writers; the XZ writer writes in place when the destination is readable and positioned at its end, and spools its payload to a temporary file otherwise.
    /// <para>目标容器格式。<see cref="CompressionFormat.None"/> 原样复制输入；gzip、zlib、deflate、Brotli、bzip2 与 LZ4 帧走各自的流式编码器，而 XZ、LZMA、Zstandard、LZ4 传统格式与 LZOP 使用专用的块帧写出路径；XZ 写出器在目标可读且定位在末尾时就地写出，否则把载荷暂存到临时文件。</para></param>
    /// <param name="options">Optional tuning; <see langword="null"/> keeps every codec default.
    /// <para>可选调优参数；为 <see langword="null"/> 时全部采用编解码器默认值。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is unknown or cannot compress.
    /// <para><paramref name="format"/> 未知或不支持压缩。</para></exception>
    /// <exception cref="CompressionException">The format has no streaming or dedicated encoder, or the codec failed; the XZ path also reports temporary-file, in-place move and I/O failures this way.
    /// <para>该格式没有流式或专用编码器，或编解码器失败；XZ 路径的临时文件、就地移动与 I/O 故障也以此异常上报。</para></exception>
    public static void Compress(Stream input, Stream output, CompressionFormat format, CompressionOptions? options = null)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        ValidateForCompression(format);

        if (format == CompressionFormat.None)
        {
            input.CopyTo(output);
            return;
        }

        var info = CompressionFormats.GetInfo(format)!;
        if (info.IsStreaming)
        {
            CompressStreaming(input, output, format, options);
            return;
        }

        switch (format)
        {
            case CompressionFormat.Xz:
                XzCompressor.Compress(input, output,
                    options?.DictionarySize ?? Lzma2Encoder.DefaultDictionarySize,
                    maxDegreeOfParallelism: options?.MaxDegreeOfParallelism);
                return;
            case CompressionFormat.Lzma:
                LzmaCompressor.Compress(input, output);
                return;
            case CompressionFormat.Zstd:
                ZstdCompressor.Compress(input, output, options);
                return;
            case CompressionFormat.Lz4Legacy:
                Lz4Compressor.CompressLegacy(input, output, options);
                return;
            case CompressionFormat.Lzop:
                LzopCompressor.Compress(input, output, options?.MaxDegreeOfParallelism);
                return;
            case CompressionFormat.None:
                break;
            case CompressionFormat.Gzip:
                break;
            case CompressionFormat.Zlib:
                break;
            case CompressionFormat.Deflate:
                break;
            case CompressionFormat.Brotli:
                break;
            case CompressionFormat.Lz4:
                break;
            case CompressionFormat.Bzip2:
                break;
            case CompressionFormat.Zopfli:
                break;
            default:
                throw new CompressionException($"Streaming compression is not supported for format: {format}");
        }
    }

    /// <summary>Streaming decompression.<para>流式解压。</para></summary>
    /// <param name="input">Readable compressed payload positioned at its first byte; it is not closed by this call.
    /// <para>定位在压缩数据起始处的可读流，不会被本调用关闭。</para></param>
    /// <param name="output">Writable destination for the decoded bytes; it is not closed and not flushed beyond what the codec performs.
    /// <para>解码字节的写入目标，不会被本调用关闭，除编解码器自身的写入外不做额外刷新。</para></param>
    /// <param name="format">Container the payload is decoded with.<para>用于解码该数据的容器格式。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is unknown or cannot decompress.
    /// <para><paramref name="format"/> 未知或不支持解压。</para></exception>
    /// <exception cref="IOException">A write into <paramref name="output"/> failed; this overload puts no cap on the decoded size.
    /// <para>写入 <paramref name="output"/> 失败；本重载不限制解压后大小。</para></exception>
    /// <exception cref="CompressionException">The payload is malformed or the format has no streaming decoder.
    /// <para>数据损坏，或该格式没有流式解码器。</para></exception>
    public static void Decompress(Stream input, Stream output, CompressionFormat format)
        => Decompress(input, output, format, long.MaxValue, CancellationToken.None);

    /// <summary>Decompresses a stream with a unified output limit and cancellation support.
    /// <para>使用统一输出上限和取消支持进行流式解压。</para></summary>
    /// <param name="input">Readable compressed payload positioned at its first byte; the caller keeps ownership and must close it.
    /// <para>定位在压缩数据起始处的可读流；所有权属于调用方，需由调用方关闭。</para></param>
    /// <param name="output">Writable destination wrapped internally in a counting stream, so writes are checked before they reach it; it is left open.
    /// <para>写入目标在内部被计数流包装，写入先校验再落地；该流保持打开。</para></param>
    /// <param name="format">Container the payload is decoded with. <see cref="CompressionFormat.None"/> copies the input unchanged and <see cref="CompressionFormat.Zopfli"/> decodes through the gzip reader.
    /// <para>用于解码的容器格式：<see cref="CompressionFormat.None"/> 原样复制输入，<see cref="CompressionFormat.Zopfli"/> 通过 gzip 读取器解码。</para></param>
    /// <param name="maximumOutputBytes">Inclusive cap on total decoded bytes, checked before each write so the limit is never overshot; pass <see cref="long.MaxValue"/> to leave the output effectively unbounded.
    /// <para>解压输出总字节数的含上限，在每次写入前检查，因此不会超出限制；传入 <see cref="long.MaxValue"/> 表示实质上不限大小。</para></param>
    /// <param name="cancellationToken">Token observed before the first byte is read and on every write through the counting wrapper; there is no asynchronous I/O here, so cancellation surfaces as a throw rather than a returned task.
    /// <para>在读取首字节前以及每次通过计数包装写入时检查；本方法不含异步 I/O，取消以抛出异常的方式体现。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is negative.
    /// <para><paramref name="maximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already signalled, or becomes signalled during a write.
    /// <para><paramref name="cancellationToken"/> 在调用前已触发，或在写入过程中触发。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is unknown or cannot decompress.
    /// <para><paramref name="format"/> 未知或不支持解压。</para></exception>
    /// <exception cref="IOException"><paramref name="maximumOutputBytes"/> would be exceeded, or the underlying write failed.
    /// <para>即将超过 <paramref name="maximumOutputBytes"/>，或底层写入失败。</para></exception>
    /// <exception cref="CompressionException">The payload is malformed for <paramref name="format"/> or the format has no streaming decoder.
    /// <para>数据相对于 <paramref name="format"/> 损坏，或该格式没有流式解码器。</para></exception>
    public static void Decompress(Stream input, Stream output, CompressionFormat format, long maximumOutputBytes, CancellationToken cancellationToken = default)
        => DecompressWithOptions(input, output, format, maximumOutputBytes, null, cancellationToken);

    /// <summary>Decompresses a stream with a unified output limit, cancellation support and optional tuning.
    /// <para>使用统一输出上限、取消支持与可选调优进行流式解压。</para></summary>
    /// <param name="input">Readable compressed payload positioned at its first byte; the caller keeps ownership and must close it.
    /// <para>定位在压缩数据起始处的可读流；所有权属于调用方，需由调用方关闭。</para></param>
    /// <param name="output">Writable destination wrapped internally in a counting stream, so writes are checked before they reach it; it is left open.
    /// <para>写入目标在内部被计数流包装，写入先校验再落地；该流保持打开。</para></param>
    /// <param name="format">Container the payload is decoded with.<para>用于解码的容器格式。</para></param>
    /// <param name="maximumOutputBytes">Inclusive cap on total decoded bytes, checked before each write so the limit is never overshot.
    /// <para>解压输出总字节数的含上限，在每次写入前检查，因此不会超出限制。</para></param>
    /// <param name="options">Optional tuning; <see langword="null"/> keeps every codec default. Only <see cref="CompressionOptions.MaxDegreeOfParallelism"/> is read on this path, by the block-oriented LZOP decoder that can decode independent blocks concurrently while still emitting them in block order.
    /// <para>可选调优参数；为 <see langword="null"/> 时全部采用编解码器默认值。本路径只读取 <see cref="CompressionOptions.MaxDegreeOfParallelism"/>，供按块组织的 LZOP 解码器并发解码相互独立的块，同时仍按块顺序写出。</para></param>
    /// <param name="cancellationToken">Token observed before the first byte is read and on every write through the counting wrapper.
    /// <para>在读取首字节前以及每次通过计数包装写入时检查。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is negative.
    /// <para><paramref name="maximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already signalled, or becomes signalled during a write.
    /// <para><paramref name="cancellationToken"/> 在调用前已触发，或在写入过程中触发。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is unknown or cannot decompress.
    /// <para><paramref name="format"/> 未知或不支持解压。</para></exception>
    /// <exception cref="IOException"><paramref name="maximumOutputBytes"/> would be exceeded, or the underlying write failed.
    /// <para>即将超过 <paramref name="maximumOutputBytes"/>，或底层写入失败。</para></exception>
    /// <exception cref="CompressionException">The payload is malformed for <paramref name="format"/> or the format has no streaming decoder.
    /// <para>数据相对于 <paramref name="format"/> 损坏，或该格式没有流式解码器。</para></exception>
    public static void DecompressWithOptions(Stream input, Stream output, CompressionFormat format, long maximumOutputBytes, CompressionOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateForDecompression(format);

        using var limitedOutput = new BoundedWriteStream(output, maximumOutputBytes, leaveOpen: true, cancellationToken: cancellationToken);
        output = limitedOutput;

        if (format == CompressionFormat.None)
        {
            input.CopyTo(output);
            return;
        }


        switch (format)
        {
            case CompressionFormat.Xz:
                XzCompressor.Decompress(input, output);
                return;
            case CompressionFormat.Lzma:
                LzmaCompressor.Decompress(input, output);
                return;
            case CompressionFormat.Zstd:
                ZstdCompressor.Decompress(input, output);
                return;
            case CompressionFormat.Lz4Legacy:
                Lz4Compressor.DecompressLegacy(input, output);
                return;
            case CompressionFormat.None:
                break;
            case CompressionFormat.Gzip:
                break;
            case CompressionFormat.Zlib:
                break;
            case CompressionFormat.Deflate:
                break;
            case CompressionFormat.Brotli:
                break;
            case CompressionFormat.Lz4:
                break;
            case CompressionFormat.Bzip2:
                break;
            case CompressionFormat.Lzop:
                break;
            case CompressionFormat.Zopfli:
                break;
            default:
                break;
        }

        var info = CompressionFormats.GetInfo(format)!;
        if (info.IsStreaming)
        {
            DecompressStreaming(input, output, format);
            return;
        }

        if (format == CompressionFormat.Zopfli)
        {
            GzipCompressor.Decompress(input, output);
            return;
        }

        if (format == CompressionFormat.Lzop)
        {
            LzopCompressor.Decompress(input, output, maximumOutputBytes, options?.MaxDegreeOfParallelism);
            return;
        }

        throw new CompressionException($"Streaming decompression is not supported for format: {format}");
    }

    /// <summary>Creates the incremental decoder for a compressed payload, putting no output cap of its own on the decoded bytes and leaving the read loop, its bound and its cancellation to the caller.
    /// <para>为压缩数据创建增量解码流，本身不对解码字节施加任何输出上限，读取循环、限额与取消均由调用方掌握。</para></summary>
    /// <param name="input">Readable payload positioned at its first byte; ownership stays with the caller, and disposing the returned stream leaves it open.
    /// <para>定位在首字节处的可读数据；所有权归调用方，释放返回的流不会关闭它。</para></param>
    /// <param name="format">Container the payload is decoded with. An incremental reader exists for <see cref="CompressionFormat.None"/>, <see cref="CompressionFormat.Gzip"/>, <see cref="CompressionFormat.Zlib"/>, <see cref="CompressionFormat.Deflate"/>, <see cref="CompressionFormat.Brotli"/>, <see cref="CompressionFormat.Lz4"/>, <see cref="CompressionFormat.Bzip2"/>, <see cref="CompressionFormat.Xz"/>, <see cref="CompressionFormat.Zstd"/> and <see cref="CompressionFormat.Zopfli"/>, where <see cref="CompressionFormat.None"/> reads <paramref name="input"/> unchanged.
    /// <para>用于解码的容器格式。具备增量读取器的格式为 <see cref="CompressionFormat.None"/>、<see cref="CompressionFormat.Gzip"/>、<see cref="CompressionFormat.Zlib"/>、<see cref="CompressionFormat.Deflate"/>、<see cref="CompressionFormat.Brotli"/>、<see cref="CompressionFormat.Lz4"/>、<see cref="CompressionFormat.Bzip2"/>、<see cref="CompressionFormat.Xz"/>、<see cref="CompressionFormat.Zstd"/> 与 <see cref="CompressionFormat.Zopfli"/>，其中 <see cref="CompressionFormat.None"/> 原样读取 <paramref name="input"/>。</para></param>
    /// <returns>A readable stream of decoded bytes that enforces no size limit; the caller stops or bounds its own reads. Use <see cref="M:UotanStudio.Compress.CompressionService.Decompress(System.IO.Stream,System.IO.Stream,UotanStudio.Compress.CompressionFormat,System.Int64,System.Threading.CancellationToken)"/> when one bounded copy describes the work instead.
    /// <para>解码字节的可读流，不强制任何大小上限，需要限额时由调用方自行停止或界定读取。若一次带上限的拷贝即可描述该工作，请改用 <see cref="M:UotanStudio.Compress.CompressionService.Decompress(System.IO.Stream,System.IO.Stream,UotanStudio.Compress.CompressionFormat,System.Int64,System.Threading.CancellationToken)"/>。</para></returns>
    /// <remarks>The returned stream is the codec reader itself, so a malformed payload surfaces from the reader on the next read rather than from this call. Where the codec reader stops at the end of the first member or frame, a concatenated payload needs the copy entry, which walks every member.
    /// <para>返回的流就是编解码读取器本身，因此损坏的数据会在下一次读取时由读取器抛出，而不是在本调用抛出。读取器只覆盖首个成员或帧的格式，串联数据仍需会遍历全部成员的拷贝入口。</para></remarks>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is unknown or cannot decompress.
    /// <para><paramref name="format"/> 未知或不支持解压。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="format"/> decodes through a push-style block loop rather than a lazy reader: LZMA, LZ4 legacy and LZOP. Their bytes are produced by <see cref="M:UotanStudio.Compress.CompressionService.Decompress(System.IO.Stream,System.IO.Stream,UotanStudio.Compress.CompressionFormat,System.Int64,System.Threading.CancellationToken)"/>, which already reads block by block under its own limit.
    /// <para><paramref name="format"/> 通过推式块循环而非惰性读取器解码，即 LZMA、LZ4 传统格式与 LZOP。其字节请交由 <see cref="M:UotanStudio.Compress.CompressionService.Decompress(System.IO.Stream,System.IO.Stream,UotanStudio.Compress.CompressionFormat,System.Int64,System.Threading.CancellationToken)"/> 产出，该入口已按自带上限逐块读取。</para></exception>
    /// <exception cref="CompressionException">The codec reader could not be constructed, for example because the container metadata read during construction is malformed; the original error is kept as <see cref="Exception.InnerException"/>.
    /// <para>无法构造解码读取器，例如构造期间读取的容器元数据非法；原始异常保留在 <see cref="Exception.InnerException"/> 中。</para></exception>
    /// <example>
    /// <code language="csharp">
    /// using var reader = CompressionService.CreateDecompressionStream(payload, CompressionFormat.Gzip);
    /// var buffer = new byte[64 * 1024];
    /// int read;
    /// while ((read = reader.Read(buffer)) > 0)
    /// {
    /// cancellationToken.ThrowIfCancellationRequested();
    /// output.Write(buffer, 0, Math.Min(read, budget));
    /// }
    /// </code>
    /// </example>
    public static Stream CreateDecompressionStream(Stream input, CompressionFormat format)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateForDecompression(format);

        try
        {
            return format switch
            {
                CompressionFormat.None => new NonDisposingStream(input),
                CompressionFormat.Gzip => GzipCompressor.CreateDecompressionStream(input),
                CompressionFormat.Zopfli => GzipCompressor.CreateDecompressionStream(input),
                CompressionFormat.Zlib => ZlibCompressor.CreateDecompressionStream(input),
                CompressionFormat.Deflate => DeflateCompressor.CreateDecompressionStream(input),
                CompressionFormat.Brotli => BrotliCompressor.CreateDecompressionStream(input),
                CompressionFormat.Lz4 => Lz4Compressor.CreateDecompressionStream(input),
                CompressionFormat.Bzip2 => Bzip2Compressor.CreateDecompressionStream(input),
                CompressionFormat.Xz => XzCompressor.CreateDecompressionStream(input),
                CompressionFormat.Zstd => ZstdCompressor.CreateDecompressionStream(input),
                _ => throw new NotSupportedException($"Streaming decompression is not supported for format: {format}")
            };
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException($"Failed to create the incremental decoder for format: {format}", ex);
        }
    }

    /// <summary>Creates the incremental encoder that writes into a caller-owned destination, putting no size cap of its own on the encoded bytes.
    /// <para>创建写入调用方目标流的增量编码器，本身不对编码字节施加大小上限。</para></summary>
    /// <param name="output">Writable destination for the encoded bytes starting at its current position; ownership stays with the caller, and disposing the returned stream leaves it open.
    /// <para>从其当前位置开始接收编码字节的可写目标流；所有权归调用方，释放返回的流不会关闭它。</para></param>
    /// <param name="format">Target container. An incremental writer exists for <see cref="CompressionFormat.None"/>, <see cref="CompressionFormat.Gzip"/>, <see cref="CompressionFormat.Zlib"/>, <see cref="CompressionFormat.Deflate"/>, <see cref="CompressionFormat.Brotli"/>, <see cref="CompressionFormat.Lz4"/>, <see cref="CompressionFormat.Bzip2"/> and <see cref="CompressionFormat.Zstd"/>, where <see cref="CompressionFormat.None"/> writes the bytes it receives unchanged.
    /// <para>目标容器格式。具备增量写出器的格式为 <see cref="CompressionFormat.None"/>、<see cref="CompressionFormat.Gzip"/>、<see cref="CompressionFormat.Zlib"/>、<see cref="CompressionFormat.Deflate"/>、<see cref="CompressionFormat.Brotli"/>、<see cref="CompressionFormat.Lz4"/>、<see cref="CompressionFormat.Bzip2"/> 与 <see cref="CompressionFormat.Zstd"/>，其中 <see cref="CompressionFormat.None"/> 原样写出收到的字节。</para></param>
    /// <param name="options">Optional level tuning; <see langword="null"/> keeps every codec default. Chunking has no effect because one incremental stream is produced, and bzip2 ignores the level entirely because its block size is fixed.
    /// <para>可选的等级调优；为 <see langword="null"/> 时全部采用编解码器默认值。增量写出只生成单个流，因此并行分块不生效；bzip2 的块大小固定，其等级也被忽略。</para></param>
    /// <returns>A writable stream of the encoded container. Disposing it writes the codec trailer and flushes it into <paramref name="output"/>, so the caller must dispose the returned stream before consuming <paramref name="output"/>.
    /// <para>编码容器字节的可写流。释放它会写出编解码器尾部结构并刷入 <paramref name="output"/>，因此调用方必须先释放返回的流，再消费 <paramref name="output"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="format"/> is unknown or cannot compress, for example <see cref="CompressionFormat.Zopfli"/>.
    /// <para><paramref name="format"/> 未知或不支持压缩，例如 <see cref="CompressionFormat.Zopfli"/>。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="format"/> has no incremental writer because its container needs sizes known only after the whole payload: XZ, LZMA, LZ4 legacy and LZOP. Those bytes are produced by <see cref="M:UotanStudio.Compress.CompressionService.Compress(System.IO.Stream,System.IO.Stream,UotanStudio.Compress.CompressionFormat,UotanStudio.Compress.CompressionOptions)"/>, which writes in place, spools or blocks internally.
    /// <para><paramref name="format"/> 没有增量写出器，因为其容器需要在整段载荷完成后才能确定的尺寸，即 XZ、LZMA、LZ4 传统格式与 LZOP。其字节请交由 <see cref="M:UotanStudio.Compress.CompressionService.Compress(System.IO.Stream,System.IO.Stream,UotanStudio.Compress.CompressionFormat,UotanStudio.Compress.CompressionOptions)"/> 产出，该入口内部已做就地写出、暂存或分块。</para></exception>
    /// <exception cref="CompressionException">The codec writer could not be constructed for <paramref name="output"/>, for example because the stream rejects writes; the original error is kept as <see cref="Exception.InnerException"/>.
    /// <para>无法基于 <paramref name="output"/> 构造编码写出器，例如该流拒绝写入；原始异常保留在 <see cref="Exception.InnerException"/> 中。</para></exception>
    /// <example>
    /// <code language="csharp">
    /// using var encoded = new MemoryStream();
    /// using (var writer = CompressionService.CreateCompressionStream(encoded, CompressionFormat.Gzip))
    /// {
    /// writer.Write(payload);
    /// }
    /// </code>
    /// </example>
    public static Stream CreateCompressionStream(Stream output, CompressionFormat format, CompressionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateForCompression(format);

        try
        {
            return format switch
            {
                CompressionFormat.None => new NonDisposingStream(output),
                CompressionFormat.Gzip => GzipCompressor.CreateCompressionStream(output, options),
                CompressionFormat.Zlib => ZlibCompressor.CreateCompressionStream(output, options),
                CompressionFormat.Deflate => DeflateCompressor.CreateCompressionStream(output, options),
                CompressionFormat.Brotli => BrotliCompressor.CreateCompressionStream(output, options),
                CompressionFormat.Lz4 => Lz4Compressor.CreateCompressionStream(output, options),
                CompressionFormat.Bzip2 => Bzip2Compressor.CreateCompressionStream(output),
                CompressionFormat.Zstd => ZstdCompressor.CreateCompressionStream(output, options),
                _ => throw new NotSupportedException($"Streaming compression is not supported for format: {format}")
            };
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompressionException($"Failed to create the incremental encoder for format: {format}", ex);
        }
    }

    private static void ValidateForCompression(CompressionFormat format)
    {
        if (format == CompressionFormat.None)
        {
            return;
        }

        var info = CompressionFormats.GetInfo(format);
        if (info == null || !info.CanCompress)
        {
            throw new ArgumentException($"Compression is not supported for format: {format}", nameof(format));
        }
    }

    private static void ValidateForDecompression(CompressionFormat format)
    {
        if (format == CompressionFormat.None)
        {
            return;
        }

        var info = CompressionFormats.GetInfo(format);
        if (info == null || !info.CanDecompress)
        {
            throw new ArgumentException($"Decompression is not supported for format: {format}", nameof(format));
        }
    }

    private static byte[] CompressCore(byte[] data, CompressionFormat format, CompressionOptions? options)
    {
        return format switch
        {
            CompressionFormat.Gzip => GzipCompressor.Compress(data, options),
            CompressionFormat.Zlib => ZlibCompressor.Compress(data, options),
            CompressionFormat.Deflate => DeflateCompressor.Compress(data, options),
            CompressionFormat.Brotli => BrotliCompressor.Compress(data, options),
            CompressionFormat.Lz4 => Lz4Compressor.Compress(data, options),
            CompressionFormat.Lz4Legacy => Lz4Compressor.CompressLegacy(data, options),
            CompressionFormat.Lzma => LzmaCompressor.Compress(data),
            CompressionFormat.Xz => XzCompressor.Compress(data, options?.DictionarySize ?? Lzma2Encoder.DefaultDictionarySize, maxDegreeOfParallelism: options?.MaxDegreeOfParallelism),
            CompressionFormat.Bzip2 => Bzip2Compressor.Compress(data, options),
            CompressionFormat.Zstd => ZstdCompressor.Compress(data, options),
            CompressionFormat.None => data,


            CompressionFormat.Lzop => LzopCompressor.Compress(data),
            CompressionFormat.Zopfli => throw new NotSupportedException("Zopfli compression is not supported."),
            _ => throw new ArgumentException($"Unsupported compression format: {format}", nameof(format))
        };
    }

    private static byte[] DecompressCore(byte[] data, CompressionFormat format)
    {
        return format switch
        {
            CompressionFormat.Gzip => GzipCompressor.Decompress(data),
            CompressionFormat.Zlib => ZlibCompressor.Decompress(data),
            CompressionFormat.Deflate => DeflateCompressor.Decompress(data),
            CompressionFormat.Brotli => BrotliCompressor.Decompress(data),
            CompressionFormat.Lz4 => Lz4Compressor.Decompress(data),
            CompressionFormat.Lz4Legacy => Lz4Compressor.DecompressLegacy(data),
            CompressionFormat.Lzma => LzmaCompressor.Decompress(data),
            CompressionFormat.Xz => XzCompressor.Decompress(data),
            CompressionFormat.Bzip2 => Bzip2Compressor.Decompress(data),
            CompressionFormat.Zstd => ZstdCompressor.Decompress(data),
            CompressionFormat.None => data,


            CompressionFormat.Lzop => LzopCompressor.Decompress(data),


            CompressionFormat.Zopfli => GzipCompressor.Decompress(data),
            _ => throw new ArgumentException($"Unsupported compression format: {format}", nameof(format))
        };
    }

    private static void CompressStreaming(Stream input, Stream output, CompressionFormat format, CompressionOptions? options)
    {
        switch (format)
        {
            case CompressionFormat.Gzip:
                GzipCompressor.Compress(input, output, options);
                break;
            case CompressionFormat.Zlib:
                ZlibCompressor.Compress(input, output, options);
                break;
            case CompressionFormat.Deflate:
                DeflateCompressor.Compress(input, output, options);
                break;
            case CompressionFormat.Brotli:
                BrotliCompressor.Compress(input, output, options);
                break;
            case CompressionFormat.Bzip2:
                Bzip2Compressor.Compress(input, output);
                break;
            case CompressionFormat.Lz4:
                Lz4Compressor.Compress(input, output, options);
                break;
            case CompressionFormat.None:
                break;
            case CompressionFormat.Lz4Legacy:
                break;
            case CompressionFormat.Lzma:
                break;
            case CompressionFormat.Xz:
                break;
            case CompressionFormat.Zstd:
                break;
            case CompressionFormat.Lzop:
                break;
            case CompressionFormat.Zopfli:
                break;
            default:
                throw new ArgumentException($"Format is not streaming: {format}", nameof(format));
        }
    }

    private static void DecompressStreaming(Stream input, Stream output, CompressionFormat format)
    {
        switch (format)
        {
            case CompressionFormat.Gzip:
                GzipCompressor.Decompress(input, output);
                break;
            case CompressionFormat.Zlib:
                ZlibCompressor.Decompress(input, output);
                break;
            case CompressionFormat.Deflate:
                DeflateCompressor.Decompress(input, output);
                break;
            case CompressionFormat.Brotli:
                BrotliCompressor.Decompress(input, output);
                break;
            case CompressionFormat.Bzip2:
                Bzip2Compressor.Decompress(input, output);
                break;
            case CompressionFormat.Lz4:
                Lz4Compressor.Decompress(input, output);
                break;
            case CompressionFormat.None:
                break;
            case CompressionFormat.Lz4Legacy:
                break;
            case CompressionFormat.Lzma:
                break;
            case CompressionFormat.Xz:
                break;
            case CompressionFormat.Zstd:
                break;
            case CompressionFormat.Lzop:
                break;
            case CompressionFormat.Zopfli:
                break;
            default:
                throw new ArgumentException($"Format is not streaming: {format}", nameof(format));
        }
    }
}