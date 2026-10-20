using System.Buffers.Binary;

namespace UotanStudio.Media.Decoding;

/// <summary>Bounded Annex-B framing shared by native decoders and encoders.</summary>
internal static class AnnexB
{
    internal static List<Range> Split(ReadOnlySpan<byte> data)
    {
        var result = new List<Range>();
        Split(data, result);
        return result;
    }

    internal static void Split(ReadOnlySpan<byte> data, List<Range> result)
    {
        result.Clear();
        VideoFrameLimits.ValidateAccessUnit(data);
        var start = -1;
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            var prefix = data[i + 2] == 1 ? 3 : i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
            if (prefix == 0) continue;
            if (start >= 0)
            {
                var end = i;
                while (end > start && data[end - 1] == 0) end--;
                if (end == start) throw new InvalidDataException("Empty Annex-B NAL unit.");
                result.Add(start..end);
            }
            else
            {
                foreach (var leading in data[..i])
                    if (leading != 0) throw new InvalidDataException("Data before Annex-B start code.");
            }
            if (result.Count >= 4096) throw new InvalidDataException("Too many NAL units.");
            start = i + prefix;
            i = start - 1;
        }
        if (start < 0 || start == data.Length) throw new InvalidDataException("Missing Annex-B NAL unit.");
        var final = data.Length;
        while (final > start && data[final - 1] == 0) final--;
        if (final == start) throw new InvalidDataException("Empty Annex-B NAL unit.");
        result.Add(start..final);
    }

    internal static int LengthPrefixedSize(ReadOnlySpan<byte> data, IReadOnlyList<Range> ranges)
    {
        var size = 0;
        foreach (Range range in ranges) size = checked(size + 4 + data[range].Length);
        if (size > VideoFrameLimits.MaximumAccessUnitBytes) throw new InvalidDataException("Access unit exceeds 32 MiB.");
        return size;
    }

    internal static void WriteLengthPrefixed(ReadOnlySpan<byte> data, IReadOnlyList<Range> ranges, Span<byte> destination)
    {
        var offset = 0;
        foreach (Range range in ranges)
        {
            ReadOnlySpan<byte> nal = data[range];
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset, 4), (uint)nal.Length);
            nal.CopyTo(destination[(offset + 4)..]);
            offset += nal.Length + 4;
        }
        if (offset != destination.Length) throw new ArgumentException("The destination does not match the NAL ranges.", nameof(destination));
    }

    internal static byte[] ToLengthPrefixed(ReadOnlySpan<byte> data, IReadOnlyList<Range> ranges)
    {
        var result = new byte[LengthPrefixedSize(data, ranges)];
        WriteLengthPrefixed(data, ranges, result);
        return result;
    }
}