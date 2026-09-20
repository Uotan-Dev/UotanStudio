using SevenZip;
using SevenZip.Compression.LZMA;

namespace UotanStudio.Firmware.BoundedContexts.Compression.Internal.Xz;

internal static class Lzma2Encoder
{
    // LZMA2 chunk headers carry the compressed-size field in 16 bits.  The
    // previous 2 MiB window therefore fell back to an uncompressed chunk for
    // most real ramdisks (the compressed stream was larger than 64 KiB),
    // producing .xz files that were almost as large as their input.  Keeping
    // each independently encoded chunk within the representable range lets
    // the XZ stream retain compression while preserving bounded memory use.
    private const int UncompressedChunkMax = 1 << 16;

    private const byte Lclppb = 0x5D;

    public const uint DefaultDictionarySize = 1u << 23;

    /// <summary>
    /// Encodes the input into a raw LZMA2 stream (without the XZ container).
    /// <para>将输入编码为 LZMA2 原始流（不含 XZ 容器）。</para>
    /// </summary>
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

    /// <summary>
    /// Streaming encode: reads input in fixed 2 MiB windows and encodes them into a raw LZMA2 stream written to output.
    /// <para>流式编码：从 input 按固定 2 MiB 窗口读取并编码为 LZMA2 原始流， 写入 output。</para>
    /// </summary>
    public static long Encode(Stream input, Stream output, uint dictionarySize = DefaultDictionarySize,
        int? maxDegreeOfParallelism = null, Action<byte[], int, int>? onOriginalWindow = null)
    {
        var dop = Parallelism.Resolve(maxDegreeOfParallelism, 2);
        var windows = new List<byte[]>(dop);
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
                    i => compressed[i] = Lzma1Compress(windows[i], 0, windows[i].Length, dictionarySize));
            }
            else
            {
                for (var i = 0; i < windows.Count; i++)
                {
                    compressed[i] = Lzma1Compress(windows[i], 0, windows[i].Length, dictionarySize);
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
    /// Reads one window of up to 2 MiB from the stream; returns null at EOF.
    /// <para>从流中读取至多 2 MiB 的一个窗口；EOF 时返回 null。</para>
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
    /// Compresses one data block with an independent LZMA1 encoder; returns raw LZMA1 data without the 5-byte property header.
    /// <para>用独立的 LZMA1 编码器压缩一个数据块，返回不含 5 字节属性头的裸 LZMA1 数据。</para>
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