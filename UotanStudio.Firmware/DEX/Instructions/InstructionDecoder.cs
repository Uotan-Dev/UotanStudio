using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex.Instructions;

/// <summary>
/// <para>指令流解码器：把原始 16 位代码单元解码为指令列表（含 switch/fill-array-data 伪指令 payload）。</para>
/// Instruction stream decoder: decodes raw 16-bit code units into an instruction list (including switch /
/// fill-array-data pseudo-instruction payloads).
/// </summary>
public static class InstructionDecoder
{
    /// <summary>
    /// <para>解码原始指令流为指令列表。</para>
    /// Decodes a raw instruction stream into an instruction list.
    /// </summary>
    /// <param name="raw">原始 16 位代码单元。<para>Raw 16-bit code units.</para></param>
    /// <param name="dex">所属 DEX 文件（用于解析字符串/类型/字段/方法引用）。<para>The owning DEX file (for resolving refs).</para></param>
    /// <returns>解码后的指令列表。<para>The decoded instruction list.</para></returns>
    public static List<Instruction> Decode(ushort[] raw, DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(dex);

        // 第一遍：收集 31t 指令（packed-switch 0x2b / sparse-switch 0x2c / fill-array-data 0x26）
        // 引用的 payload 目标偏移（代码单元索引）。0x0100/0x0200/0x0300 本身也可能是合法指令
        // （move v0,v0 等），因此只有被 31t 明确引用的偏移才按 payload 解析。
        // 同时统计指令条数：raw.Length 是多单元指令的高估上界，直接用其预容量会浪费内存。
        // Pass 1: collect payload target offsets (in code units) referenced by 31t instructions
        // (packed-switch 0x2b / sparse-switch 0x2c / fill-array-data 0x26). Since 0x0100/0x0200/0x0300
        // can also be ordinary instructions (e.g. move v0,v0), only offsets referenced by a 31t
        // instruction are decoded as payloads. The pass also counts instructions so the result list
        // is pre-sized accurately instead of over-allocating for raw.Length.
        List<int>? payloadTargets = null;
        var instructionCount = 0;
        var scan = 0;
        while (scan < raw.Length)
        {
            var scanUnit = raw[scan];
            var scanOpcode = (byte)(scanUnit >> 8);
            if (scanOpcode is 0x26 or 0x2b or 0x2c && scan + 2 < raw.Length)
            {
                var rel = (int)((uint)(raw[scan + 1] | (raw[scan + 2] << 16)));
                (payloadTargets ??= []).Add(scan + rel);
            }
            scan += Math.Max(1, OpCodes.GetSize(scanOpcode));
            instructionCount++;
        }

        // 第二遍：实际解码。
        // Pass 2: actual decode.
        var instructions = new List<Instruction>(instructionCount);
        var index = 0;
        while (index < raw.Length)
        {
            var unit = raw[index];
            var opcode = (byte)(unit >> 8);

            // 仅在 31t 引用的 payload 目标偏移处，才把标识字当作伪指令 payload 解析。
            // Only treat the ident word as a pseudo-instruction payload at 31t-referenced offsets.
            if (payloadTargets is { Count: > 0 } targets && targets.Contains(index)
                && (unit == 0x0100 || unit == 0x0200 || unit == 0x0300))
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
                    Operands = [unit & 0xff],
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

            var operands = DecodeOperands(raw, index, info, dex);
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
        switch (ident)
        {
            case 0x0100: // packed-switch-payload: ident, size, first_key(s32), targets[size]
                {
                    var size = raw[index + 1];
                    var firstKey = (int)((uint)(raw[index + 2] | (raw[index + 3] << 16)));
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
            case 0x0200: // sparse-switch-payload: ident, size, keys[size], targets[size]
                {
                    var size = raw[index + 1];
                    var keys = new int[size];
                    var targets = new int[size];
                    for (var i = 0; i < size; i++)
                    {
                        keys[i] = (int)((uint)(raw[index + 2 + i * 2] | (raw[index + 3 + i * 2] << 16)));
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
            default: // 0x0300 fill-array-data-payload: ident, element_width(u16), size(u32), data
                {
                    var width = raw[index + 1];
                    var size = raw[index + 2] | (raw[index + 3] << 16);
                    var dataUnits = (width * size + 1) / 2;
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
                        Operands = [new PayloadOperand("fill-array-data", width, payload)],
                        Size = 4 + dataUnits
                    });
                    return 4 + dataUnits;
                }
        }
    }

    private static IReadOnlyList<object?> DecodeOperands(ushort[] raw, int index, OpCodeInfo info, DexFile dex)
    {
        var unit0 = raw[index];
        var aNibble = unit0 & 0x0f;                 // vA / 低 4 位
        var bNibble = (unit0 >> 4) & 0x0f;          // vB / 高 4 位
        var aa = (byte)(unit0 & 0xff);              // vAA / 分支偏移

        switch (info.Format)
        {
            case InstructionFormat.Format10x:
                return [];
            case InstructionFormat.Format12x:
                return [aNibble, bNibble];
            case InstructionFormat.Format11n:
                {
                    var literal = SignExtend4((byte)((unit0 >> 4) & 0x0f));
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
                return [aa, raw[index + 1]];
            case InstructionFormat.Format21t:
                {
                    var rel = (short)raw[index + 1];
                    return [aa, new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format21s:
                return [aa, new LiteralOperand((short)raw[index + 1])];
            case InstructionFormat.Format21h:
                {
                    var value = (uint)(raw[index + 1] << 16);
                    return [aa, new LiteralOperand((int)value)];
                }
            case InstructionFormat.Format21c:
                return [aa, ResolveRef(dex, raw[index + 1], info.RefKind)];
            case InstructionFormat.Format23x:
                return [aa, (byte)(raw[index + 1] & 0xff), (byte)(raw[index + 1] >> 8)];
            case InstructionFormat.Format22b:
                return [aa, (byte)(raw[index + 1] & 0xff), new LiteralOperand((sbyte)(raw[index + 1] >> 8))];
            case InstructionFormat.Format22t:
                {
                    var rel = (short)raw[index + 1];
                    return [aNibble, bNibble, new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format22s:
                return [aNibble, bNibble, new LiteralOperand((short)raw[index + 1])];
            case InstructionFormat.Format22c:
                return [aNibble, bNibble, ResolveRef(dex, raw[index + 1], info.RefKind)];
            case InstructionFormat.Format30t:
                {
                    var rel = (int)((uint)(raw[index + 1] | (raw[index + 2] << 16)));
                    return [new BranchTargetOperand((index + rel) * 2)];
                }
            case InstructionFormat.Format32x:
                return [raw[index + 1], raw[index + 2]];
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
                    return [aa, ResolveRef(dex, refIdx, info.RefKind)];
                }
            case InstructionFormat.Format35c:
                {
                    var count = (byte)((unit0 >> 12) & 0x0f); // A 位于字节 1 高 4 位
                    var g = (byte)(unit0 & 0x0f);
                    // C..F 位于 unit2（字节 4-5），引用索引位于 unit1（字节 2-3）
                    // C..F reside in unit2 (bytes 4-5), the reference index in unit1 (bytes 2-3).
                    var c = (byte)(raw[index + 2] & 0x0f);
                    var d = (byte)((raw[index + 2] >> 4) & 0x0f);
                    var e = (byte)((raw[index + 2] >> 8) & 0x0f);
                    var f = (byte)((raw[index + 2] >> 12) & 0x0f);
                    // 仅取后 count 个寄存器（C..G 中从 G 往回数 count 个），顺序 C 先 G 后
                    // Only the last 'count' registers of {C,D,E,F,G} are arguments, in C-first order.
                    var regs = new int[count];
                    var pos = count;
                    if (count > 0) regs[--pos] = g;
                    if (count > 1) regs[--pos] = f;
                    if (count > 2) regs[--pos] = e;
                    if (count > 3) regs[--pos] = d;
                    if (count > 4) regs[--pos] = c;
                    var refIdx = raw[index + 1];
                    return [regs, ResolveRef(dex, refIdx, info.RefKind)];
                }
            case InstructionFormat.Format3rc:
                {
                    var count = aa;
                    var first = raw[index + 2];
                    var regs = new int[count];
                    for (var i = 0; i < count; i++) regs[i] = first + i;
                    var refIdx = raw[index + 1];
                    return [regs, ResolveRef(dex, refIdx, info.RefKind)];
                }
            case InstructionFormat.Format45cc:
                {
                    var count = (byte)((unit0 >> 12) & 0x0f);
                    var g = (byte)(unit0 & 0x0f);
                    // C..F 位于 unit2，method 引用位于 unit1，proto 引用位于 unit3
                    // C..F reside in unit2, the method reference in unit1, the proto reference in unit3.
                    var c = (byte)(raw[index + 2] & 0x0f);
                    var d = (byte)((raw[index + 2] >> 4) & 0x0f);
                    var e = (byte)((raw[index + 2] >> 8) & 0x0f);
                    var f = (byte)((raw[index + 2] >> 12) & 0x0f);
                    var regs = new int[count];
                    var pos = count;
                    if (count > 0) regs[--pos] = g;
                    if (count > 1) regs[--pos] = f;
                    if (count > 2) regs[--pos] = e;
                    if (count > 3) regs[--pos] = d;
                    if (count > 4) regs[--pos] = c;
                    var methodRef = raw[index + 1];
                    var protoRef = raw[index + 3];
                    return [regs, ResolveRef(dex, methodRef, info.RefKind), ResolveProtoRef(dex, protoRef)];
                }
            case InstructionFormat.Format4rcc:
                {
                    var count = aa;
                    var first = raw[index + 2];
                    var regs = new int[count];
                    for (var i = 0; i < count; i++) regs[i] = first + i;
                    var methodRef = raw[index + 1];
                    var protoRef = raw[index + 3];
                    return [regs, ResolveRef(dex, methodRef, info.RefKind), ResolveProtoRef(dex, protoRef)];
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

    private static object? ResolveRef(DexFile dex, int index, RefKind kind)
    {
        switch (kind)
        {
            case RefKind.String:
                return dex.GetString(index);
            case RefKind.Type:
                return new TypeRefOperand(dex.GetType(index));
            case RefKind.Field:
                {
                    var field = dex.GetField(index);
                    if (field == null) return index;
                    return new FieldRefOperand(dex.FormatType(field.ClassIndex), dex.GetString(field.NameIndex), dex.FormatType(field.TypeIndex));
                }
            case RefKind.Method:
                {
                    var method = dex.GetMethod(index);
                    if (method == null) return index;
                    var proto = dex.GetProto(method.ProtoIndex);
                    string[] parameters;
                    if (proto == null)
                    {
                        parameters = [];
                    }
                    else
                    {
                        // 手写循环代替 LINQ：invoke 指令热路径，避免 Select/ToArray 的迭代器与委托分配。
                        // Hand-rolled loop instead of LINQ: this is the invoke hot path, and it avoids
                        // iterator/delegate allocations from Select/ToArray.
                        var indices = proto.ParameterTypeIndices;
                        parameters = new string[indices.Length];
                        for (var i = 0; i < indices.Length; i++)
                        {
                            parameters[i] = dex.FormatType(indices[i]);
                        }
                    }
                    var returnType = proto == null ? "?" : dex.FormatType(proto.ReturnTypeIndex);
                    return new MethodRefOperand(dex.FormatType(method.ClassIndex), dex.GetString(method.NameIndex), returnType, parameters);
                }
            default:
                return index;
        }
    }

    private static object? ResolveProtoRef(DexFile dex, int index)
    {
        var proto = dex.GetProto(index);
        if (proto == null) return index;
        return $"({string.Join(", ", proto.ParameterTypeIndices.Select(dex.FormatType))}){dex.FormatType(proto.ReturnTypeIndex)}";
    }
}