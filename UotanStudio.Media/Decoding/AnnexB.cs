using System.Buffers.Binary;

namespace UotanStudio.Media.Decoding;

/// <summary>Bounded Annex-B framing shared by native decoders and encoders.</summary>
internal static class AnnexB
{
    internal static List<Range> Split(ReadOnlySpan<byte> data)
    {
        VideoFrameLimits.ValidateAccessUnit(data);
        var result = new List<Range>();
        int start = -1;
        for (int i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            int prefix = data[i + 2] == 1 ? 3 : i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
            if (prefix == 0) continue;
            if (start >= 0)
            {
                int end = i;
                while (end > start && data[end - 1] == 0) end--;
                if (end == start) throw new InvalidDataException("Empty Annex-B NAL unit.");
                result.Add(start..end);
            }
            else
            {
                foreach (byte leading in data[..i])
                    if (leading != 0) throw new InvalidDataException("Data before Annex-B start code.");
            }
            if (result.Count >= 4096) throw new InvalidDataException("Too many NAL units.");
            start = i + prefix;
            i = start - 1;
        }
        if (start < 0 || start == data.Length) throw new InvalidDataException("Missing Annex-B NAL unit.");
        int final = data.Length;
        while (final > start && data[final - 1] == 0) final--;
        if (final == start) throw new InvalidDataException("Empty Annex-B NAL unit.");
        result.Add(start..final);
        return result;
    }

    internal static byte[] ToLengthPrefixed(ReadOnlySpan<byte> data, IReadOnlyList<Range> ranges)
    {
        int size = 0;
        foreach (Range range in ranges) size = checked(size + 4 + data[range].Length);
        if (size > VideoFrameLimits.MaximumAccessUnitBytes) throw new InvalidDataException("Access unit exceeds 32 MiB.");
        byte[] result = new byte[size];
        int offset = 0;
        foreach (Range range in ranges)
        {
            ReadOnlySpan<byte> nal = data[range];
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset), (uint)nal.Length);
            nal.CopyTo(result.AsSpan(offset + 4));
            offset += nal.Length + 4;
        }
        return result;
    }
}