using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

internal static class SmaliSyntax
{
    internal static IEnumerable<string> AnnotationTokens(string line)
    {
        var position = 0;
        while (position < line.Length)
        {
            if (char.IsWhiteSpace(line[position]))
            {
                position++;
                continue;
            }
            var start = position;
            var current = line[position++];
            if (current is '"' or '\'')
            {
                var escaped = false;
                var closed = false;
                while (position < line.Length)
                {
                    var value = line[position++];
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (value == '\\')
                    {
                        escaped = true;
                    }
                    else if (value == current)
                    {
                        closed = true;
                        break;
                    }
                }
                if (!closed)
                {
                    throw new FormatException("An annotation literal is missing its closing quote.");
                }
            }
            else if (current is not ('{' or '}' or '=' or ','))
            {
                while (position < line.Length && !char.IsWhiteSpace(line[position])
                    && line[position] is not ('{' or '}' or '=' or ',' or '"' or '\''))
                {
                    position++;
                }
            }
            yield return line[start..position];
        }
    }

    internal static string StripComment(string text)
    {
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#')
            {
                return text[..i];
            }
        }
        return text;
    }

    internal static string[] Operands(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var values = new List<string>();
        var start = 0;
        var braces = 0;
        var parentheses = 0;
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    break;
                case '{': braces++; break;
                case '}': braces--; break;
                case '(': parentheses++; break;
                case ')': parentheses--; break;
                case ',' when braces == 0 && parentheses == 0:
                    values.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
            if (braces < 0 || parentheses < 0)
            {
                throw new FormatException("Unbalanced instruction operand.");
            }
        }
        if (quote != '\0' || braces != 0 || parentheses != 0)
        {
            throw new FormatException("Unterminated instruction operand.");
        }
        values.Add(text[start..].Trim());
        if (values.Any(string.IsNullOrEmpty))
        {
            throw new FormatException("An instruction operand is empty.");
        }
        return [.. values];
    }

    internal static string StringLiteral(string text) => QuotedLiteral(text, '"');

    internal static int CloseParenthesis(string text, int open)
    {
        var close = new ParenthesisScanner().Scan(text.AsSpan(open));
        return close < 0 ? -1 : open + close;
    }

    internal sealed class ParenthesisScanner
    {
        private int _depth;
        private char _quote;
        private bool _escaped;

        internal int Scan(ReadOnlySpan<char> text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (_quote != '\0')
                {
                    if (_escaped)
                    {
                        _escaped = false;
                    }
                    else if (c == '\\')
                    {
                        _escaped = true;
                    }
                    else if (c == _quote)
                    {
                        _quote = '\0';
                    }
                    continue;
                }
                if (c is '"' or '\'')
                {
                    _quote = c;
                }
                else if (c == '(')
                {
                    _depth++;
                }
                else if (c == ')' && --_depth == 0)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    internal static char CharacterLiteral(string text)
    {
        var value = QuotedLiteral(text, '\'');
        if (value.Length != 1)
        {
            throw new FormatException("A character literal must contain exactly one UTF-16 code unit.");
        }
        return value[0];
    }

    private static string QuotedLiteral(string text, char quote)
    {
        if (text.Length < 2 || text[0] != quote || text[^1] != quote)
        {
            throw new FormatException("A string literal must be quoted.");
        }
        var result = new StringBuilder(text.Length - 2);
        for (var i = 1; i < text.Length - 1; i++)
        {
            var c = text[i];
            if (c == quote)
            {
                throw new FormatException("An embedded quote must be escaped.");
            }
            if (c != '\\')
            {
                result.Append(c);
                continue;
            }
            if (++i >= text.Length - 1)
            {
                throw new FormatException("Unterminated string escape.");
            }
            var escape = text[i];
            if (escape == 'u')
            {
                if (i + 4 >= text.Length - 1
                    || !ushort.TryParse(text.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                {
                    throw new FormatException("A Unicode escape must contain four hexadecimal digits.");
                }
                result.Append((char)code);
                i += 4;
            }
            else
            {
                result.Append(escape switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    _ => throw new FormatException($"Unknown string escape '\\{escape}'.")
                });
            }
        }
        return result.ToString();
    }
}