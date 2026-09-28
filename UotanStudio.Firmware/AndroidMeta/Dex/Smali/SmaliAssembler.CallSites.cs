using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class SmaliAssembler
{
    private static string ReadCallSiteInstruction(string[] lines, ref int index)
    {
        var line = lines[index];
        var open = line.IndexOf('(');
        if (open < 0)
        {
            return line;
        }
        var scanner = new SmaliSyntax.ParenthesisScanner();
        if (scanner.Scan(line.AsSpan(open)) >= 0)
        {
            return line;
        }
        var text = new StringBuilder(line);
        while (++index < lines.Length && lines[index] != ".end method")
        {
            line = lines[index];
            text.Append(' ').Append(line);
            if (scanner.Scan(line) >= 0)
            {
                return text.ToString();
            }
        }
        throw new FormatException("Unterminated call-site instruction.");
    }

    private sealed class CallSiteState(string name, List<EncodedValue> values)
    {
        public string Name { get; } = name;
        public List<EncodedValue> Values { get; } = values;
    }

    private sealed partial class Context
    {
        private List<CallSiteState> CallSites { get; } = [];

        private int CallSite(string text, string owner)
        {
            var open = text.IndexOf('(');
            if (open < 1)
            {
                throw new FormatException("A call site requires a name and argument list.");
            }
            var name = text[..open].Trim();
            if (name.Any(c => char.IsWhiteSpace(c) || "(){}[],;:@\"'".Contains(c)))
            {
                throw new FormatException("Invalid call-site name.");
            }
            var close = SmaliSyntax.CloseParenthesis(text, open);
            if (close < 0)
            {
                throw new FormatException("Unterminated call-site argument list.");
            }
            var suffix = text[(close + 1)..].Trim();
            if (!suffix.StartsWith('@'))
            {
                throw new FormatException("A call site requires a bootstrap method after '@'.");
            }
            var method = suffix[1..].Trim();
            if (!method.Contains("->", StringComparison.Ordinal))
            {
                method = owner + "->" + method;
            }
            var args = SmaliSyntax.Operands(text[(open + 1)..close]);
            if (args.Length < 2)
            {
                throw new FormatException("A call site requires a method name and prototype.");
            }
            List<EncodedValue> values =
            [
                new(EncodedValueType.MethodHandle, MethodHandle("invoke-static@" + method)),
                EncodedValue.String(String(SmaliSyntax.StringLiteral(args[0]))),
                new(EncodedValueType.MethodType, Prototype(args[1]))
            ];
            foreach (var argument in args.Skip(2))
            {
                var value = new AnnotationParser(this, owner, [.. SmaliSyntax.AnnotationTokens(argument)]).ParseValue();
                SortValueAnnotations(value);
                values.Add(value);
            }
            for (var i = 0; i < CallSites.Count; i++)
            {
                var site = CallSites[i];
                if (site.Name == name && EqualValues(site.Values, values))
                {
                    return i;
                }
            }
            if (CallSites.Count > ushort.MaxValue)
            {
                throw new FormatException("A call-site reference exceeds the instruction's 16-bit index.");
            }
            CallSites.Add(new CallSiteState(name, values));
            return CallSites.Count - 1;
        }

        private static void SortValueAnnotations(EncodedValue value)
        {
            if (value.Type == EncodedValueType.Annotation)
            {
                var annotation = (AnnotationItem)value.Value!;
                annotation.Elements.Sort((left, right) => left.NameIndex.CompareTo(right.NameIndex));
                foreach (var element in annotation.Elements)
                {
                    SortValueAnnotations(element.Value);
                }
            }
            else if (value.Type == EncodedValueType.Array)
            {
                foreach (var item in value.AsArray()!)
                {
                    SortValueAnnotations(item);
                }
            }
        }

        private static bool EqualValues(IReadOnlyList<EncodedValue> left, IReadOnlyList<EncodedValue> right) =>
            left.Count == right.Count && left.Zip(right).All(pair => EqualValue(pair.First, pair.Second));

        private static bool EqualValue(EncodedValue left, EncodedValue right)
        {
            if (left.Type != right.Type)
            {
                return false;
            }
            return left.Type switch
            {
                EncodedValueType.Array => EqualValues(left.AsArray()!, right.AsArray()!),
                EncodedValueType.Annotation => EqualAnnotations((AnnotationItem)left.Value!, (AnnotationItem)right.Value!),
                EncodedValueType.Float => BitConverter.SingleToInt32Bits((float)left.Value!) == BitConverter.SingleToInt32Bits((float)right.Value!),
                EncodedValueType.Double => BitConverter.DoubleToInt64Bits((double)left.Value!) == BitConverter.DoubleToInt64Bits((double)right.Value!),
                _ => Equals(left.Value, right.Value)
            };
        }

        private static bool EqualAnnotations(AnnotationItem left, AnnotationItem right) =>
            left.TypeIndex == right.TypeIndex && left.Elements.Count == right.Elements.Count
            && left.Elements.OrderBy(element => element.NameIndex).Zip(right.Elements.OrderBy(element => element.NameIndex))
                .All(pair => pair.First.NameIndex == pair.Second.NameIndex && EqualValue(pair.First.Value, pair.Second.Value));
    }
}