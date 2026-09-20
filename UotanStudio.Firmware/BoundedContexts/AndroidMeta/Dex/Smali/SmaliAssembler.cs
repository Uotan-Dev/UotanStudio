using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Instructions;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

/// <summary>Assembles canonical smali text back into a DEX model.</summary>
public static class SmaliAssembler
{
    /// <summary>Parses text emitted by <see cref="Disassembler"/>.</summary>
    public static DexFile Assemble(string smali)
    {
        ArgumentNullException.ThrowIfNull(smali);
        var context = new Context();
        ClassState? cls = null;
        MethodState? method = null;
        foreach (var raw in smali.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0 || line.StartsWith("##", StringComparison.Ordinal)) continue;
            if (line.StartsWith(".class", StringComparison.Ordinal)) { cls = context.StartClass(line); method = null; continue; }
            if (cls is null) continue;
            if (line.StartsWith(".super", StringComparison.Ordinal)) { cls.Definition.SuperclassIndex = context.Type(Token(line, ".super")); continue; }
            if (line.StartsWith(".implements", StringComparison.Ordinal)) { cls.Interfaces.Add(context.Type(Token(line, ".implements"))); continue; }
            if (line.StartsWith(".source", StringComparison.Ordinal)) { cls.Definition.SourceFileIndex = context.String(Unquote(Token(line, ".source"))); continue; }
            if (line.StartsWith(".field", StringComparison.Ordinal)) { context.ParseField(cls, line); continue; }
            if (line.StartsWith(".method", StringComparison.Ordinal)) { method = context.StartMethod(cls, line); continue; }
            if (line.StartsWith(".end method", StringComparison.Ordinal)) { if (method is not null) context.FinishMethod(method); method = null; continue; }
            if (method is not null) ParseMethodLine(method, line);
        }
        if (context.Classes.Count == 0) throw new FormatException("No .class definition in smali text.");
        return context.Build();
    }

    private static void ParseMethodLine(MethodState method, string line)
    {
        if (line.StartsWith(".registers", StringComparison.Ordinal)) { method.Code.RegistersSize = checked((ushort)ParseNumber(Token(line, ".registers"))); return; }
        if (line.StartsWith(".locals", StringComparison.Ordinal) || line.StartsWith(":", StringComparison.Ordinal) || line.StartsWith(".line", StringComparison.Ordinal) || line.StartsWith(".local", StringComparison.Ordinal) || line.StartsWith(".param", StringComparison.Ordinal) || line.StartsWith(".prologue", StringComparison.Ordinal) || line.StartsWith(".epilogue", StringComparison.Ordinal) || line.StartsWith(".catch", StringComparison.Ordinal) || line.StartsWith(".debug", StringComparison.Ordinal)) return;
        method.Lines.Add(line);
    }

    private static string StripComment(string line)
        => line.TrimStart().StartsWith("#", StringComparison.Ordinal) ? string.Empty : line;
    private static string Token(string line, string directive) => line[directive.Length..].Trim();
    private static string Unquote(string text) => text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1].Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\") : text;
    private static long ParseNumber(string text)
    {
        text = text.Trim().TrimStart('#').TrimEnd('L');
        var sign = text.StartsWith('-') ? -1 : 1; if (sign < 0) text = text[1..];
        var style = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (style == NumberStyles.HexNumber) text = text[2..];
        return sign * long.Parse(text, style, CultureInfo.InvariantCulture);
    }
    private static uint Access(IEnumerable<string> values)
    {
        var result = 0u;
        foreach (var value in values) result |= value switch
        {
            "public" => AccessFlags.Public,
            "private" => AccessFlags.Private,
            "protected" => AccessFlags.Protected,
            "static" => AccessFlags.Static,
            "final" => AccessFlags.Final,
            "synchronized" => AccessFlags.Synchronized,
            "volatile" => AccessFlags.Volatile,
            "bridge" => AccessFlags.Bridge,
            "transient" => AccessFlags.Transient,
            "varargs" => AccessFlags.Varargs,
            "native" => AccessFlags.Native,
            "interface" => AccessFlags.Interface,
            "abstract" => AccessFlags.Abstract,
            "strictfp" => AccessFlags.Strict,
            "synthetic" => AccessFlags.Synthetic,
            "annotation" => AccessFlags.Annotation,
            "enum" => AccessFlags.Enum,
            "constructor" => AccessFlags.Constructor,
            _ => 0
        };
        return result;
    }

    private sealed class Context
    {
        public List<ClassState> Classes { get; } = [];
        public List<string> Strings { get; } = [];
        public List<string> Types { get; } = [];
        public List<ProtoDefinition> Protos { get; } = [];
        public List<FieldId> Fields { get; } = [];
        public List<MethodId> Methods { get; } = [];
        public int String(string value) { var i = Strings.IndexOf(value); if (i < 0) { i = Strings.Count; Strings.Add(value); } return i; }
        public int Type(string value) { var d = Descriptor(value); var i = Types.IndexOf(d); if (i < 0) { i = Types.Count; Types.Add(d); String(d); } return i; }
        public int Field(FieldId value) { var i = Fields.FindIndex(x => x.ClassIndex == value.ClassIndex && x.NameIndex == value.NameIndex && x.TypeIndex == value.TypeIndex); if (i < 0) { i = Fields.Count; Fields.Add(value); } return i; }
        public int Method(MethodId value) { var i = Methods.FindIndex(x => x.ClassIndex == value.ClassIndex && x.NameIndex == value.NameIndex && x.ProtoIndex == value.ProtoIndex); if (i < 0) { i = Methods.Count; Methods.Add(value); } return i; }
        public ClassState StartClass(string line) { var p = line[6..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries); var state = new ClassState(new ClassDefinition { TypeIndex = Type(p[^1]), AccessFlags = Access(p[..^1]) }); Classes.Add(state); return state; }
        public void ParseField(ClassState cls, string line)
        {
            var text = line[6..].Trim(); var eq = text.IndexOf('='); if (eq >= 0) text = text[..eq].Trim(); var p = text.Split(' ', StringSplitOptions.RemoveEmptyEntries); var spec = p[^1]; var colon = spec.IndexOf(':'); if (colon <= 0) throw new FormatException($"Invalid field: {line}");
            var id = new FieldId { ClassIndex = cls.Definition.TypeIndex, NameIndex = String(spec[..colon]), TypeIndex = Type(spec[(colon + 1)..]) }; var field = new EncodedField { FieldIndex = Field(id), AccessFlags = Access(p[..^1]) }; if ((field.AccessFlags & AccessFlags.Static) != 0) cls.Definition.StaticFields.Add(field); else cls.Definition.InstanceFields.Add(field);
        }
        public MethodState StartMethod(ClassState cls, string line)
        {
            var text = line[7..].Trim(); var p = text.Split(' ', StringSplitOptions.RemoveEmptyEntries); var sig = p[^1]; var open = sig.IndexOf('('); var close = sig.LastIndexOf(')'); if (open < 1 || close < open) throw new FormatException($"Invalid method: {line}");
            var args = ParseTypes(sig[(open + 1)..close]); var ret = Type(sig[(close + 1)..]); var proto = new ProtoDefinition { ReturnTypeIndex = ret, ParameterTypeIndices = args, ShortyIndex = String("V") }; Protos.Add(proto); var id = new MethodId { ClassIndex = cls.Definition.TypeIndex, NameIndex = String(sig[..open]), ProtoIndex = Protos.Count - 1 }; return new MethodState(cls, new EncodedMethod { MethodIndex = Method(id), AccessFlags = Access(p[..^1]), Code = new CodeItem() });
        }
        public void FinishMethod(MethodState state)
        {
            if (state.Lines.Count > 0) Encode(state);
            if ((state.Encoded.AccessFlags & (AccessFlags.Abstract | AccessFlags.Native)) != 0) return;
            var target = (state.Encoded.AccessFlags & (AccessFlags.Private | AccessFlags.Static | AccessFlags.Constructor)) != 0
                ? state.Class.Definition.DirectMethods : state.Class.Definition.VirtualMethods;
            target.Add(state.Encoded);
        }
        public DexFile Build() { var dex = new DexFile(); dex.Strings.AddRange(Strings); dex.Types.AddRange(Types); dex.Protos.AddRange(Protos); dex.Fields.AddRange(Fields); dex.Methods.AddRange(Methods); foreach (var c in Classes) { c.Definition.InterfaceTypeIndices = [.. c.Interfaces]; dex.Classes.Add(c.Definition); } return dex; }
        private int[] ParseTypes(string text) => string.IsNullOrWhiteSpace(text) ? [] : text.Split(',', StringSplitOptions.TrimEntries).Select(Type).ToArray();
        private static string Descriptor(string value) { value = value.Trim(); if (value is "void" or "V") return "V"; if (value is "boolean" or "Z") return "Z"; if (value is "byte" or "B") return "B"; if (value is "short" or "S") return "S"; if (value is "char" or "C") return "C"; if (value is "int" or "I") return "I"; if (value is "long" or "J") return "J"; if (value is "float" or "F") return "F"; if (value is "double" or "D") return "D"; if (value.StartsWith('[')) return "[" + Descriptor(value[1..]); if (value.StartsWith('L') && value.EndsWith(';')) return value; if (value.EndsWith("[]")) return "[" + Descriptor(value[..^2]); return "L" + value.Replace('.', '/') + ";"; }
        private static void Encode(MethodState state)
        {
            var labels = new Dictionary<string, int>(); var pc = 0; foreach (var line in state.Lines) { if (line.StartsWith(':')) labels[line[1..]] = pc; else { var op = OpCodes.Find(line.Split(' ', 2)[0]) ?? throw new FormatException($"Unknown instruction: {line}"); pc += Size(op.Info.Format); } }
            var raw = new List<ushort>(pc); pc = 0; foreach (var line in state.Lines) { if (line.StartsWith(':')) continue; var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries); var op = OpCodes.Find(parts[0])!.Value; var args = parts.Length == 1 ? [] : parts[1].Split(',', StringSplitOptions.TrimEntries); Emit(raw, op.Opcode, op.Info.Format, args, labels, pc); pc += Size(op.Info.Format); }
            state.Encoded.Code!.RawInsns = [.. raw];
        }
        private static int Size(InstructionFormat f) => f switch { InstructionFormat.Format10x or InstructionFormat.Format12x or InstructionFormat.Format11n or InstructionFormat.Format11x or InstructionFormat.Format10t => 1, InstructionFormat.Format21s or InstructionFormat.Format21c or InstructionFormat.Format22x or InstructionFormat.Format22t or InstructionFormat.Format22s or InstructionFormat.Format22c or InstructionFormat.Format23x => 2, InstructionFormat.Format31i or InstructionFormat.Format31c or InstructionFormat.Format31t or InstructionFormat.Format32x or InstructionFormat.Format35c or InstructionFormat.Format3rc or InstructionFormat.Format30t => 3, InstructionFormat.Format51l => 5, _ => 1 };
        private static void Emit(List<ushort> raw, byte op, InstructionFormat f, string[] a, Dictionary<string, int> labels, int pc)
        {
            static int R(string s) => int.Parse(s.Trim().TrimStart('v'), CultureInfo.InvariantCulture); static long N(string s) => ParseNumber(s); var aa = a.Length > 0 ? R(a[0]) : 0;
            var rel = a.Length == 0 ? 0 : (labels.TryGetValue(a[^1].TrimStart(':'), out var target) ? target - pc : (int)N(a[^1]));
            switch (f)
            {
                case InstructionFormat.Format10x: raw.Add((ushort)(op << 8)); break;
                case InstructionFormat.Format11x: raw.Add((ushort)((op << 8) | aa)); break;
                case InstructionFormat.Format12x: raw.Add((ushort)((op << 8) | (R(a[0]) & 15) | ((R(a[1]) & 15) << 4))); break;
                case InstructionFormat.Format11n: raw.Add((ushort)((op << 8) | (R(a[0]) & 15) | (((ushort)N(a[1]) & 15) << 4))); break;
                case InstructionFormat.Format22x: raw.Add((ushort)((op << 8) | R(a[0]))); raw.Add((ushort)R(a[1])); break;
                case InstructionFormat.Format32x: raw.Add((ushort)(op << 8)); raw.Add((ushort)R(a[0])); raw.Add((ushort)R(a[1])); break;
                case InstructionFormat.Format23x: raw.Add((ushort)((op << 8) | R(a[0]))); raw.Add((ushort)(R(a[1]) | (R(a[2]) << 8))); break;
                case InstructionFormat.Format22b: raw.Add((ushort)((op << 8) | R(a[0]))); raw.Add((ushort)(R(a[1]) | (((ushort)N(a[2]) & 0xff) << 8))); break;
                case InstructionFormat.Format22s: raw.Add((ushort)((op << 8) | (R(a[0]) & 15) | ((R(a[1]) & 15) << 4))); raw.Add((ushort)N(a[2])); break;
                case InstructionFormat.Format21s: raw.Add((ushort)((op << 8) | aa)); raw.Add((ushort)N(a[1])); break;
                case InstructionFormat.Format21h: raw.Add((ushort)((op << 8) | aa)); raw.Add((ushort)(N(a[1]) >> 16)); break;
                case InstructionFormat.Format31i: raw.Add((ushort)((op << 8) | aa)); raw.Add((ushort)N(a[1])); raw.Add((ushort)(N(a[1]) >> 16)); break;
                case InstructionFormat.Format10t: raw.Add((ushort)((op << 8) | ((ushort)rel & 0xff))); break;
                case InstructionFormat.Format20t or InstructionFormat.Format21t or InstructionFormat.Format22t: raw.Add((ushort)((op << 8) | (f == InstructionFormat.Format22t ? (R(a[0]) & 15) | ((R(a[1]) & 15) << 4) : aa))); raw.Add((ushort)rel); break;
                case InstructionFormat.Format30t: raw.Add((ushort)(op << 8)); raw.Add((ushort)rel); raw.Add((ushort)(rel >> 16)); break;
                default: throw new FormatException($"Unsupported instruction format {f}; edit this method with a supported form.");
            }
        }
    }
    private sealed class ClassState(ClassDefinition definition) { public ClassDefinition Definition { get; } = definition; public List<int> Interfaces { get; } = []; }
    private sealed class MethodState(ClassState cls, EncodedMethod encoded) { public ClassState Class { get; } = cls; public EncodedMethod Encoded { get; } = encoded; public CodeItem Code => Encoded.Code!; public List<string> Lines { get; } = []; }
}