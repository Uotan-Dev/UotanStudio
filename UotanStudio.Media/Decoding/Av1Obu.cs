namespace UotanStudio.Media.Decoding;

/// <summary>Bounded AV1 low-overhead OBU temporal-unit splitter. 有界 AV1 OBU 时间单元分割器。</summary>
public static class Av1Obu
{
    /// <summary>Splits a delimited OBU stream without copying encoded payloads. 按时间分隔符分割，保留原始编码负载。</summary>
    public static IReadOnlyList<Range> SplitTemporalUnits(ReadOnlySpan<byte> stream)
    {
        var ranges = new List<Range>();
        int start = 0, position = 0;
        while (position < stream.Length)
        {
            int headerOffset = position;
            int type = ReadObu(stream, ref position);
            if (type == 2 && headerOffset > start) { ranges.Add(start..headerOffset); start = headerOffset; }
        }
        if (start < stream.Length) ranges.Add(start..stream.Length);
        return ranges;
    }

    /// <summary>Validates all OBUs and distinguishes standalone configuration from an access unit containing a frame header.</summary>
    internal static bool ContainsFrame(ReadOnlySpan<byte> stream)
    {
        bool found = false;
        int position = 0;
        while (position < stream.Length) found |= ReadObu(stream, ref position) is 3 or 6 or 7;
        return found;
    }

    private static int ReadObu(ReadOnlySpan<byte> stream, ref int position)
    {
        byte header = stream[position++];
        if ((header & 0x81) != 0 || (header & 2) == 0) throw new InvalidDataException("Expected sized low-overhead AV1 OBUs.");
        int type = (header >> 3) & 15;
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
            byte value = stream[position++];
            length |= (ulong)(value & 127) << (index * 7);
            if ((value & 128) == 0) break;
        }
        if (index == 8 || length > (ulong)(stream.Length - position)) throw new InvalidDataException("Invalid OBU size.");
        position += checked((int)length);
        return type;
    }
}