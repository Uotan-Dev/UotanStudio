using System.Globalization;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class SmaliAssembler
{
    private sealed record SwitchCase(int Key, string? Target, int Offset = 0);

    private sealed class PayloadBlock(string kind, int argument, bool rawOffsets = false)
    {
        public string Kind { get; } = kind;
        public bool RawOffsets { get; } = rawOffsets;
        public string Directive => RawOffsets ? Kind + "-raw" : Kind;
        public int Argument { get; } = argument;
        public List<long> Elements { get; } = [];
        public List<SwitchCase> Cases { get; } = [];
        public int Size => checked(Kind switch
        {
            "packed-switch" => 4 + Cases.Count * 2,
            "sparse-switch" => 2 + Cases.Count * 4,
            _ => (int)(4 + ((long)Argument * Elements.Count + 1) / 2)
        });
    }

    private sealed record LayoutEntry(int Address, string Line, PayloadBlock? Payload = null, int SourceIndex = -1);

    private sealed class MethodLayout
    {
        public Dictionary<string, int> Labels { get; } = new(StringComparer.Ordinal);
        public List<LayoutEntry> Entries { get; } = [];
        public List<(int Address, string Line)> DebugEntries { get; } = [];
        public SortedSet<int> Boundaries { get; } = [];
        public HashSet<int> Instructions { get; } = [];
        public int Size { get; set; }
    }

    private sealed class PayloadBindings
    {
        public Dictionary<int, int> Owners { get; } = [];
        public Dictionary<int, int> Targets { get; } = [];
    }

    private static bool ParsePayloadLine(MethodState state, string line)
    {
        if (state.OpenPayload is { } payload)
        {
            if (line == ".end " + payload.Directive)
            {
                if (payload.Kind == "sparse-switch")
                {
                    payload.Cases.Sort((left, right) => left.Key.CompareTo(right.Key));
                    for (var i = 1; i < payload.Cases.Count; i++)
                    {
                        if (payload.Cases[i - 1].Key == payload.Cases[i].Key)
                        {
                            throw new FormatException("A sparse-switch payload contains a duplicate key.");
                        }
                    }
                }
                state.OpenPayload = null;
                return true;
            }
            if (payload.Kind == "array-data")
            {
                payload.Elements.Add(PayloadElement(line, payload.Argument));
                return true;
            }
            if (payload.Cases.Count == ushort.MaxValue)
            {
                throw new FormatException("A switch payload cannot exceed 65535 cases.");
            }
            if (payload.Kind == "packed-switch")
            {
                var key = (long)payload.Argument + payload.Cases.Count;
                if (key > int.MaxValue)
                {
                    throw new FormatException("The packed-switch key range overflows a signed 32-bit integer.");
                }
                payload.Cases.Add(ParseSwitchCase(payload, (int)key, line));
                return true;
            }
            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                throw new FormatException("A sparse-switch entry requires a key and a target label.");
            }
            payload.Cases.Add(ParseSwitchCase(payload, PayloadKey(line[..arrow].Trim()), line[(arrow + 2)..].Trim()));
            return true;
        }

        var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts[0] is not (".packed-switch" or ".sparse-switch" or ".array-data" or ".packed-switch-raw" or ".sparse-switch-raw"))
        {
            return false;
        }
        var rawOffsets = parts[0].EndsWith("-raw", StringComparison.Ordinal);
        var kind = rawOffsets ? parts[0][1..^4] : parts[0][1..];
        if (parts.Length != (kind == "sparse-switch" ? 1 : 2))
        {
            throw new FormatException("Invalid payload declaration.");
        }
        var argument = kind == "sparse-switch" ? 0 : PayloadKey(parts[1]);
        if (kind == "array-data" && argument is not (1 or 2 or 4 or 8))
        {
            throw new FormatException("An array-data element width must be 1, 2, 4 or 8 bytes.");
        }
        state.OpenPayload = new PayloadBlock(kind, argument, rawOffsets);
        state.Payloads.Add(state.Lines.Count, state.OpenPayload);
        state.Lines.Add(line);
        return true;
    }

    private static SwitchCase ParseSwitchCase(PayloadBlock payload, int key, string target)
    {
        if (!payload.RawOffsets)
        {
            return new SwitchCase(key, PayloadLabel(target));
        }
        if (!int.TryParse(target, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset))
        {
            throw new FormatException("A raw switch target requires a signed 32-bit decimal code-unit offset.");
        }
        return new SwitchCase(key, null, offset);
    }

    private static string PayloadLabel(string value)
    {
        if (value.Length < 2 || value[0] != ':' || value.Any(char.IsWhiteSpace))
        {
            throw new FormatException($"Invalid payload label '{value}'.");
        }
        return value;
    }

    private static int PayloadKey(string value)
    {
        var number = SmaliLiterals.Fixed(value, 32);
        return unchecked((int)number);
    }

    private static long PayloadElement(string value, int width)
    {
        var bits = SmaliLiterals.Bits(SmaliLiterals.Scalar(value));
        ValidateWidth(bits, width);
        return bits;
    }

    private static void ValidateWidth(long value, int width)
    {
        if (width < 8 && (value < -(1L << (width * 8 - 1)) || value >= 1L << (width * 8)))
        {
            throw new FormatException($"The array element does not fit {width} bytes.");
        }
    }

    private static MethodLayout LayoutMethod(MethodState state, Func<string, int> stringIndex)
    {
        var replacements = new Dictionary<int, string>();
        while (true)
        {
            var layout = LayoutPass(state, replacements);
            var changed = false;
            foreach (var entry in layout.Entries)
            {
                if (entry.Payload is not null || entry.SourceIndex < 0)
                {
                    continue;
                }
                var parts = entry.Line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "const-string")
                {
                    var operands = SmaliSyntax.Operands(parts[1]);
                    if (operands.Length != 2)
                    {
                        throw new FormatException("const-string requires a register and a string literal.");
                    }
                    if (stringIndex(operands[1]) > ushort.MaxValue)
                    {
                        replacements[entry.SourceIndex] = "const-string/jumbo " + parts[1];
                        changed = true;
                    }
                    continue;
                }
                if (parts.Length != 2 || parts[0] is not ("goto" or "goto/16"))
                {
                    continue;
                }
                var offset = (long)ResolveAddress(parts[1], entry.Address, layout) - entry.Address;
                var widened = offset is < short.MinValue or > short.MaxValue ? "goto/32"
                    : offset is < sbyte.MinValue or > sbyte.MaxValue ? "goto/16" : parts[0];
                if (widened != parts[0])
                {
                    replacements[entry.SourceIndex] = widened + " " + parts[1];
                    changed = true;
                }
            }
            if (!changed)
            {
                ValidateBranchTargets(layout);
                return layout;
            }
        }
    }

    private static MethodLayout LayoutPass(MethodState state, Dictionary<int, string> replacements)
    {
        var result = new MethodLayout();
        var pendingLabels = new List<string>();
        for (var index = 0; index < state.Lines.Count; index++)
        {
            var line = replacements.GetValueOrDefault(index, state.Lines[index]);
            if (IsDebugDirective(line))
            {
                result.DebugEntries.Add((result.Size, line));
                continue;
            }
            if (line.StartsWith(':'))
            {
                var name = PayloadLabel(line)[1..];
                if (!result.Labels.TryAdd(name, result.Size))
                {
                    throw new FormatException($"Duplicate label: {line}");
                }
                pendingLabels.Add(name);
                continue;
            }
            if (state.Payloads.TryGetValue(index, out var payload))
            {
                if ((result.Size & 1) != 0)
                {
                    result.Boundaries.Add(result.Size);
                    result.Instructions.Add(result.Size);
                    result.Entries.Add(new LayoutEntry(result.Size++, "nop"));
                    foreach (var name in pendingLabels)
                    {
                        result.Labels[name] = result.Size;
                    }
                }
                result.Boundaries.Add(result.Size);
                result.Entries.Add(new LayoutEntry(result.Size, line, payload));
                result.Size = checked(result.Size + payload.Size);
            }
            else
            {
                var name = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
                var op = OpCodes.Find(name) ?? throw new FormatException($"Unknown instruction: {line}");
                result.Boundaries.Add(result.Size);
                result.Instructions.Add(result.Size);
                result.Entries.Add(new LayoutEntry(result.Size, line, SourceIndex: index));
                result.Size = checked(result.Size + OpCodes.GetSize(op.Opcode));
            }
            pendingLabels.Clear();
        }
        result.Boundaries.Add(result.Size);
        return result;
    }

    private static void ValidateBranchTargets(MethodLayout layout)
    {
        foreach (var entry in layout.Entries)
        {
            if (entry.Payload is not null)
            {
                continue;
            }
            var parts = entry.Line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            var format = OpCodes.Find(parts[0])!.Value.Info.Format;
            var count = format switch
            {
                InstructionFormat.Format10t or InstructionFormat.Format20t or InstructionFormat.Format30t => 1,
                InstructionFormat.Format21t => 2,
                InstructionFormat.Format22t => 3,
                _ => 0
            };
            if (count == 0)
            {
                continue;
            }
            var operands = parts.Length == 1 ? [] : SmaliSyntax.Operands(parts[1]);
            if (operands.Length != count)
            {
                throw new FormatException("Invalid branch operand count.");
            }
            var target = ResolveAddress(operands[^1], entry.Address, layout);
            if (!layout.Instructions.Contains(target) || target == entry.Address && format != InstructionFormat.Format30t)
            {
                throw new FormatException("A branch must target an executable instruction and use a permitted nonzero offset.");
            }
        }
    }

    private static PayloadBindings BindPayloads(MethodLayout layout)
    {
        var payloads = layout.Entries.Where(entry => entry.Payload is not null).ToDictionary(entry => entry.Address, entry => entry.Payload!);
        var result = new PayloadBindings();
        foreach (var entry in layout.Entries.ToArray())
        {
            if (entry.Payload is not null)
            {
                continue;
            }
            var parts = entry.Line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            var op = OpCodes.Find(parts[0])!.Value;
            if (op.Info.Format != InstructionFormat.Format31t)
            {
                continue;
            }
            var operands = parts.Length == 1 ? [] : SmaliSyntax.Operands(parts[1]);
            if (operands.Length != 2)
            {
                throw new FormatException("A payload instruction requires a register and a target.");
            }
            var target = ResolveAddress(operands[1], entry.Address, layout);
            var kind = op.Opcode switch { 0x26 => "array-data", 0x2b => "packed-switch", _ => "sparse-switch" };
            if (!payloads.TryGetValue(target, out var payload) || payload.Kind != kind)
            {
                throw new FormatException("The instruction does not reference a payload of the required kind.");
            }
            if (kind != "array-data" && !result.Owners.TryAdd(target, entry.Address))
            {
                if ((layout.Size & 1) != 0)
                {
                    layout.Boundaries.Add(layout.Size);
                    layout.Instructions.Add(layout.Size);
                    layout.Entries.Add(new LayoutEntry(layout.Size++, "nop"));
                }
                var clone = layout.Size;
                layout.Boundaries.Add(clone);
                layout.Entries.Add(new LayoutEntry(clone, string.Empty, payload));
                layout.Size = checked(layout.Size + payload.Size);
                layout.Boundaries.Add(layout.Size);
                result.Owners.Add(clone, entry.Address);
                result.Targets.Add(entry.Address, clone);
            }
        }
        foreach (var (address, payload) in payloads)
        {
            if (payload.Kind != "array-data" && !payload.RawOffsets && !result.Owners.ContainsKey(address))
            {
                throw new FormatException("A switch payload requires a referring switch instruction.");
            }
        }
        return result;
    }

    private static int ResolveAddress(string operand, int origin, MethodLayout layout)
    {
        if (operand.StartsWith(':'))
        {
            if (layout.Labels.TryGetValue(operand[1..], out var target))
            {
                return target;
            }
            throw new FormatException($"Undefined label '{operand}'.");
        }
        var relative = ParseNumber(operand);
        if (relative < int.MinValue || relative > int.MaxValue || (long)origin + relative is < 0 or > int.MaxValue)
        {
            throw new FormatException("A branch offset exceeds the method address range.");
        }
        return origin + (int)relative;
    }

    private static void EmitPayload(List<ushort> raw, PayloadBlock payload, int? owner, MethodLayout layout)
    {
        void Int32(int value)
        {
            raw.Add(unchecked((ushort)value));
            raw.Add(unchecked((ushort)(value >> 16)));
        }
        if (payload.Kind == "array-data")
        {
            raw.Add(0x0300);
            raw.Add((ushort)payload.Argument);
            Int32(payload.Elements.Count);
            if (payload.Argument == 1)
            {
                for (var i = 0; i < payload.Elements.Count; i += 2)
                {
                    var low = (byte)payload.Elements[i];
                    var high = i + 1 < payload.Elements.Count ? (byte)payload.Elements[i + 1] : 0;
                    raw.Add((ushort)(low | high << 8));
                }
            }
            else
            {
                foreach (var element in payload.Elements)
                {
                    for (var word = 0; word < payload.Argument / 2; word++)
                    {
                        raw.Add(unchecked((ushort)(element >> (word * 16))));
                    }
                }
            }
            return;
        }
        raw.Add(payload.Kind == "packed-switch" ? (ushort)0x0100 : (ushort)0x0200);
        raw.Add((ushort)payload.Cases.Count);
        if (payload.Kind == "packed-switch")
        {
            Int32(payload.Argument);
        }
        else
        {
            foreach (var item in payload.Cases)
            {
                Int32(item.Key);
            }
        }
        foreach (var item in payload.Cases)
        {
            if (owner is not { } origin)
            {
                if (!payload.RawOffsets)
                {
                    throw new FormatException("A label-based switch payload requires a referring instruction.");
                }
                Int32(item.Offset);
                continue;
            }
            var target = payload.RawOffsets ? (long)origin + item.Offset : ResolveAddress(item.Target!, origin, layout);
            if (target is < 0 or > int.MaxValue || !layout.Instructions.Contains((int)target))
            {
                throw new FormatException("A switch case must target an executable instruction.");
            }
            Int32(checked((int)(target - origin)));
        }
    }
}