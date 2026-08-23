using SevenZip;
using SevenZip.Compression.LZMA;

namespace UotanStudio.Firmware.Compress.Internal.Xz;

/// <summary>
/// 全托管 LZMA2 编码器。
/// <para>Fully-managed LZMA2 encoder.</para>
/// <para>
/// 基于官方 LZMA SDK 的 C# 移植（LZMA1 编码器）实现 LZMA2 分块流。
/// LZMA2 是 LZMA1 的外层容器：输入被切分为不超过 2 MiB 的块，每块用独立的
/// LZMA1 编码器压缩并带上 1 字节属性（lc/lp/pb），无法压缩的块按未压缩块输出。
/// 流以 0x00 结束标记收尾。
/// </para>
/// <para>
/// Built on the official LZMA SDK C# port (LZMA1 encoder). LZMA2 is a container
/// around LZMA1: input is split into chunks of at most 2 MiB, each chunk is
/// compressed by an independent LZMA1 encoder carrying a 1-byte property
/// (lc/lp/pb); chunks that cannot be compressed are emitted as uncompressed
/// chunks. The stream ends with the 0x00 end marker.
/// </para>
/// </summary>
internal static class Lzma2Encoder
{
    /// <summary>每个 LZMA 块的最大未压缩大小（2 MiB，21 位）。</summary>
    private const int UncompressedChunkMax = 1 << 21;

    /// <summary>LZMA1 默认属性：pb=2, lp=0, lc=3 → (2*5+0)*9+3 = 93 = 0x5D。</summary>
    private const byte Lclppb = 0x5D;

    /// <summary>默认字典大小（8 MiB，对应 XZ 字典索引 22）。</summary>
    public const uint DefaultDictionarySize = 1u << 23;

    /// <summary>
    /// 将输入编码为 LZMA2 原始流（不含 XZ 容器）。
    /// <para>Encodes the input into a raw LZMA2 stream (without the XZ container).</para>
    /// <para>
    /// 输入按固定 2 MiB 窗口切分，每个窗口用独立 LZMA1 编码器压缩（全量重置，互不依赖），
    /// 因此窗口可并行压缩；输出按窗口顺序拼接，与串行结果逐字节一致（确定性）。
    /// 无法压缩的窗口整体按 ≤64 KiB 的未压缩块输出。
    /// </para>
    /// <para>
    /// The input is split into fixed 2 MiB windows; each window is compressed by an
    /// independent LZMA1 encoder (full reset, no cross-window dependency), so windows
    /// can be compressed in parallel; output is assembled in window order and is
    /// byte-identical to sequential execution (deterministic). Incompressible windows
    /// are emitted as ≤64 KiB uncompressed chunks.
    /// </para>
    /// </summary>
    public static byte[] Encode(byte[] data, uint dictionarySize = DefaultDictionarySize, int? maxDegreeOfParallelism = null)
    {
        // 固定 2 MiB 窗口起点：与压缩结果无关，天然可分块并行。
        // Fixed 2 MiB window starts: independent of compression outcomes → parallelizable.
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
            // 每个窗口独立压缩：独立 LZMA1 编码器 + 全量重置，线程安全。
            // Each window compresses independently: own LZMA1 encoder + full reset, thread-safe.
            Parallel.For(
                0, windowCount,
                new ParallelOptions { MaxDegreeOfParallelism = dop },
                i =>
                {
                    var start = windowStarts[i];
                    var chunkSize = Math.Min(UncompressedChunkMax, data.Length - start);
                    compressed[i] = Lzma1Compress(data, start, chunkSize, dictionarySize);
                });
        }
        else
        {
            for (var i = 0; i < windowCount; i++)
            {
                var start = windowStarts[i];
                var chunkSize = Math.Min(UncompressedChunkMax, data.Length - start);
                compressed[i] = Lzma1Compress(data, start, chunkSize, dictionarySize);
            }
        }

        using var output = new MemoryStream();
        var firstChunk = true;

