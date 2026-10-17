using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class SmaliAssembler
{
    private sealed partial class Context
    {
        private string MinimumVersion { get; set; } = "035";

        private int ResolveReference(string text, RefKind kind, string? declaringClass = null)
        {
            switch (kind)
            {
                case RefKind.String:
                    return String(SmaliSyntax.StringLiteral(text));
                case RefKind.Type:
                    return Type(SmaliTypes.Single(text, allowVoid: false));
                case RefKind.Proto:
                    return Prototype(text);
                case RefKind.MethodHandle:
                    return MethodHandle(text);
                case RefKind.CallSite:
                    return CallSite(text, declaringClass ?? throw new FormatException("A call site requires its declaring class context."));
                case RefKind.Field:
                case RefKind.Method:
                    var arrow = text.IndexOf("->", StringComparison.Ordinal);
                    if (arrow < 1)
                    {
                        throw new FormatException("A member reference must contain its declaring type and '->'.");
                    }
                    var owner = SmaliTypes.Single(text[..arrow], allowVoid: false);
                    if (owner[0] != 'L')
                    {
                        throw new FormatException("A member's declaring type must be a class.");
                    }
                    var member = text[(arrow + 2)..];
                    if (kind == RefKind.Field)
                    {
                        var colon = member.IndexOf(':');
                        if (colon < 1)
                        {
                            throw new FormatException("A field reference must contain its name and type.");
                        }
                        return Field(new FieldId
                        {
                            ClassIndex = Type(owner),
                            NameIndex = String(member[..colon]),
                            TypeIndex = Type(SmaliTypes.Single(member[(colon + 1)..], allowVoid: false))
                        });
                    }
                    var open = member.IndexOf('(');
                    if (open < 1)
                    {
                        throw new FormatException("A method reference must contain its name and signature.");
                    }
                    return Method(new MethodId
                    {
                        ClassIndex = Type(owner),
                        NameIndex = String(member[..open]),
                        ProtoIndex = Prototype(member[open..])
                    });
                default:
                    throw new FormatException($"Unsupported constant-pool reference kind {kind}.");
            }
        }

        private int MethodHandle(string text)
        {
            var separator = text.IndexOf('@');
            if (separator <= 0)
            {
                throw new FormatException("A method handle requires its kind and member separated by '@'.");
            }
            var kind = SmaliMethodHandles.Kind(text[..separator]);
            var member = ResolveReference(text[(separator + 1)..], kind <= 3 ? RefKind.Field : RefKind.Method);
            if (member > ushort.MaxValue)
            {
                throw new FormatException("A method-handle member index exceeds 16 bits.");
            }
            if (MinimumVersion == "035")
            {
                MinimumVersion = "038";
            }
            if (MethodHandleIndices.TryGetValue((kind, member), out var existing))
            {
                return existing;
            }
            MethodHandleIndices.Add((kind, member), MethodHandles.Count);
            MethodHandles.Add(new MethodHandleDefinition { Kind = kind, MemberIndex = (ushort)member });
            return MethodHandles.Count - 1;
        }

        private int Prototype(string signature)
        {
            var close = signature.IndexOf(')');
            if (!signature.StartsWith('(') || close < 1 || close == signature.Length - 1)
            {
                throw new FormatException("Invalid method prototype.");
            }
            var args = SmaliTypes.Parameters(signature[1..close]).Select(Type).ToArray();
            var returnDescriptor = SmaliTypes.Single(signature[(close + 1)..], allowVoid: true);
            var ret = Type(returnDescriptor);
            if (ProtoIndices.TryGetValue(new ProtoKey(ret, args), out var existing))
            {
                return existing;
            }
            var shorty = new string([SmaliTypes.Shorty(returnDescriptor), .. args.Select(i => SmaliTypes.Shorty(Types[i]))]);
            ProtoIndices.Add(new ProtoKey(ret, args), Protos.Count);
            Protos.Add(new ProtoDefinition { ReturnTypeIndex = ret, ParameterTypeIndices = args, ShortyIndex = String(shorty) });
            return Protos.Count - 1;
        }

        private static int[] RegisterList(string text, bool range, Func<string, int, int> parse)
        {
            if (!text.StartsWith('{') || !text.EndsWith('}'))
            {
                throw new FormatException("An invocation register list must be enclosed in braces.");
            }
            var body = text[1..^1].Trim();
            if (body.Length == 0)
            {
                return [];
            }
            if (range)
            {
                var limits = body.Split("..", StringSplitOptions.TrimEntries);
                if (limits.Length != 2)
                {
                    throw new FormatException("A range register list must specify first .. last.");
                }
                var first = parse(limits[0], 16);
                var last = parse(limits[1], 16);
                var count = last - first + 1;
                if (count < 1 || count > byte.MaxValue)
                {
                    throw new FormatException("A range invocation must contain between one and 255 register words.");
                }
                return [.. Enumerable.Range(first, count)];
            }
            var values = body.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length > 5)
            {
                throw new FormatException("A fixed invocation register list cannot exceed five register words.");
            }
            return [.. values.Select(value => parse(value, 4))];
        }
    }
}