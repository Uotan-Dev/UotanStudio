using SevenZip;
using SevenZip.Compression.LZMA;

namespace UotanStudio.Compress.Internal;

internal static class Lzma2Encoder
{
    private const int UncompressedChunkMax = 1 << 16;

    private const byte Lclppb = 0x5D;

    private const long WindowEncoderDictionaryMemoryFactor = 12L;

    private const long WindowEncoderFixedMemoryBytes = 8L * 1024 * 1024;

    private const long WindowEncoderMinimumMemoryBytes = 16L * 1024 * 1024;

    private const long WindowEncoderBudgetBytes = 512L * 1024 * 1024;

    public const uint DefaultDictionarySize = 1u << 23;

    /// <summary>Encodes the input into a raw LZMA2 stream (without the XZ container).
    /// <para>将输入编码为 LZMA2 原始流（不含 XZ 容器）。</para></summary>
    /// <param name="data">Bytes to compress, split into independent 64 KiB windows; an empty array yields a stream holding only the end marker.
    /// <para>待压缩的字节，按相互独立的 64 KiB 窗口切分；空数组仅产出含结束标记的流。</para></param>
    /// <param name="dictionarySize">LZMA dictionary size in bytes given to each window encoder, defaulting to <see cref="DefaultDictionarySize"/> (8 MiB).
    /// <para>交给每个窗口编码器的字典大小（字节），默认为 <see cref="DefaultDictionarySize"/>（8 MiB）。</para></param>
    /// <param name="maxDegreeOfParallelism"><see langword="null"/> or 1 and below encodes sequentially; above 1 encodes independent windows concurrently, capped by the window count, with bytes identical to the sequential result.
    /// <para>为 <see langword="null"/> 或小于等于 1 时串行编码；大于 1 时并发编码相互独立的窗口（不超过窗口 数量），输出字节与串行结果一致。</para></param>
    /// <returns>The raw LZMA2 bytes ending with the <c>0x00</c> end marker.
    /// <para>以 <c>0x00</c> 结束标记收尾的 LZMA2 原始字节。</para></returns>
    public static byte[] Encode(byte[] data, uint dictionarySize = DefaultDictionarySize, int? maxDegreeOfParallelism = null)
    {

        var windowStarts = new List<int>();
        for (var pos = 0; pos < data.Length; pos += UncompressedChunkMax)
        {
            windowStarts.Add(pos);
        }

        var windowCount = windowStarts.Count;
        var compressed = new byte[windowCount][];
        var dop = Parallelism.Resolve(maxDegreeOfParallelism, windowCount);

        if (dop > 1)
        {

            Parallel.For(
                0, windowCount,
                new ParallelOptions { MaxDegreeOfParallelism = dop },
                () => CreateEncoder(dictionarySize),
                (i, _, encoder) =>
                {
                    var start = windowStarts[i];
                    var chunkSize = Math.Min(UncompressedChunkMax, data.Length - start);
                    compressed[i] = Lzma1Compress(encoder, data, start, chunkSize);
                    return encoder;
                },
                _ => { });
        }
        else
        {
            Encoder? encoder = null;
            for (var i = 0; i < windowCount; i++)
            {
                var start = windowStarts[i];
                var chunkSize = Math.Min(UncompressedChunkMax, data.Length - start);
                compressed[i] = Lzma1Compress(encoder ??= CreateEncoder(dictionarySize), data, start, chunkSize);
            }
        }

        using var output = new MemoryStream();
        var firstChunk = true;

        for (var i = 0; i < windowCount; i++)
        {
            var start = windowStarts[i];
            var chunkSize = Math.Min(UncompressedChunkMax, data.Length - start);
            var result = compressed[i];

            if (result.Length < chunkSize && result.Length <= 0xFFFF)
            {
                WriteLzmaChunkHeader(output, chunkSize, result.Length);
                output.Write(result, 0, result.Length);
                firstChunk = false;
            }
            else
            {

                var subStart = start;
                var subEnd = start + chunkSize;
                while (subStart < subEnd)
                {
                    var sub = Math.Min(0x10000, subEnd - subStart);
                    WriteUncompressedChunkHeader(output, firstChunk, sub);
                    output.Write(data, subStart, sub);
                    firstChunk = false;
                    subStart += sub;
                }
            }
        }

        output.WriteByte(0x00);
        return output.ToArray();
    }

