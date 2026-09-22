using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Text operations use explicit anchors; insertion never relies on line numbers.</summary>
public enum TextPatchOperation
{
    Replace,
    InsertBefore,
    InsertAfter,
    Delete
}

/// <summary>An immutable transformation result; publication is a separate operation.</summary>
public sealed record TextPatchResult(string Text, int Matches, bool Changed);

/// <summary>Bounded, deterministic text patching with exact match-count guards.</summary>
public static class TextPatch
{
    /// <summary>Maximum configuration text size, in UTF-16 code units.</summary>
    public const int MaxCharacters = 4 * 1024 * 1024;

    /// <summary>Applies non-overlapping matches. Replacement is literal, including for regex searches.</summary>
    public static TextPatchResult Apply(string text, string find, string replacement,
        TextPatchOperation operation, bool regex, int expectedMatches, CancellationToken cancellationToken = default)
    {
        ValidateSize(text);
        ValidateSize(replacement);
        ArgumentException.ThrowIfNullOrEmpty(find);
        if (find.Length > 16_384 || expectedMatches is < 0 or > 100_000 || !Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedMatches));
        }

        var expression = new Regex(regex ? find : Regex.Escape(find), RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var output = new StringBuilder(Math.Min(text.Length, 65_536));
        var position = 0;
        var count = 0;
        foreach (Match match in expression.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match.Length == 0)
            {
                throw new InvalidDataException("A patch anchor must not match an empty string.");
            }
            if (++count > expectedMatches)
            {
                throw new InvalidDataException($"Expected {expectedMatches} matches; found more.");
            }
            Append(output, text.AsSpan(position, match.Index - position));
            if (operation == TextPatchOperation.InsertAfter)
            {
                Append(output, match.ValueSpan);
            }
            if (operation != TextPatchOperation.Delete)
            {
                Append(output, replacement);
            }
            if (operation == TextPatchOperation.InsertBefore)
            {
                Append(output, match.ValueSpan);
            }
            position = match.Index + match.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (count != expectedMatches)
        {
            throw new InvalidDataException($"Expected {expectedMatches} matches; found {count}.");
        }
        Append(output, text.AsSpan(position));
        var result = output.ToString();
        return new TextPatchResult(result, count, !string.Equals(text, result, StringComparison.Ordinal));
    }

    internal static void ValidateSize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxCharacters)
        {
            throw new InvalidDataException("Configuration text exceeds the patch size limit.");
        }
    }

    private static void Append(StringBuilder builder, ReadOnlySpan<char> value)
    {
        if (value.Length > MaxCharacters - builder.Length)
        {
            throw new InvalidDataException("Patched text exceeds the patch size limit.");
        }
        builder.Append(value);
    }
}