namespace UotanStudio.Media.Decoding;

/// <summary>
/// Bounded AV1 low-overhead OBU temporal-unit splitter.
/// <para>有界 AV1 OBU 时间单元分割器。</para>
/// </summary>
public static class Av1Obu
{
    /// <summary>Splits a delimited OBU stream without copying encoded payloads.
    /// <para>按时间分隔符分割，保留原始编码负载。</para></summary>
    /// <param name="stream">A low-overhead AV1 OBU stream in which every OBU declares its size; headers with forbidden or reserved bits are rejected, and an extension flag consumes one extra header byte.
    /// <para>每个 OBU 均声明长度的低开销 AV1 OBU 码流；含 forbidden 或保留位的头部会被拒绝，带扩展标志时额外消耗一个头部字节。</para></param>
    /// <returns>Contiguous half-open ranges covering the whole input, one per temporal unit; each unit after the first starts at its temporal-delimiter OBU.
    /// <para>覆盖整个输入的连续半开区间，每个时间单元一段；首个单元之后的每段从其 temporal-delimiter OBU 起始。</para></returns>
    /// <exception cref="InvalidDataException">An OBU header is not sized low-overhead framing, or an extension byte or size field is truncated, oversized, or out of range for the remaining stream.
    /// <para>OBU 头部不是带长度的低开销分帧，或扩展字节、长度字段被截断、超长或超出流剩余范围。</para></exception>
    public static IReadOnlyList<Range> SplitTemporalUnits(ReadOnlySpan<byte> stream)
    {
        var ranges = new List<Range>();
        int start = 0, position = 0;
        while (position < stream.Length)
        {
            var headerOffset = position;
            var type = ReadObu(stream, ref position);
            if (type == 2 && headerOffset > start) { ranges.Add(start..headerOffset); start = headerOffset; }
        }
        if (start < stream.Length) ranges.Add(start..stream.Length);
        return ranges;
    }

    /// <summary>Validates all OBUs and distinguishes standalone configuration from an access unit containing a frame header.</summary>
    internal static bool ContainsFrame(ReadOnlySpan<byte> stream)
    {
        var found = false;
        var position = 0;
        while (position < stream.Length) found |= ReadObu(stream, ref position) is 3 or 6 or 7;
        return found;
    }

    private static int ReadObu(ReadOnlySpan<byte> stream, ref int position)
    {
        var header = stream[position++];
        if ((header & 0x81) != 0 || (header & 2) == 0) throw new InvalidDataException("Expected sized low-overhead AV1 OBUs.");
        var type = (header >> 3) & 15;
        if ((header & 4) != 0)
        {
            if (position == stream.Length) throw new InvalidDataException("Truncated AV1 extension.");
            position++;
        }
        ulong length = 0;
        int index;
        for (index = 0; index < 8; index++)
        {
            if (position == stream.Length) throw new InvalidDataException("Truncated OBU size.");
            var value = stream[position++];
            length |= (ulong)(value & 127) << (index * 7);
            if ((value & 128) == 0) break;
        }
        if (index == 8 || length > (ulong)(stream.Length - position)) throw new InvalidDataException("Invalid OBU size.");
        position += checked((int)length);
        return type;
    }
}