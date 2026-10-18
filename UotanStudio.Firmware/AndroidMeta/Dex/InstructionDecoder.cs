using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex;

/// <summary>
/// Decodes raw Dalvik code units into high-level instructions, resolving constant-pool references through
/// the owning DEX file and emitting switch and array-data payloads as their own entries.
/// <para>将原始 Dalvik 代码单元解码为高层指令，经所属 DEX 文件解析常量池引用，
/// 并把 switch 与数组数据有效负载作为独立条目输出。</para>
/// </summary>
internal static class InstructionDecoder
{
    /// <summary>Decodes a raw instruction stream into an instruction list.
    /// <para>解码原始指令流为指令列表。</para></summary>
    /// <param name="raw">The 16-bit code units of one method body, in stream order.
    /// <para>单个方法体的 16 位代码单元，按流顺序排列。</para></param>
    /// <param name="dex">Source <see cref="DexFile"/> used to resolve string, type, field and method reference operands.
    /// <para>用于解析字符串、类型、字段与方法引用操作数的 <see cref="DexFile"/>。</para></param>
    /// <returns>Instructions in stream order with byte offsets and code-unit sizes. A payload ident at an instruction boundary is decoded as a payload entry; an opcode with no table entry becomes a one-unit <c>unknown-0xNN</c> entry; an instruction cut off by the end of the stream becomes a <c>truncated-</c> entry and ends decoding.
    /// <para>按流顺序返回的指令，含字节偏移与代码单元长度。指令边界上的有效负载标识解码为负载条目；无表项的操作码输出为 1 单元的 <c>unknown-0xNN</c> 条目；被流尾截断的指令输出为 <c>truncated-</c> 前缀条目并结束解码。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="raw"/> or <paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="raw"/> 或 <paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">A payload is truncated, misaligned, or has an invalid width, or a fixed register list exceeds five words.
    /// <para>负载被截断、未对齐或元素宽度无效，或者固定寄存器列表超过五个字。</para></exception>
    public static List<Instruction> Decode(ushort[] raw, DexFile dex)
        => Decode(raw, dex, descriptors: false);

    internal static List<Instruction> DecodeForSmali(ushort[] raw, DexFile dex)
        => Decode(raw, dex, descriptors: true);

    private static List<Instruction> Decode(ushort[] raw, DexFile dex, bool descriptors)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(dex);