        // 按窗口顺序拼接输出（与串行一致）。
        // Assemble output in window order (identical to sequential).
        for (var i = 0; i < windowCount; i++)
        {
            var start = windowStarts[i];
            var chunkSize = Math.Min(UncompressedChunkMax, data.Length - start);
            var result = compressed[i];

            // 压缩有效（更小且压缩大小可放入 16 位字段）时输出 LZMA 块，否则按未压缩块输出。
            if (result.Length < chunkSize && result.Length <= 0xFFFF)
            {
                WriteLzmaChunkHeader(output, chunkSize, result.Length);
                output.Write(result, 0, result.Length);
                firstChunk = false;
            }
            else
            {
                // 未压缩块最多 64 KiB，超出的拆成多个未压缩块。
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

        // 结束标记。
        output.WriteByte(0x00);
        return output.ToArray();
    }

    /// <summary>
    /// 流式编码：从 <paramref name="input"/> 按固定 2 MiB 窗口读取并编码为 LZMA2 原始流，
    /// 写入 <paramref name="output"/>。内存占用有界：每次仅缓冲至多
    /// maxDegreeOfParallelism 个窗口（各 ≤2 MiB），不整块缓冲输入。
    /// 输出与 <see cref="Encode(byte[], uint, int?)"/> 对相同输入逐字节一致。
    /// <para>Streaming encode: reads <paramref name="input"/> in fixed 2 MiB windows and
    /// encodes them into a raw LZMA2 stream written to <paramref name="output"/>.
    /// Memory is bounded: only up to maxDegreeOfParallelism windows (each ≤2 MiB) are
    /// buffered at a time; the input is never fully buffered. The output is byte-identical
    /// to <see cref="Encode(byte[], uint, int?)"/> for the same input.</para>
    /// </summary>
    /// <param name="input">待压缩的输入流。<para>The input stream to compress.</para></param>
    /// <param name="output">写入 LZMA2 原始流的输出流。<para>The output stream receiving the raw LZMA2 stream.</para></param>
    /// <param name="dictionarySize">字典大小（默认 8 MiB）。<para>Dictionary size (default 8 MiB).</para></param>
    /// <param name="maxDegreeOfParallelism">多核并行度；null/1 为串行。<para>Multi-core parallelism; null/1 = sequential.</para></param>
    /// <param name="onOriginalWindow">每读入一个原始窗口后回调（用于增量校验和）；可为 null。
    /// <para>Callback invoked after each original window is read (for incremental checksums); may be null.</para></param>
    /// <returns>未压缩总字节数。<para>The total uncompressed byte count.</para></returns>
    public static long Encode(Stream input, Stream output, uint dictionarySize = DefaultDictionarySize,
        int? maxDegreeOfParallelism = null, Action<byte[], int, int>? onOriginalWindow = null)
    {
        var dop = Parallelism.Resolve(maxDegreeOfParallelism, 2);
        var windows = new List<byte[]>(dop);
        long total = 0;
        var firstChunk = true;

        while (true)
        {
            // 批量读取至多 dop 个窗口（内存有界）。
            // Read a batch of up to dop windows (bounded memory).
            windows.Clear();
            while (windows.Count < dop)
            {
                var win = ReadWindow(input);
                if (win == null)
                {
                    break; // EOF
                }

                onOriginalWindow?.Invoke(win, 0, win.Length);
                windows.Add(win);
                total += win.Length;
            }
            if (windows.Count == 0)
            {
                break;
            }

            // 各窗口独立压缩（独立编码器 + 全量重置，线程安全）。
            // Each window compresses independently (own encoder + full reset, thread-safe).
            var compressed = new byte[windows.Count][];
            if (windows.Count > 1 && dop > 1)
            {
                Parallel.For(
                    0, windows.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = dop },
                    i => compressed[i] = Lzma1Compress(windows[i], 0, windows[i].Length, dictionarySize));
            }
            else
            {
                for (var i = 0; i < windows.Count; i++)
                {
                    compressed[i] = Lzma1Compress(windows[i], 0, windows[i].Length, dictionarySize);
                }
            }

            // 按窗口顺序拼接输出（与串行一致）。
            // Assemble output in window order (identical to sequential).
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

        // 结束标记。
        output.WriteByte(0x00);
        return total;
    }

    /// <summary>
    /// 从流中读取至多 2 MiB 的一个窗口；EOF 时返回 null。
    /// <para>Reads one window of up to 2 MiB from the stream; returns null at EOF.</para>
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
    /// 用独立的 LZMA1 编码器压缩一个数据块，返回不含 5 字节属性头的裸 LZMA1 数据。
    /// <para>Compresses one data block with an independent LZMA1 encoder; returns raw LZMA1 data without the 5-byte property header.</para>
    /// </summary>
    private static byte[] Lzma1Compress(byte[] data, int start, int count, uint dictionarySize)
    {
        var encoder = new Encoder();
        encoder.SetCoderProperties(
            [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitPosBits, CoderPropID.LitContextBits],
            [(int)dictionarySize, 2, 0, 3]);

        using var input = new MemoryStream(data, start, count, writable: false);
        using var output = new MemoryStream();
        encoder.Code(input, output, count, -1, null);
        return output.ToArray();
    }

    /// <summary>
    /// 写 LZMA 块头：控制字节（新属性 + 字典重置）+ 未压缩大小 + 压缩大小 + 属性字节。
    /// <para>Writes the LZMA chunk header: control byte (new props + dictionary reset) + uncompressed size + compressed size + props byte.</para>
    /// </summary>
    private static void WriteLzmaChunkHeader(Stream output, int uncompressedSize, int compressedSize)
    {
        // 每块都是独立编码器，始终全量重置（0xE0）；高 5 位并入未压缩大小。
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
    /// 写未压缩块头：控制字节（1=字典重置，2=不重置）+ 2 字节大小。
    /// <para>Writes the uncompressed chunk header: control byte (1=dict reset, 2=no reset) + 2-byte size.</para>
    /// </summary>
    private static void WriteUncompressedChunkHeader(Stream output, bool firstChunk, int size)
    {
        output.WriteByte(firstChunk ? (byte)1 : (byte)2);
        output.WriteByte((byte)((size - 1) >> 8));
        output.WriteByte((byte)((size - 1) & 0xFF));
    }
}