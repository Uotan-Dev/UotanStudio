using System.Globalization;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

/// <summary>
/// Assembles supported smali declarations and instructions into a DEX model, rejecting unsupported syntax.
/// <para>将受支持的 smali 声明与指令汇编为 DEX 模型，拒绝不支持的语法。</para>
/// </summary>
public static partial class SmaliAssembler
{
    /// <remarks>
    /// The project-specific .packed-switch-raw and .sparse-switch-raw blocks retain signed decimal code-unit offsets
    /// in unreferenced tables; these directives are an extension of standard Smali syntax.
    /// <para>项目专用的 .packed-switch-raw 和 .sparse-switch-raw 块保留无引用表的有符号十进制代码单元偏移，属于标准 Smali 语法的扩展。</para>
    /// </remarks>
    /// <summary>
    /// Parses text emitted by <see cref="Disassembler"/>.
    /// <para>解析由 <see cref="Disassembler"/> 输出的文本。</para>
    /// </summary>
    /// <param name="smali">
    /// Whole-file smali text; unsupported syntax fails assembly rather than being discarded.
    /// <para>整文件 smali 文本；不支持的语法会导致汇编失败，不会被丢弃。</para>
    /// </param>
    /// <returns>
    /// A <see cref="DexFile"/> model with canonically ordered identifier tables and relocated references.
    /// <para>返回标识符表已按规范排序且引用已重定位的 <see cref="DexFile"/> 模型。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="smali"/> is <see langword="null"/>. <para><paramref name="smali"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="FormatException">
    /// The text declares no <c>.class</c>, contains malformed or unsupported syntax, or ends inside a method.
    /// <para>文本未声明 <c>.class</c>，包含非法或不支持的语法，或在方法内部结束。</para>
    /// </exception>
    public static DexFile Assemble(string smali)
    {
        ArgumentNullException.ThrowIfNull(smali);
        var context = new Context();
        ClassState? cls = null;
        MethodState? method = null;
        var lines = smali.Replace("\r\n", "\n").Split('\n')
            .Select(raw => SmaliSyntax.StripComment(raw).Trim()).Where(line => line.Length > 0).ToArray();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line == ".end method")
            {
                if (method is null)
                {
                    throw new FormatException("An .end method directive has no matching declaration.");
                }
                context.FinishMethod(method);
                method = null;
                continue;
            }
            if (method is not null)
            {
                if (method.OpenPayload is null && line.StartsWith(".annotation ", StringComparison.Ordinal))
                {
                    context.AddMethodAnnotation(method, context.ReadAnnotation(lines, ref lineIndex, method.Class));
                }
                else if (method.OpenPayload is null && line.StartsWith(".param ", StringComparison.Ordinal))
                {
                    context.ReadParameter(method, lines, ref lineIndex);
                }
                else
                {
                    if (line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0]
                        is "invoke-custom" or "invoke-custom/range")
                    {
                        line = ReadCallSiteInstruction(lines, ref lineIndex);
                    }
                    ParseMethodLine(method, line);
                }
                continue;
            }
            if (line.StartsWith(".class ", StringComparison.Ordinal))
            {
                cls = context.StartClass(line);
                continue;
            }
            if (cls is null)
            {
                throw new FormatException("A class must be declared before its members.");
            }
            if (line.StartsWith(".super ", StringComparison.Ordinal))
            {
                cls.Definition.SuperclassIndex = context.Type(Token(line, ".super"));
                continue;
            }
            if (line.StartsWith(".implements ", StringComparison.Ordinal))
            {
                cls.Interfaces.Add(context.Type(Token(line, ".implements")));
                continue;
            }
            if (line.StartsWith(".source ", StringComparison.Ordinal))
            {
                cls.Definition.SourceFileIndex = context.String(SmaliSyntax.StringLiteral(Token(line, ".source")));
                continue;
            }
            if (line.StartsWith(".field ", StringComparison.Ordinal))
            {
                var field = context.ParseField(cls, ReadFieldDeclaration(lines, ref lineIndex));
                context.ReadFieldAnnotations(cls, field, lines, ref lineIndex);
                continue;
            }
            if (line.StartsWith(".annotation ", StringComparison.Ordinal))
            {
                context.AddClassAnnotation(cls, context.ReadAnnotation(lines, ref lineIndex, cls));
                continue;
            }
            if (line.StartsWith(".method ", StringComparison.Ordinal))
            {
                method = context.StartMethod(cls, line);
                continue;
            }
            throw new FormatException($"Unsupported class directive: {line}");
        }
        if (method is not null)
        {
            throw new FormatException("A method declaration is missing .end method.");
        }
        if (context.Classes.Count == 0)
        {
            throw new FormatException("No .class definition in smali text.");
        }
        return context.Build();
    }

    private static void ParseMethodLine(MethodState method, string line)
    {
        if (ParsePayloadLine(method, line))
        {
            return;
        }
        if (line.StartsWith(".catch ", StringComparison.Ordinal) || line.StartsWith(".catchall ", StringComparison.Ordinal))
        {
            method.Catches.Add(line);
            return;
        }
        if (line.StartsWith(".registers ", StringComparison.Ordinal))
        {
            SetRegisters(method, ParseNumber(Token(line, ".registers")));
            return;
        }
        if (line.StartsWith(".locals ", StringComparison.Ordinal))
        {
            SetRegisters(method, checked(ParseNumber(Token(line, ".locals")) + method.Code.InsSize));
            return;
        }
        if (line.StartsWith('.') && !IsDebugDirective(line))
        {
            throw new FormatException($"Unsupported method directive: {line}");
        }
        method.Lines.Add(line);
    }

    private static void SetRegisters(MethodState method, long count)
    {
        if (count < method.Code.InsSize || count > ushort.MaxValue)
        {
            throw new FormatException("Register count must include all incoming parameter words and fit in 16 bits.");
        }
        if (method.RegisterCountSpecified && method.Code.RegistersSize != count)
        {
            throw new FormatException("Conflicting .registers and .locals declarations.");
        }
        method.Code.RegistersSize = (ushort)count;
        method.RegisterCountSpecified = true;
    }

    private static string Token(string line, string directive) => line[directive.Length..].Trim();
    private static long ParseNumber(string text) => SmaliLiterals.Integral(text);
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
            "declared-synchronized" => AccessFlags.DeclaredSynchronized,
            _ => throw new FormatException($"Unknown access modifier '{value}'.")
        };
        return result;
    }

    private sealed partial class Context
    {
        public List<ClassState> Classes { get; } = [];
        public List<string> Strings { get; } = [];
        public List<string> Types { get; } = [];
        public List<ProtoDefinition> Protos { get; } = [];
        public List<FieldId> Fields { get; } = [];
        public List<MethodId> Methods { get; } = [];
        public List<MethodHandleDefinition> MethodHandles { get; } = [];
        private List<MethodState> MethodStates { get; } = [];
        private Dictionary<string, int> StringIndices { get; } = new(StringComparer.Ordinal);
        private Dictionary<string, int> TypeIndices { get; } = new(StringComparer.Ordinal);
        private Dictionary<(int ClassIndex, int NameIndex, int TypeIndex), int> FieldIndices { get; } = [];
        private Dictionary<(int ClassIndex, int NameIndex, int ProtoIndex), int> MethodIndices { get; } = [];
        private Dictionary<ProtoKey, int> ProtoIndices { get; } = [];
        private Dictionary<(int Kind, int MemberIndex), int> MethodHandleIndices { get; } = [];
        private List<ClassDefinition>? OrderedClassResult { get; set; }
        public int String(string value)
        {
            if (!StringIndices.TryGetValue(value, out var index))
            {
                StringIndices.Add(value, index = Strings.Count);
                Strings.Add(value);
            }
            return index;
        }
        public int Type(string value)
        {
            var descriptor = Descriptor(value);
            if (!TypeIndices.TryGetValue(descriptor, out var index))
            {
                TypeIndices.Add(descriptor, index = Types.Count);
                Types.Add(descriptor);
                String(descriptor);
            }
            return index;
        }
        public int Field(FieldId value)
        {
            var key = (value.ClassIndex, value.NameIndex, value.TypeIndex);
            if (!FieldIndices.TryGetValue(key, out var index))
            {
                FieldIndices.Add(key, index = Fields.Count);
                Fields.Add(value);
            }
            return index;
        }
        public int Method(MethodId value)
        {
            var key = (value.ClassIndex, value.NameIndex, value.ProtoIndex);
            if (!MethodIndices.TryGetValue(key, out var index))
            {
                MethodIndices.Add(key, index = Methods.Count);
                Methods.Add(value);
            }
            return index;
        }
        public ClassState StartClass(string line) { var p = line[6..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries); var state = new ClassState(new ClassDefinition { TypeIndex = Type(p[^1]), AccessFlags = Access(p[..^1]) }); Classes.Add(state); return state; }
        public EncodedField ParseField(ClassState cls, string line)
        {
            var text = line[6..].Trim();
            var eq = text.IndexOf('=');
            var initializer = eq >= 0 ? text[(eq + 1)..].Trim() : null;
            if (eq >= 0)
            {
                text = text[..eq].Trim();
            }
            var p = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0)
            {
                throw new FormatException($"Invalid field: {line}");
            }
            var spec = p[^1];
            var colon = spec.IndexOf(':');
            if (colon <= 0)
            {
                throw new FormatException($"Invalid field: {line}");
            }
            var descriptor = SmaliTypes.Single(spec[(colon + 1)..], allowVoid: false);
            var id = new FieldId
            {
                ClassIndex = cls.Definition.TypeIndex,
                NameIndex = String(spec[..colon]),
                TypeIndex = Type(descriptor)
            };
            var field = new EncodedField { FieldIndex = Field(id), AccessFlags = Access(p[..^1]) };
            if ((field.AccessFlags & AccessFlags.Static) != 0)
            {
                cls.Definition.StaticFields.Add(field);
                cls.ExplicitStaticValues.Add(initializer is not null);
                (cls.Definition.StaticValues ??= []).Add(FieldValue(descriptor, initializer, Types[cls.Definition.TypeIndex]));
            }
            else
            {
                if (initializer is not null)
                {
                    throw new FormatException("An instance field cannot have an encoded static initializer.");
                }
                cls.Definition.InstanceFields.Add(field);
            }
            return field;
        }
        public MethodState StartMethod(ClassState cls, string line)
        {
            var text = line[7..].Trim();
            var open = text.IndexOf('(');
            var close = text.LastIndexOf(')');
            if (open < 1 || close < open || close == text.Length - 1)
            {
                throw new FormatException($"Invalid method: {line}");
            }
            var declaration = text[..open].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var protoIndex = Prototype(text[open..]);
            var args = Protos[protoIndex].ParameterTypeIndices;
            var flags = Access(declaration[..^1]);
            var incoming = (flags & AccessFlags.Static) == 0 ? 1 : 0;
            foreach (var argument in args)
            {
                incoming = checked(incoming + (Types[argument] is "J" or "D" ? 2 : 1));
            }
            if (incoming > ushort.MaxValue)
            {
                throw new FormatException("Method parameters exceed the incoming register limit.");
            }
            var id = new MethodId { ClassIndex = cls.Definition.TypeIndex, NameIndex = String(declaration[^1]), ProtoIndex = protoIndex };
            return new MethodState(cls, new EncodedMethod
            {
                MethodIndex = Method(id),
                AccessFlags = flags,
                Code = new CodeItem { InsSize = (ushort)incoming, RegistersSize = (ushort)incoming }
            });
        }
        public void FinishMethod(MethodState state)
        {
            if (state.OpenPayload is not null)
            {
                throw new FormatException("A payload declaration is missing its .end directive.");
            }
            MethodStates.Add(state);
            ResolveParameters(state);
            if ((state.Encoded.AccessFlags & (AccessFlags.Abstract | AccessFlags.Native)) != 0)
            {
                if (state.Lines.Count != 0 || state.RegisterCountSpecified || state.Catches.Count != 0)
                {
                    throw new FormatException("Abstract and native methods cannot declare bytecode or registers.");
                }
                state.Encoded.Code = null;
            }
            else if (state.Lines.Count > 0)
            {
                Encode(state);
            }
            var target = (state.Encoded.AccessFlags & (AccessFlags.Private | AccessFlags.Static | AccessFlags.Constructor)) != 0
                ? state.Class.Definition.DirectMethods : state.Class.Definition.VirtualMethods;
            target.Add(state.Encoded);
        }
        public DexFile Build()
        {
            Canonicalize();
            var dex = new DexFile(MinimumVersion);
            dex.Strings.AddRange(Strings);
            dex.Types.AddRange(Types);
            dex.Protos.AddRange(Protos);
            dex.Fields.AddRange(Fields);
            dex.Methods.AddRange(Methods);
            dex.MethodHandles.AddRange(MethodHandles);
            dex.CallSites.AddRange(CallSites.Select(site => site.Values));
            dex.Classes.AddRange(OrderedClasses());
            return dex;
        }
        private static string Descriptor(string value) { value = value.Trim(); if (value is "void" or "V") return "V"; if (value is "boolean" or "Z") return "Z"; if (value is "byte" or "B") return "B"; if (value is "short" or "S") return "S"; if (value is "char" or "C") return "C"; if (value is "int" or "I") return "I"; if (value is "long" or "J") return "J"; if (value is "float" or "F") return "F"; if (value is "double" or "D") return "D"; if (value.StartsWith('[')) return "[" + Descriptor(value[1..]); if (value.StartsWith('L') && value.EndsWith(';')) return value; if (value.EndsWith("[]", StringComparison.Ordinal)) return "[" + Descriptor(value[..^2]); return "L" + value.Replace('.', '/') + ";"; }
        private void Encode(MethodState state)
        {
            var layout = LayoutMethod(state, text => String(SmaliSyntax.StringLiteral(text)));
            var bindings = BindPayloads(layout);
            var raw = new List<ushort>(layout.Size);
            foreach (var entry in layout.Entries)
            {
                if (entry.Payload is { } payload)
                {
                    EmitPayload(raw, payload, bindings.Owners.TryGetValue(entry.Address, out var owner) ? owner : null, layout);
                    continue;
                }
                var opcode = entry.Opcode;
                var info = entry.Info ?? throw new FormatException($"Unknown instruction: {entry.Line}");
                var args = entry.Args;
                if (bindings.Targets.TryGetValue(entry.Address, out var payloadTarget))
                {
                    args = [.. entry.Args];
                    args[^1] = (payloadTarget - entry.Address).ToString(CultureInfo.InvariantCulture);
                }
                if (opcode >= 0xfe)
                {
                    MinimumVersion = "039";
                }
                else if (opcode >= 0xfa && MinimumVersion == "035")
                {
                    MinimumVersion = "038";
                }
                Emit(raw, opcode, info, args, layout.Labels, entry.Address, state.Code, Types[state.Class.Definition.TypeIndex]);
            }
            state.Encoded.Code!.RawInsns = [.. raw];
            EncodeCatches(state, layout.Labels, layout.Boundaries, layout.Instructions);
            EncodeDebugInfo(state, layout);
        }
        private void Emit(List<ushort> raw, byte op, OpCodeInfo info, string[] a, Dictionary<string, int> labels, int pc, CodeItem code, string owner)
        {
            var f = info.Format;
            var expected = f switch
            {
                InstructionFormat.Format10x => 0,
                InstructionFormat.Format11x or InstructionFormat.Format10t or InstructionFormat.Format20t or InstructionFormat.Format30t => 1,
                InstructionFormat.Format12x or InstructionFormat.Format11n or InstructionFormat.Format22x
                    or InstructionFormat.Format21t or InstructionFormat.Format21s or InstructionFormat.Format21h
                    or InstructionFormat.Format32x or InstructionFormat.Format31i or InstructionFormat.Format51l
                    or InstructionFormat.Format21c or InstructionFormat.Format31c or InstructionFormat.Format31t
                    or InstructionFormat.Format35c or InstructionFormat.Format3rc => 2,
                InstructionFormat.Format23x or InstructionFormat.Format22b or InstructionFormat.Format22t or InstructionFormat.Format22s
                    or InstructionFormat.Format22c or InstructionFormat.Format45cc or InstructionFormat.Format4rcc => 3,
                _ => throw new FormatException($"Unsupported instruction format {f}.")
            };
            if (a.Length != expected)
            {
                throw new FormatException($"Instruction requires {expected} operands, received {a.Length}.");
            }
            int R(int index, int bits) => Register(a[index], bits);
            int Register(string text, int bits)
            {
                if (text.Length < 2 || text[0] is not ('v' or 'p')
                    || !int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    throw new FormatException($"Invalid register '{text}'.");
                }
                if (text[0] == 'p')
                {
                    if (number >= code.InsSize)
                    {
                        throw new FormatException($"Parameter register '{text}' is outside the method signature.");
                    }
                    number = checked(number + code.RegistersSize - code.InsSize);
                }
                if (number >= code.RegistersSize || number >= 1 << bits)
                {
                    throw new FormatException($"Register '{text}' exceeds the method or instruction register limit.");
                }
                return number;
            }
            int Reference(int index, RefKind kind, bool jumbo = false)
            {
                var reference = ResolveReference(a[index], kind, owner);
                if (!jumbo && reference > ushort.MaxValue)
                {
                    throw new FormatException("A constant-pool reference exceeds the instruction's 16-bit index.");
                }
                return reference;
            }
            long N(int index, int bits)
            {
                var value = SmaliLiterals.Fixed(a[index], Math.Max(16, bits));
                return Signed(value, bits);
            }
            long Branch(int bits)
            {
                var operand = a[^1];
                if (operand.StartsWith(':'))
                {
                    if (!labels.TryGetValue(operand[1..], out var target))
                    {
                        throw new FormatException($"Undefined label '{operand}'.");
                    }
                    return Signed((long)target - pc, bits);
                }
                return Signed(ParseNumber(operand), bits);
            }
            static long Signed(long value, int bits)
            {
                if (bits < 64 && (value < -(1L << (bits - 1)) || value >= 1L << (bits - 1)))
                {
                    throw new FormatException($"Value {value} does not fit a signed {bits}-bit operand.");
                }
                return value;
            }
            void First(int operands) => raw.Add((ushort)(op | operands << 8));
            void Wide(long value, int words)
            {
                for (var i = 0; i < words; i++)
                {
                    raw.Add(unchecked((ushort)(value >> (i * 16))));
                }
            }
            switch (f)
            {
                case InstructionFormat.Format10x:
                    First(0);
                    break;
                case InstructionFormat.Format11x:
                    First(R(0, 8));
                    break;
                case InstructionFormat.Format12x:
                    First(R(0, 4) | R(1, 4) << 4);
                    break;
                case InstructionFormat.Format11n:
                    First(R(0, 4) | (int)(N(1, 4) & 15) << 4);
                    break;
                case InstructionFormat.Format22x:
                    First(R(0, 8));
                    Wide(R(1, 16), 1);
                    break;
                case InstructionFormat.Format32x:
                    First(0);
                    Wide(R(0, 16), 1);
                    Wide(R(1, 16), 1);
                    break;
                case InstructionFormat.Format23x:
                    First(R(0, 8));
                    Wide(R(1, 8) | R(2, 8) << 8, 1);
                    break;
                case InstructionFormat.Format22b:
                    First(R(0, 8));
                    Wide((uint)R(1, 8) | (N(2, 8) & 255) << 8, 1);
                    break;
                case InstructionFormat.Format22s:
                    First(R(0, 4) | R(1, 4) << 4);
                    Wide(N(2, 16), 1);
                    break;
                case InstructionFormat.Format21s:
                    First(R(0, 8));
                    Wide(N(1, 16), 1);
                    break;
                case InstructionFormat.Format21h:
                    var shift = op == 0x19 ? 48 : 16;
                    var value = N(1, op == 0x19 ? 64 : 32);
                    if ((value & ((1L << shift) - 1)) != 0)
                    {
                        throw new FormatException("A high16 literal must have zero low bits.");
                    }
                    First(R(0, 8));
                    Wide(value >> shift, 1);
                    break;
                case InstructionFormat.Format31i:
                    First(R(0, 8));
                    Wide(N(1, 32), 2);
                    break;
                case InstructionFormat.Format51l:
                    First(R(0, 8));
                    Wide(N(1, 64), 4);
                    break;
                case InstructionFormat.Format21c:
                    First(R(0, 8));
                    Wide(Reference(1, info.RefKind), 1);
                    break;
                case InstructionFormat.Format22c:
                    First(R(0, 4) | R(1, 4) << 4);
                    Wide(Reference(2, info.RefKind), 1);
                    break;
                case InstructionFormat.Format31c:
                    First(R(0, 8));
                    Wide(Reference(1, info.RefKind, jumbo: true), 2);
                    break;
                case InstructionFormat.Format35c:
                case InstructionFormat.Format45cc:
                case InstructionFormat.Format3rc:
                case InstructionFormat.Format4rcc:
                    var range = f is InstructionFormat.Format3rc or InstructionFormat.Format4rcc;
                    var registers = RegisterList(a[0], range, Register);
                    if (info.Mnemonic.StartsWith("invoke-", StringComparison.Ordinal))
                    {
                        code.OutsSize = Math.Max(code.OutsSize, (ushort)registers.Length);
                    }
                    if (range)
                    {
                        First(registers.Length);
                        Wide(Reference(1, info.RefKind), 1);
                        Wide(registers.Length == 0 ? 0 : registers[0], 1);
                    }
                    else
                    {
                        First(registers.Length << 4 | (registers.Length == 5 ? registers[4] : 0));
                        Wide(Reference(1, info.RefKind), 1);
                        var packed = 0;
                        for (var i = 0; i < Math.Min(registers.Length, 4); i++)
                        {
                            packed |= registers[i] << (i * 4);
                        }
                        Wide(packed, 1);
                    }
                    if (f is InstructionFormat.Format45cc or InstructionFormat.Format4rcc)
                    {
                        Wide(Reference(2, RefKind.Proto), 1);
                    }
                    break;
                case InstructionFormat.Format10t:
                    First((int)(Branch(8) & 255));
                    break;
                case InstructionFormat.Format20t:
                    First(0);
                    Wide(Branch(16), 1);
                    break;
                case InstructionFormat.Format21t:
                    First(R(0, 8));
                    Wide(Branch(16), 1);
                    break;
                case InstructionFormat.Format22t:
                    First(R(0, 4) | R(1, 4) << 4);
                    Wide(Branch(16), 1);
                    break;
                case InstructionFormat.Format30t:
                    First(0);
                    Wide(Branch(32), 2);
                    break;
                case InstructionFormat.Format31t:
                    First(R(0, 8));
                    Wide(Branch(32), 2);
                    break;
            }
        }
    }
    private readonly struct ProtoKey(int returnTypeIndex, int[] parameterTypeIndices) : IEquatable<ProtoKey>
    {
        private int ReturnTypeIndex { get; } = returnTypeIndex;
        private int[] ParameterTypeIndices { get; } = parameterTypeIndices;
        public bool Equals(ProtoKey other) => ReturnTypeIndex == other.ReturnTypeIndex
            && ParameterTypeIndices.AsSpan().SequenceEqual(other.ParameterTypeIndices);
        public override bool Equals(object? obj) => obj is ProtoKey other && Equals(other);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(ReturnTypeIndex);
            foreach (var index in ParameterTypeIndices)
            {
                hash.Add(index);
            }
            return hash.ToHashCode();
        }
    }
    private sealed class ClassState(ClassDefinition definition)
    {
        public ClassDefinition Definition { get; } = definition;
        public List<int> Interfaces { get; } = [];
        public List<bool> ExplicitStaticValues { get; } = [];
    }
    private sealed class MethodState(ClassState cls, EncodedMethod encoded)
    {
        public ClassState Class { get; } = cls;
        public EncodedMethod Encoded { get; } = encoded;
        public CodeItem Code => Encoded.Code!;
        public List<string> Lines { get; } = [];
        public List<string> Catches { get; } = [];
        public List<ParameterDeclaration> Parameters { get; } = [];
        public Dictionary<int, string> ParameterNames { get; } = [];
        public Dictionary<int, PayloadBlock> Payloads { get; } = [];
        public PayloadBlock? OpenPayload { get; set; }
        public bool RegisterCountSpecified { get; set; }
    }
}