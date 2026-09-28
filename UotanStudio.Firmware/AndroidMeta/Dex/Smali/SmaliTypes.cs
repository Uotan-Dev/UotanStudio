namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

internal static class SmaliTypes
{
    internal static char Shorty(string descriptor) => descriptor[0] is '[' or 'L' ? 'L' : descriptor[0];

    internal static string[] Parameters(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        if (text.Contains(',') || IsReadable(text.Trim()))
        {
            return text.Split(',').Select(t => Single(t.Trim(), allowVoid: false)).ToArray();
        }
        var descriptors = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            ReadDescriptor(text, ref position, allowVoid: false);
            descriptors.Add(text[start..position]);
        }
        return [.. descriptors];
    }

    internal static string Single(string text, bool allowVoid)
    {
        if (text.Length == 0)
        {
            throw new FormatException("A type descriptor is required.");
        }
        if (IsReadable(text))
        {
            var dimensions = 0;
            while (text.EndsWith("[]", StringComparison.Ordinal))
            {
                dimensions++;
                text = text[..^2];
            }
            text = new string('[', dimensions) + (text switch
            {
                "void" => "V",
                "boolean" => "Z",
                "byte" => "B",
                "short" => "S",
                "char" => "C",
                "int" => "I",
                "long" => "J",
                "float" => "F",
                "double" => "D",
                _ => "L" + text.Replace('.', '/') + ";"
            });
        }
        var position = 0;
        ReadDescriptor(text, ref position, allowVoid);
        if (position != text.Length)
        {
            throw new FormatException("A single type cannot contain multiple descriptors.");
        }
        return text;
    }

    private static bool IsReadable(string text) =>
        text is "void" or "boolean" or "byte" or "short" or "char" or "int" or "long" or "float" or "double"
        || text.EndsWith("[]", StringComparison.Ordinal)
        || text.Contains('.') && !text.StartsWith('L') && !text.StartsWith('[');

    private static void ReadDescriptor(string text, ref int position, bool allowVoid)
    {
        var dimensions = 0;
        while (position < text.Length && text[position] == '[')
        {
            dimensions++;
            position++;
        }
        if (dimensions > 255 || position == text.Length)
        {
            throw new FormatException("Invalid array type descriptor.");
        }
        var kind = text[position++];
        if (kind == 'L')
        {
            var end = text.IndexOf(';', position);
            if (end <= position)
            {
                throw new FormatException("An object descriptor must contain a name and terminate with ';'.");
            }
            var name = text.AsSpan(position, end - position);
            if (name[0] == '/' || name[^1] == '/' || name.Contains("//", StringComparison.Ordinal))
            {
                throw new FormatException("An object descriptor cannot contain an empty name component.");
            }
            foreach (var c in name)
            {
                if (char.IsWhiteSpace(c) || c is '.' or '[' or '(' or ')' or ',')
                {
                    throw new FormatException("Invalid character in an object descriptor.");
                }
            }
            position = end + 1;
            return;
        }
        if (kind is 'Z' or 'B' or 'S' or 'C' or 'I' or 'J' or 'F' or 'D'
            || kind == 'V' && allowVoid && dimensions == 0)
        {
            return;
        }
        throw new FormatException($"Invalid type descriptor '{text}'.");
    }
}