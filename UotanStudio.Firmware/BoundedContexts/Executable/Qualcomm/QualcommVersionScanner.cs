using System.Globalization;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Executable.Elf;

namespace UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;

internal static class QualcommVersionScanner
{
    private const int MaximumStringBytes = 1024;
    private const int MaximumScanBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly (string Name, byte[] Prefix)[] Prefixes =
    [
        ("QcVersion", "QC_IMAGE_VERSION_STRING="u8.ToArray()),
        ("OemVersion", "OEM_IMAGE_VERSION_STRING="u8.ToArray()),
        ("ImageVariant", "IMAGE_VARIANT_STRING="u8.ToArray())
    ];

    internal static IReadOnlyList<QualcommVersionEvidence> Read(
        Stream stream, IReadOnlyList<ElfSegment> segments, CancellationToken token)
    {
        var result = new List<QualcommVersionEvidence>();
        var seen = new HashSet<long>();
        var buffer = new byte[64 * 1024];
        var text = new byte[MaximumStringBytes];
        var remaining = MaximumScanBytes;
        var expectBuildDate = false;
        string? buildDate = null;
        long buildOffset = 0;
        foreach (var segment in segments.Where(item => item.Type == 1 && item.FileSize > 0))
        {
            expectBuildDate = false;
            buildDate = null;
            var length = checked((int)Math.Min(segment.FileSize, (ulong)remaining));
            var textLength = 0;
            var tooLong = false;
            var start = (long)segment.Offset;
            for (var cursor = 0; cursor < length && result.Count < 32;)
            {
                var count = Math.Min(buffer.Length, length - cursor);
                QualcommMbnLayout.ReadAt(stream, checked((long)segment.Offset + cursor), buffer.AsSpan(0, count), token);
                remaining -= count;
                for (var i = 0; i < count; i++)
                {
                    var value = buffer[i];
                    if (value == 0)
                    {
                        if (!tooLong && textLength > 0)
                        {
                            Add(text.AsSpan(0, textLength), start);
                        }
                        else
                        {
                            expectBuildDate = false;
                            buildDate = null;
                        }
                        textLength = 0;
                        tooLong = false;
                        start = (long)segment.Offset + cursor + i + 1;
                    }
                    else if (textLength < text.Length)
                    {
                        text[textLength++] = value;
                    }
                    else
                    {
                        tooLong = true;
                    }
                }
                cursor += count;
            }
            if (remaining == 0 || result.Count >= 32)
            {
                break;
            }
        }
        return result.AsReadOnly();

        void Add(ReadOnlySpan<byte> data, long offset)
        {
            if (data.SequenceEqual("Binary build date: %s @ %s"u8))
            {
                expectBuildDate = true;
                buildDate = null;
                buildOffset = offset;
            }
            else if (expectBuildDate)
            {
                expectBuildDate = false;
                buildDate = data.Length <= 32 && data.IndexOfAnyInRange((byte)128, byte.MaxValue) < 0
                    ? Encoding.ASCII.GetString(data) : null;
            }
            else if (buildDate is not null)
            {
                if (data.Length <= 9 && result.Count < 32
                    && DateTime.TryParseExact(buildDate + " " + Encoding.ASCII.GetString(data),
                        ["MMM dd yyyy HH:mm:ss", "MMM d yyyy HH:mm:ss"], CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var time) && seen.Add(buildOffset))
                {
                    result.Add(new("BuildTime", time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), buildOffset));
                }
                buildDate = null;
            }
            foreach (var (name, prefix) in Prefixes)
            {
                if (result.Count >= 32 || !data.StartsWith(prefix) || data.Length == prefix.Length || seen.Contains(offset))
                {
                    continue;
                }
                try
                {
                    var value = StrictUtf8.GetString(data[prefix.Length..]);
                    if (!value.Any(char.IsControl))
                    {
                        result.Add(new(name, value, offset));
                        seen.Add(offset);
                    }
                }
                catch (DecoderFallbackException)
                {
                }
            }
        }
    }
}