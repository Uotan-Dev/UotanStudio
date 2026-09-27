using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

public static partial class SmaliAssembler
{
    private static string ReadFieldDeclaration(string[] lines, ref int index)
    {
        var line = lines[index];
        var equals = line.IndexOf('=');
        if (equals < 0)
        {
            return line;
        }
        var declaration = new StringBuilder(line);
        var value = line[(equals + 1)..];
        var depth = 0;
        var hasValue = false;
        var previous = string.Empty;
        while (true)
        {
            foreach (var token in SmaliSyntax.AnnotationTokens(value))
            {
                hasValue = true;
                if (token is "{" or ".subannotation")
                {
                    depth++;
                }
                else if (token == "}" || token == "subannotation" && previous == ".end")
                {
                    depth--;
                }
                if (depth is < 0 or > 64)
                {
                    throw new FormatException("Invalid field initializer nesting.");
                }
                previous = token;
            }
            if (hasValue && depth == 0)
            {
                return declaration.ToString();
            }
            if (++index >= lines.Length || lines[index] == ".end field")
            {
                throw new FormatException("Unterminated field initializer.");
            }
            value = lines[index];
            declaration.Append(' ').Append(value);
        }
    }

    private sealed partial class Context
    {
        private EncodedValue FieldValue(string descriptor, string? initializer, string owner)
        {
            if (initializer is null)
            {
                return descriptor[0] switch
                {
                    'Z' => EncodedValue.Boolean(false),
                    'B' => EncodedValue.Byte(0),
                    'S' => EncodedValue.Short(0),
                    'C' => EncodedValue.Char('\0'),
                    'I' => EncodedValue.Int(0),
                    'J' => EncodedValue.Long(0),
                    'F' => EncodedValue.Float(0),
                    'D' => EncodedValue.Double(0),
                    _ => EncodedValue.Null()
                };
            }
            if (initializer.Length == 0)
            {
                throw new FormatException("A field initializer cannot be empty.");
            }
            return new AnnotationParser(this, owner, [.. SmaliSyntax.AnnotationTokens(initializer)]).ParseValue();
        }
    }
}