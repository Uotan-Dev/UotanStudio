using System.Globalization;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Upstream base.h capability TLV: 16-byte tag, 16-byte decimal byte length, UTF-8 value.</summary>
internal static class HdcCapabilities
{
    internal static Dictionary<string, string> Decode(ReadOnlySpan<byte> bytes)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (!bytes.IsEmpty)
        {
            if (bytes.Length < 32) throw new InvalidDataException("Truncated HDC capability header.");
            var tag = Encoding.ASCII.GetString(bytes[..16]).TrimEnd(' ');
            if (tag.Length == 0 || !int.TryParse(Encoding.ASCII.GetString(bytes.Slice(16, 16)).TrimEnd(' '),
                NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > bytes.Length - 32)
                throw new InvalidDataException("Invalid HDC capability length.");
            if (!values.TryAdd(tag, Encoding.UTF8.GetString(bytes.Slice(32, length))))
                throw new InvalidDataException("Duplicate HDC capability.");
            bytes = bytes[(32 + length)..];
        }
        return values;
    }
}
