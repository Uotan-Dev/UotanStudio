using System.Globalization;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>A bounded sector address grammar; disk-relative addresses are evaluated by the target.</summary>
internal readonly record struct FirehoseSectorAddress(long Value, bool RelativeToEnd)
{
    internal static FirehoseSectorAddress Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 64)
        {
            throw new ArgumentException("The sector address is too long.", nameof(text));
        }

        var token = text.AsSpan().Trim();
        if (token.EndsWith(".", StringComparison.Ordinal))
        {
            token = token[..^1];
        }

        var relative = token.StartsWith("NUM_DISK_SECTORS-", StringComparison.Ordinal);
        if (relative)
        {
            token = token["NUM_DISK_SECTORS-".Length..];
        }

        var hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex)
        {
            token = token[2..];
        }

        if (!long.TryParse(token, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) || value < 0 || (relative && value == 0))
        {
            throw new ArgumentException("Expected a nonnegative sector or NUM_DISK_SECTORS minus a positive offset.", nameof(text));
        }

        return new FirehoseSectorAddress(value, relative);
    }

    internal void ValidateRange(long sectors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectors);
        if (RelativeToEnd)
        {
            if (sectors > Value)
            {
                throw new ArgumentOutOfRangeException(nameof(sectors), "The disk-relative range extends beyond the end of storage.");
            }
        }
        else
        {
            _ = checked(Value + sectors);
        }
    }

    public override string ToString() => RelativeToEnd
        ? $"NUM_DISK_SECTORS-{Value.ToString(CultureInfo.InvariantCulture)}."
        : Value.ToString(CultureInfo.InvariantCulture);
}