    /// <summary>Streaming encode: reads independent 64 KiB windows and writes a raw LZMA2 stream.
    /// <para>流式编码：读取独立的 64 KiB 窗口并写出 LZMA2 原始流。</para></summary>
    /// <param name="input">Source of the uncompressed bytes, drained in whole 64 KiB windows until it returns <c>0</c>.
    /// <para>未压缩字节的来源，按完整的 64 KiB 窗口读取直到返回 <c>0</c>。</para></param>
    /// <param name="output">Destination stream that receives the chunk headers, payload bytes and the terminating <c>0x00</c> end marker; the caller keeps ownership and flushes it.
    /// <para>接收块头、载荷字节与结尾 <c>0x00</c> 结束标记的目标流；所有权仍属于调用方，由其负责刷新。</para></param>
    /// <param name="dictionarySize">LZMA dictionary size in bytes given to each window encoder, defaulting to <see cref="DefaultDictionarySize"/> (8 MiB).
    /// <para>交给每个窗口编码器的字典大小（字节），默认为 <see cref="DefaultDictionarySize"/>（8 MiB）。</para></param>
    /// <param name="maxDegreeOfParallelism"><see langword="null"/> or 1 and below reads and encodes sequentially; above 1 that many windows are encoded concurrently, bounded by the window count, the detected physical core count and a 512 MiB budget for the dictionary-sized window encoders, while staying in input order.
    /// <para>为 <see langword="null"/> 或小于等于 1 时串行读取与编码；大于 1 时并发编码相应数量的窗口，上限由窗口数量、 探测到的物理核心数以及字典大小的窗口编码器所占 512 MiB 预算共同决定，窗口仍按输入顺序写出。</para></param>
    /// <param name="onOriginalWindow">Optional callback invoked once per window with the freshly read uncompressed bytes; <see langword="null"/> reports nothing.
    /// <para>可选回调，每个窗口以刚读取的未压缩字节调用一次；为 <see langword="null"/> 时不上报。</para></param>
    /// <returns>The total number of uncompressed bytes read from <paramref name="input"/>.
    /// <para>从 <paramref name="input"/> 读取的未压缩字节总数。</para></returns>
    public static long Encode(Stream input, Stream output, uint dictionarySize = DefaultDictionarySize,
        int? maxDegreeOfParallelism = null, Action<byte[], int, int>? onOriginalWindow = null)
    {
        var dop = ResolveStreamingDegree(maxDegreeOfParallelism, dictionarySize);
        var windows = new List<byte[]>(dop);
        var encoders = new Encoder?[dop];
        long total = 0;
        var firstChunk = true;

        while (true)
        {

            windows.Clear();
            while (windows.Count < dop)
            {
                var win = ReadWindow(input);
                if (win == null)
                {
                    break;
                }

                onOriginalWindow?.Invoke(win, 0, win.Length);
                windows.Add(win);
                total += win.Length;
            }
            if (windows.Count == 0)
            {
                break;
            }

            var compressed = new byte[windows.Count][];
            if (windows.Count > 1 && dop > 1)
            {
                Parallel.For(
                    0, windows.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = dop },
                    i => compressed[i] = Lzma1Compress(encoders[i] ??= CreateEncoder(dictionarySize), windows[i], 0, windows[i].Length));
            }
            else
            {
                for (var i = 0; i < windows.Count; i++)
                {
                    compressed[i] = Lzma1Compress(encoders[i] ??= CreateEncoder(dictionarySize), windows[i], 0, windows[i].Length);
                }
            }

            for (var i = 0; i < windows.Count; i++)
            {
                var win = windows[i];
                var result = compressed[i];

                if (result.Length < win.Length && result.Length <= 0xFFFF)
                {
                    WriteLzmaChunkHeader(output, win.Length, result.Length);
                    output.Write(result, 0, result.Length);
                    firstChunk = false;
                }
                else
                {
                    var subStart = 0;
                    while (subStart < win.Length)
                    {
                        var sub = Math.Min(0x10000, win.Length - subStart);
                        WriteUncompressedChunkHeader(output, firstChunk, sub);
                        output.Write(win, subStart, sub);
                        firstChunk = false;
                        subStart += sub;
                    }
                }
            }
        }

