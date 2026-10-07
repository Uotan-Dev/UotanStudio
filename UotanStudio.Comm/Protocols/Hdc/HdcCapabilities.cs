using System.Globalization;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Upstream base.h capability TLV: 16-byte tag, 16-byte decimal byte length, UTF-8 value.</summary>
internal static class HdcCapabilities
{
    internal static Dictionary<string, string> Decode(ReadOnlySpan<byte> bytes)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        while (!bytes.IsEmpty)
        {
            if (bytes.Length < 32) throw new InvalidDataException("Truncated HDC capability header.");
            var tag = Encoding.ASCII.GetString(bytes[..16].TrimEnd((byte)' '));
            if (tag.Length == 0 || !int.TryParse(bytes.Slice(16, 16).TrimEnd((byte)' '),
                NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > bytes.Length - 32)
                throw new InvalidDataException("Invalid HDC capability length.");
            if (!values.TryAdd(tag, Encoding.UTF8.GetString(bytes.Slice(32, length))))
                throw new InvalidDataException("Duplicate HDC capability.");
            bytes = bytes[(32 + length)..];
        }
        return values;
    }
}