        var instructions = new List<Instruction>(Math.Max(4, raw.Length / 2));
        var index = 0;
        while (index < raw.Length)
        {
            var unit = raw[index];
            var opcode = (byte)unit;


            if (unit is 0x0100 or 0x0200 or 0x0300)
            {
                var consumed = DecodePayload(raw, index, instructions, unit);
                index += consumed;
                continue;
            }

            var info = OpCodes.Get(opcode);
            if (info == null)
            {
                instructions.Add(new Instruction
                {
                    Offset = index * 2,
                    OpCode = opcode,
                    Mnemonic = $"unknown-0x{opcode:X2}",
                    Operands = [unit >> 8],
                    Size = 1
                });
                index++;
                continue;
            }

            var size = OpCodes.GetSize(opcode);
            if (index + size > raw.Length)
            {
                instructions.Add(new Instruction
                {
                    Offset = index * 2,
                    OpCode = opcode,
                    Mnemonic = $"truncated-{info.Mnemonic}",
                    Operands = [],
                    Size = raw.Length - index
                });
                break;
            }

            var operands = DecodeOperands(raw, index, info, dex, descriptors);
            instructions.Add(new Instruction
            {
                Offset = index * 2,
                OpCode = opcode,
                Mnemonic = info.Mnemonic,
                Operands = operands,
                Size = size
            });
            index += size;
        }
        return instructions;
    }

    private static int DecodePayload(ushort[] raw, int index, List<Instruction> code, ushort ident)
    {
        if ((index & 1) != 0 || raw.Length - index < (ident == 0x0200 ? 2 : 4))
        {
            throw new InvalidDataException("A DEX payload must be aligned to four bytes and contain its complete header.");
        }
        var required = ident switch
        {
            0x0100 => 4L + raw[index + 1] * 2L,
            0x0200 => 2L + raw[index + 1] * 4L,
            _ => 4L + (raw[index + 1] * (long)((uint)raw[index + 2] | (uint)raw[index + 3] << 16) + 1) / 2
        };
        if (required > raw.Length - index || ident == 0x0300 && raw[index + 1] is not (1 or 2 or 4 or 8))
        {
            throw new InvalidDataException("The DEX payload exceeds the instruction stream or has an invalid element width.");
        }
        switch (ident)
        {
            case 0x0100:
                {
                    var size = raw[index + 1];
                    var firstKey = (int)((uint)(raw[index + 2] | (raw[index + 3] << 16)));
                    if (size > 0 && (long)firstKey + size - 1 > int.MaxValue)
                    {
                        throw new InvalidDataException("The packed-switch key range exceeds a signed 32-bit integer.");
                    }
                    var targets = new int[size];
                    for (var i = 0; i < size; i++)
                    {
                        targets[i] = (int)((uint)(raw[index + 4 + i * 2] | (raw[index + 5 + i * 2] << 16)));
                    }
                    code.Add(new Instruction
                    {
                        Offset = index * 2,
                        OpCode = 0,
                        Mnemonic = "packed-switch-payload",
                        Operands = [new PayloadOperand("packed-switch", firstKey, targets)],
                        Size = 4 + size * 2
                    });
                    return 4 + size * 2;
                }
            case 0x0200:
                {
                    var size = raw[index + 1];
                    var keys = new int[size];
                    var targets = new int[size];
                    for (var i = 0; i < size; i++)
                    {
                        keys[i] = (int)((uint)(raw[index + 2 + i * 2] | (raw[index + 3 + i * 2] << 16)));
                        if (i > 0 && keys[i] <= keys[i - 1])
                        {
                            throw new InvalidDataException("Sparse-switch keys must be strictly increasing.");
                        }
                        targets[i] = (int)((uint)(raw[index + 2 + size * 2 + i * 2] | (raw[index + 3 + size * 2 + i * 2] << 16)));
                    }
                    code.Add(new Instruction
                    {
                        Offset = index * 2,
                        OpCode = 0,
                        Mnemonic = "sparse-switch-payload",
                        Operands = [new PayloadOperand("sparse-switch", 0, keys), new PayloadOperand("targets", 0, targets)],
                        Size = 2 + size * 4
                    });
                    return 2 + size * 4;
                }
            default:
                {
                    var width = raw[index + 1];
                    var dataUnits = checked((int)required - 4);
                    var payload = new int[dataUnits];
                    for (var i = 0; i < dataUnits; i++)
                    {
                        payload[i] = raw[index + 4 + i];
                    }
                    code.Add(new Instruction
                    {
                        Offset = index * 2,
                        OpCode = 0,
                        Mnemonic = "fill-array-data-payload",
                        Operands = [new PayloadOperand("fill-array-data", width, payload)
                        {
                            ElementCount = (uint)raw[index + 2] | (uint)raw[index + 3] << 16
                        }],
                        Size = 4 + dataUnits
                    });
                    return 4 + dataUnits;
                }
        }
    }

    private static IReadOnlyList<object?> DecodeOperands(ushort[] raw, int index, OpCodeInfo info, DexFile dex, bool descriptors)
    {
        var unit0 = raw[index];
        var aNibble = (unit0 >> 8) & 0x0f;
        var bNibble = (unit0 >> 12) & 0x0f;
        var aa = unit0 >> 8;

        switch (info.Format)
        {
            case InstructionFormat.Format10x:
                return [];
            case InstructionFormat.Format12x:
                return [aNibble, bNibble];
            case InstructionFormat.Format11n:
                {
                    var literal = SignExtend4((byte)bNibble);
                    return [aNibble, new LiteralOperand(literal)];
                }
            case InstructionFormat.Format11x:
                return [aa];
            case InstructionFormat.Format10t:
                {
                    var rel = (sbyte)aa;
                    return [new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format20t:
                {
                    var rel = (short)raw[index + 1];
                    return [new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format22x:
                return [aa, (int)raw[index + 1]];
            case InstructionFormat.Format21t:
                {
                    var rel = (short)raw[index + 1];
                    return [aa, new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format21s:
                return [aa, new LiteralOperand((short)raw[index + 1])];
            case InstructionFormat.Format21h:
                {
                    var value = (byte)unit0 == 0x19
                        ? (long)(short)raw[index + 1] << 48
                        : (long)(short)raw[index + 1] << 16;
                    return [aa, new LiteralOperand(value)];
                }
            case InstructionFormat.Format21c:
                return [aa, ResolveRef(dex, raw[index + 1], info.RefKind, descriptors)];
            case InstructionFormat.Format23x:
                return [aa, raw[index + 1] & 0xff, raw[index + 1] >> 8];
            case InstructionFormat.Format22b:
                return [aa, raw[index + 1] & 0xff, new LiteralOperand((sbyte)(raw[index + 1] >> 8))];
            case InstructionFormat.Format22t:
                {
                    var rel = (short)raw[index + 1];
                    return [aNibble, bNibble, new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format22s:
                return [aNibble, bNibble, new LiteralOperand((short)raw[index + 1])];
            case InstructionFormat.Format22c:
                return [aNibble, bNibble, ResolveRef(dex, raw[index + 1], info.RefKind, descriptors)];
            case InstructionFormat.Format30t:
                {
                    var rel = (int)((uint)(raw[index + 1] | (raw[index + 2] << 16)));
                    return [new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format32x:
                return [(int)raw[index + 1], (int)raw[index + 2]];
            case InstructionFormat.Format31i:
                {
                    var value = (int)((uint)(raw[index + 1] | (raw[index + 2] << 16)));
                    return [aa, new LiteralOperand(value)];
                }
            case InstructionFormat.Format31t:
                {
                    var rel = (int)((uint)(raw[index + 1] | (raw[index + 2] << 16)));
                    return [aa, new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format31c:
                {
                    var refIdx = raw[index + 1] | (raw[index + 2] << 16);
                    return [aa, ResolveRef(dex, refIdx, info.RefKind, descriptors)];
                }
            case InstructionFormat.Format35c:
                {
                    var count = (byte)((unit0 >> 12) & 0x0f);
                    var g = (byte)((unit0 >> 8) & 0x0f);


                    var c = (byte)(raw[index + 2] & 0x0f);
                    var d = (byte)((raw[index + 2] >> 4) & 0x0f);
                    var e = (byte)((raw[index + 2] >> 8) & 0x0f);
                    var f = (byte)((raw[index + 2] >> 12) & 0x0f);


                    var regs = RegisterList(count, c, d, e, f, g);
                    var refIdx = raw[index + 1];
                    return [regs, ResolveRef(dex, refIdx, info.RefKind, descriptors)];
                }
            case InstructionFormat.Format3rc:
                {
                    var count = aa;
                    var first = raw[index + 2];
                    var regs = new int[count];
                    for (var i = 0; i < count; i++) regs[i] = first + i;
                    var refIdx = raw[index + 1];
                    return [regs, ResolveRef(dex, refIdx, info.RefKind, descriptors)];
                }
            case InstructionFormat.Format45cc:
                {
                    var count = (byte)((unit0 >> 12) & 0x0f);
                    var g = (byte)((unit0 >> 8) & 0x0f);


                    var c = (byte)(raw[index + 2] & 0x0f);
                    var d = (byte)((raw[index + 2] >> 4) & 0x0f);
                    var e = (byte)((raw[index + 2] >> 8) & 0x0f);
                    var f = (byte)((raw[index + 2] >> 12) & 0x0f);
                    var regs = RegisterList(count, c, d, e, f, g);
                    var methodRef = raw[index + 1];
                    var protoRef = raw[index + 3];
                    return [regs, ResolveRef(dex, methodRef, info.RefKind, descriptors), ResolveProtoRef(dex, protoRef)];
                }
            case InstructionFormat.Format4rcc:
                {
                    var count = aa;
                    var first = raw[index + 2];
                    var regs = new int[count];
                    for (var i = 0; i < count; i++) regs[i] = first + i;
                    var methodRef = raw[index + 1];
                    var protoRef = raw[index + 3];
                    return [regs, ResolveRef(dex, methodRef, info.RefKind, descriptors), ResolveProtoRef(dex, protoRef)];
                }
            case InstructionFormat.Format51l:
                {
                    var value = (long)(((ulong)raw[index + 4] << 48) | ((ulong)raw[index + 3] << 32) | ((ulong)raw[index + 2] << 16) | raw[index + 1]);
                    return [aa, new LiteralOperand(value)];
                }
            default:
                return [];
        }
    }

    private static int SignExtend4(byte nibble) => nibble >= 8 ? nibble - 16 : nibble;

    private static int[] RegisterList(int count, int c, int d, int e, int f, int g)
    {
        if (count > 5)
        {
            throw new InvalidDataException("A fixed DEX register list cannot contain more than five words.");
        }
        ReadOnlySpan<int> registers = [c, d, e, f, g];
        return registers[..count].ToArray();
    }

    private static object? ResolveRef(DexFile dex, int index, RefKind kind, bool descriptors)
    {
        var cache = dex.InstructionOperands;
        switch (kind)
        {
            case RefKind.String:
                return dex.GetString(index);
            case RefKind.Type:
                return cache.ResolveType(dex, index);
            case RefKind.Proto:
                return (object?)cache.ResolveProto(dex, index) ?? index;
            case RefKind.MethodHandle:
                return new MethodHandleRefOperand(index);
            case RefKind.CallSite:
                return new CallSiteRefOperand(index);
            case RefKind.Field:
                return (object?)cache.ResolveField(dex, index, descriptors) ?? index;
            case RefKind.Method:
                return (object?)cache.ResolveMethod(dex, index, descriptors) ?? index;
            default:
                return index;
        }
    }

    private static object? ResolveProtoRef(DexFile dex, int index)
        => (object?)dex.InstructionOperands.ResolveProto(dex, index) ?? index;
}

/// <summary>
/// Per-model memo of resolved reference operands, so decoding a large image formats each type, field, method
/// and prototype name once instead of once per referencing instruction. Entries are rebuilt whenever the
/// string, type, prototype, field or method table count changes; the cached operands are immutable records.
/// <para>每个模型一份已解析引用操作数的记忆缓存，使解码大型镜像时每个类型、字段、方法与原型名称只格式化一次，
/// 而不是每条引用指令各格式化一次。字符串、类型、原型、字段或方法表的数量变化时缓存重建；
/// 缓存内容是记录类型，本身不可变。</para>
/// </summary>
internal sealed class InstructionOperandCache
{
    private (int Strings, int Types, int Protos, int Fields, int Methods) _stamp;
    private TypeRefOperand?[] _types = [];
    private ProtoRefOperand?[] _protos = [];
    private FieldRefOperand?[] _fieldDescriptors = [];
    private FieldRefOperand?[] _fieldNames = [];
    private MethodRefOperand?[] _methodDescriptors = [];
    private MethodRefOperand?[] _methodNames = [];

    /// <summary>
    /// Resolves a <c>type_ids</c> reference, keeping the tolerant <c>"?"</c> descriptor of the index lookup.
    /// <para>解析 <c>type_ids</c> 引用，保留索引查询原有的宽松 <c>"?"</c> 描述符行为。</para>
    /// </summary>
    public TypeRefOperand ResolveType(DexFile dex, int index)
    {
        if ((uint)index < (uint)dex.Types.Count)
        {
            Sync(dex);
            return _types[index] ??= new TypeRefOperand(dex.GetType(index));
        }
        return new TypeRefOperand(dex.GetType(index));
    }

    /// <summary>
    /// Resolves a <c>field_ids</c> reference, or <see langword="null"/> when the index is out of range.
    /// <para>解析 <c>field_ids</c> 引用；索引越界时返回 <see langword="null"/>。</para>
    /// </summary>
    public FieldRefOperand? ResolveField(DexFile dex, int index, bool descriptors)
    {
        if ((uint)index >= (uint)dex.Fields.Count) return null;
        Sync(dex);
        var cache = descriptors ? _fieldDescriptors : _fieldNames;
        if (cache[index] is { } cached) return cached;
        var field = dex.Fields[index];
        var owner = descriptors ? dex.GetType(field.ClassIndex) : dex.FormatType(field.ClassIndex);
        var type = descriptors ? dex.GetType(field.TypeIndex) : dex.FormatType(field.TypeIndex);
        return cache[index] = new FieldRefOperand(owner, dex.GetString(field.NameIndex), type);
    }

    /// <summary>
    /// Resolves a <c>method_ids</c> reference, or <see langword="null"/> when the index is out of range.
    /// <para>解析 <c>method_ids</c> 引用；索引越界时返回 <see langword="null"/>。</para>
    /// </summary>
    public MethodRefOperand? ResolveMethod(DexFile dex, int index, bool descriptors)
    {
        if ((uint)index >= (uint)dex.Methods.Count) return null;
        Sync(dex);
        var cache = descriptors ? _methodDescriptors : _methodNames;
        if (cache[index] is { } cached) return cached;
        var method = dex.Methods[index];
        var proto = dex.GetProto(method.ProtoIndex);
        string[] parameters;
        if (proto == null)
        {
            parameters = [];
        }
        else
        {
            var indices = proto.ParameterTypeIndices;
            parameters = new string[indices.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                parameters[i] = descriptors ? dex.GetType(indices[i]) : dex.FormatType(indices[i]);
            }
        }
        var owner = descriptors ? dex.GetType(method.ClassIndex) : dex.FormatType(method.ClassIndex);
        var returnType = proto == null
            ? "?"
            : descriptors ? dex.GetType(proto.ReturnTypeIndex) : dex.FormatType(proto.ReturnTypeIndex);
        return cache[index] = new MethodRefOperand(owner, dex.GetString(method.NameIndex), returnType, parameters);
    }

    /// <summary>
    /// Resolves a <c>proto_ids</c> reference, or <see langword="null"/> when the index is out of range. The
    /// operand always carries raw descriptors, matching the disassembly text of prototype references.
    /// <para>解析 <c>proto_ids</c> 引用；索引越界时返回 <see langword="null"/>。操作数始终携带原始描述符，
    /// 与原型引用的反汇编文本一致。</para>
    /// </summary>
    public ProtoRefOperand? ResolveProto(DexFile dex, int index)
    {
        if ((uint)index >= (uint)dex.Protos.Count) return null;
        Sync(dex);
        if (_protos[index] is { } cached) return cached;
        var proto = dex.Protos[index];
        return _protos[index] = new ProtoRefOperand(dex.GetType(proto.ReturnTypeIndex), [.. proto.ParameterTypeIndices.Select(dex.GetType)]);
    }

    private void Sync(DexFile dex)
    {
        var stamp = (dex.Strings.Count, dex.Types.Count, dex.Protos.Count, dex.Fields.Count, dex.Methods.Count);
        if (stamp == _stamp) return;
        _stamp = stamp;
        _types = new TypeRefOperand?[dex.Types.Count];
        _protos = new ProtoRefOperand?[dex.Protos.Count];
        _fieldDescriptors = new FieldRefOperand?[dex.Fields.Count];
        _fieldNames = new FieldRefOperand?[dex.Fields.Count];
        _methodDescriptors = new MethodRefOperand?[dex.Methods.Count];
        _methodNames = new MethodRefOperand?[dex.Methods.Count];
    }
}