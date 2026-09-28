using UotanStudio.Firmware.AndroidMeta.Dex;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class SmaliAssembler
{
    private static bool IsDebugDirective(string line) =>
        line.StartsWith(".line ", StringComparison.Ordinal)
        || line.StartsWith(".local ", StringComparison.Ordinal)
        || line.StartsWith(".end local ", StringComparison.Ordinal)
        || line.StartsWith(".restart local ", StringComparison.Ordinal)
        || line.StartsWith(".source ", StringComparison.Ordinal)
        || line is ".prologue" or ".epilogue" or ".source";

    private sealed partial class Context
    {
        private void EncodeDebugInfo(MethodState state, MethodLayout layout)
        {
            if (layout.DebugEntries.Count == 0)
            {
                EncodeParameterNames(state);
                return;
            }
            using var stream = new MemoryStream();
            void U(uint value)
            {
                Span<byte> bytes = stackalloc byte[5];
                stream.Write(bytes[..Leb128.WriteUleb128(bytes, 0, value)]);
            }
            void S(int value)
            {
                Span<byte> bytes = stackalloc byte[5];
                stream.Write(bytes[..Leb128.WriteSleb128(bytes, 0, value)]);
            }
            int Register(string text)
            {
                if (text.Length < 2 || text[0] is not ('v' or 'p')
                    || !int.TryParse(text.AsSpan(1), out var register) || register < 0)
                {
                    throw new FormatException("Invalid debug register.");
                }
                if (text[0] == 'p')
                {
                    register = checked(register + state.Code.RegistersSize - state.Code.InsSize);
                }
                if (register >= state.Code.RegistersSize)
                {
                    throw new FormatException("Debug register exceeds the method's register count.");
                }
                return register;
            }
            static int LineNumber(string directive)
            {
                var value = ParseNumber(Token(directive, ".line"));
                if (value < int.MinValue || value > uint.MaxValue)
                {
                    throw new FormatException("A debug line number must fit 32 bits.");
                }
                return unchecked((int)value);
            }
            var firstLine = layout.DebugEntries.FirstOrDefault(entry => entry.Line.StartsWith(".line ", StringComparison.Ordinal)).Line;
            var lineNumber = firstLine is null ? 0 : LineNumber(firstLine);
            U(unchecked((uint)lineNumber));
            var count = Protos[Methods[state.Encoded.MethodIndex].ProtoIndex].ParameterTypeIndices.Length;
            U((uint)count);
            for (var i = 0; i < count; i++)
            {
                U(state.ParameterNames.TryGetValue(i, out var name) ? checked((uint)String(name) + 1) : 0);
            }
            var address = 0;
            foreach (var entry in layout.DebugEntries)
            {
                if (entry.Address != address)
                {
                    stream.WriteByte(1);
                    U(checked((uint)(entry.Address - address)));
                    address = entry.Address;
                }
                var line = entry.Line;
                if (line.StartsWith(".line ", StringComparison.Ordinal))
                {
                    var next = LineNumber(line);
                    var delta = unchecked(next - lineNumber);
                    if (delta is < -4 or > 10)
                    {
                        stream.WriteByte(2);
                        S(delta);
                        delta = 0;
                    }
                    stream.WriteByte((byte)(14 + delta));
                    lineNumber = next;
                }
                else if (line is ".prologue" or ".epilogue")
                {
                    stream.WriteByte(line == ".prologue" ? (byte)7 : (byte)8);
                }
                else if (line == ".source" || line.StartsWith(".source ", StringComparison.Ordinal))
                {
                    var text = Token(line, ".source");
                    stream.WriteByte(9);
                    U(text.Length == 0 ? 0 : checked((uint)String(SmaliSyntax.StringLiteral(text)) + 1));
                }
                else if (line.StartsWith(".local ", StringComparison.Ordinal))
                {
                    var args = SmaliSyntax.Operands(Token(line, ".local"));
                    if (args.Length is < 1 or > 3)
                    {
                        throw new FormatException("A debug local requires a register, name:type and optional signature.");
                    }
                    if (args.Length == 1)
                    {
                        stream.WriteByte(3);
                        U((uint)Register(args[0]));
                        U(0);
                        U(0);
                        continue;
                    }
                    var colon = args[1].LastIndexOf(':');
                    if (colon < 0)
                    {
                        throw new FormatException("A debug local must declare name:type.");
                    }
                    var name = args[1][..colon].Trim();
                    var type = args[1][(colon + 1)..].Trim();
                    if (type != "V")
                    {
                        type = SmaliTypes.Single(type, allowVoid: false);
                    }
                    stream.WriteByte(args.Length == 3 ? (byte)4 : (byte)3);
                    U((uint)Register(args[0]));
                    U(name == "null" ? 0 : checked((uint)String(SmaliSyntax.StringLiteral(name)) + 1));
                    U(type == "V" ? 0 : checked((uint)Type(type) + 1));
                    if (args.Length == 3)
                    {
                        U(checked((uint)String(SmaliSyntax.StringLiteral(args[2])) + 1));
                    }
                }
                else
                {
                    var end = line.StartsWith(".end local ", StringComparison.Ordinal);
                    stream.WriteByte(end ? (byte)5 : (byte)6);
                    U((uint)Register(Token(line, end ? ".end local" : ".restart local")));
                }
            }
            stream.WriteByte(0);
            state.Code.DebugInfo = stream.ToArray();
        }
    }
}