        output.WriteByte(0x00);
        return total;
    }

    /// <summary>
    /// Resolves how many windows the streaming encoder may hold concurrently. Each window encoder owns a
    /// dictionary-sized match finder whose buffer, hash and son tables keep roughly twelve dictionary copies
    /// resident, so the degree is capped by the request, the physical core count and the budget; the encoded
    /// bytes do not depend on it because every window resets its encoder and declares the dictionary reset in
    /// its chunk header.
    /// <para>解析流式编码器可同时持有的窗口数量。每个窗口编码器都拥有与字典同规模的匹配查找器，其缓冲、哈希与
    /// 子表合计约占用十二份字典大小的常驻内存，因此并行度受请求、物理核心数与预算共同限制；编码字节不取决于该值，
    /// 因为每个窗口都会重置编码器并在块头中宣告字典重置。</para>
    /// </summary>
    /// <param name="requested">Caller request; <see langword="null"/> or 1 and below resolves to 1.
    /// <para>调用方请求；<see langword="null"/> 或不大于 1 时解析为 1。</para></param>
    /// <param name="dictionarySize">Dictionary size each window encoder is configured with.
    /// <para>每个窗口编码器配置的字典大小。</para></param>
    /// <returns>The degree to encode with, never below <c>1</c>.
    /// <para>实际使用的编码并行度，不小于 <c>1</c>。</para></returns>
    private static int ResolveStreamingDegree(int? requested, uint dictionarySize)
    {
        if (requested is null or <= 1)
        {
            return 1;
        }

        var perWindow = Math.Max(
            ((long)dictionarySize * WindowEncoderDictionaryMemoryFactor) + WindowEncoderFixedMemoryBytes,
            WindowEncoderMinimumMemoryBytes);
        var affordable = (int)Math.Clamp(WindowEncoderBudgetBytes / perWindow, 1L, 64L);
        return Parallelism.Resolve(requested, affordable);
    }

    /// <summary>
    /// Reads one window of up to 64 KiB from the stream; returns null at EOF.
    /// <para>从流中读取至多 64 KiB 的一个窗口；EOF 时返回 null。</para>
    /// </summary>
    private static byte[]? ReadWindow(Stream input)
    {
        var win = new byte[UncompressedChunkMax];
        var read = 0;
        while (read < win.Length)
        {
            var n = input.Read(win, read, win.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read == 0 ? null : read == win.Length ? win : win.AsSpan(0, read).ToArray();
    }

    /// <summary>
    /// Creates an encoder owned exclusively by one worker for this encoding call.
    /// <para>创建本次编码调用中由单个 worker 独占的编码器。</para>
    /// </summary>
    private static Encoder CreateEncoder(uint dictionarySize)
    {
        var encoder = new Encoder();
        encoder.SetCoderProperties(
            [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitPosBits, CoderPropID.LitContextBits],
            [(int)dictionarySize, 2, 0, 3]);

        return encoder;
    }

    private static byte[] Lzma1Compress(Encoder encoder, byte[] data, int start, int count)
    {
        using var input = new MemoryStream(data, start, count, writable: false);
        using var output = new MemoryStream();
        encoder.Code(input, output, count, -1, null);
        return output.ToArray();
    }

    /// <summary>
    /// Writes the LZMA chunk header: control byte (new props + dictionary reset) + uncompressed size + compressed size + props byte.
    /// <para>写 LZMA 块头：控制字节（新属性 + 字典重置）+ 未压缩大小 + 压缩大小 + 属性字节。</para>
    /// </summary>
    private static void WriteLzmaChunkHeader(Stream output, int uncompressedSize, int compressedSize)
    {

        var usz = (uint)(uncompressedSize - 1);
        var csz = (uint)(compressedSize - 1);

        output.WriteByte((byte)(0xE0 + (usz >> 16)));
        output.WriteByte((byte)((usz >> 8) & 0xFF));
        output.WriteByte((byte)(usz & 0xFF));
        output.WriteByte((byte)(csz >> 8));
        output.WriteByte((byte)(csz & 0xFF));
        output.WriteByte(Lclppb);
    }

    /// <summary>
    /// Writes the uncompressed chunk header: control byte (1=dict reset, 2=no reset) + 2-byte size.
    /// <para>写未压缩块头：控制字节（1=字典重置，2=不重置）+ 2 字节大小。</para>
    /// </summary>
    private static void WriteUncompressedChunkHeader(Stream output, bool firstChunk, int size)
    {
        output.WriteByte(firstChunk ? (byte)1 : (byte)2);
        output.WriteByte((byte)((size - 1) >> 8));
        output.WriteByte((byte)((size - 1) & 0xFF));
    }
}