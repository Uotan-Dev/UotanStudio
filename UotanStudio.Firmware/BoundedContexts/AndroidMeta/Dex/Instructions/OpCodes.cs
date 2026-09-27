namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Instructions;

/// <summary>
/// Encoding layout of a Dalvik instruction, named by total code-unit count and operand fields.
/// <para>Dalvik 指令的编码布局，按代码单元总数与操作数字段命名。</para>
/// </summary>
public enum InstructionFormat
{
    /// <summary>
    /// Opcode only; one code unit with no operands.
    /// <para>仅操作码；1 个代码单元，无操作数。</para>
    /// </summary>
    Format10x,
    /// <summary>
    /// One code unit holding two 4-bit register indices.
    /// <para>1 个代码单元，包含两个 4 位寄存器编号。</para>
    /// </summary>
    Format12x,
    /// <summary>
    /// One code unit holding a 4-bit register and a 4-bit signed literal.
    /// <para>1 个代码单元，包含一个 4 位寄存器与一个 4 位有符号字面量。</para>
    /// </summary>
    Format11n,
    /// <summary>
    /// One code unit holding a single 8-bit register index.
    /// <para>1 个代码单元，包含一个 8 位寄存器编号。</para>
    /// </summary>
    Format11x,
    /// <summary>
    /// One code unit with an 8-bit signed branch offset.
    /// <para>1 个代码单元，包含 8 位有符号跳转偏移。</para>
    /// </summary>
    Format10t,
    /// <summary>
    /// Two code units with a 16-bit signed branch offset.
    /// <para>2 个代码单元，包含 16 位有符号跳转偏移。</para>
    /// </summary>
    Format20t,
    /// <summary>
    /// Two code units: an 8-bit register and a 16-bit register index.
    /// <para>2 个代码单元：8 位寄存器与 16 位寄存器编号。</para>
    /// </summary>
    Format22x,
    /// <summary>
    /// Two code units: an 8-bit register and a 16-bit signed branch offset.
    /// <para>2 个代码单元：8 位寄存器与 16 位有符号跳转偏移。</para>
    /// </summary>
    Format21t,
    /// <summary>
    /// Two code units: an 8-bit register and a 16-bit signed literal.
    /// <para>2 个代码单元：8 位寄存器与 16 位有符号字面量。</para>
    /// </summary>
    Format21s,
    /// <summary>
    /// Two code units: an 8-bit register and a 16-bit literal placed in the high half of the value.
    /// <para>2 个代码单元：8 位寄存器与置于高位的 16 位字面量。</para>
    /// </summary>
    Format21h,
    /// <summary>
    /// Two code units: an 8-bit register and a 16-bit <see cref="RefKind"/>-typed constant-pool index.
    /// <para>2 个代码单元：8 位寄存器与按 <see cref="RefKind"/> 类型解析的 16 位常量池索引。</para>
    /// </summary>
    Format21c,
    /// <summary>
    /// Two code units holding three 8-bit register indices.
    /// <para>2 个代码单元，包含三个 8 位寄存器编号。</para>
    /// </summary>
    Format23x,
    /// <summary>
    /// Two code units: two 8-bit registers and an 8-bit signed literal.
    /// <para>2 个代码单元：两个 8 位寄存器与 8 位有符号字面量。</para>
    /// </summary>
    Format22b,
    /// <summary>
    /// Two code units: two 4-bit registers and a 16-bit signed branch offset.
    /// <para>2 个代码单元：两个 4 位寄存器与 16 位有符号跳转偏移。</para>
    /// </summary>
    Format22t,
    /// <summary>
    /// Two code units: two 4-bit registers and a 16-bit signed literal.
    /// <para>2 个代码单元：两个 4 位寄存器与 16 位有符号字面量。</para>
    /// </summary>
    Format22s,
    /// <summary>
    /// Two code units: two 4-bit registers and a 16-bit field or type index.
    /// <para>2 个代码单元：两个 4 位寄存器与 16 位字段或类型索引。</para>
    /// </summary>
    Format22c,
    /// <summary>
    /// Three code units with a 32-bit signed branch offset.
    /// <para>3 个代码单元，包含 32 位有符号跳转偏移。</para>
    /// </summary>
    Format30t,
    /// <summary>
    /// Three code units holding two 16-bit register indices.
    /// <para>3 个代码单元，包含两个 16 位寄存器编号。</para>
    /// </summary>
    Format32x,
    /// <summary>
    /// Three code units: an 8-bit register and a 32-bit signed literal.
    /// <para>3 个代码单元：8 位寄存器与 32 位有符号字面量。</para>
    /// </summary>
    Format31i,
    /// <summary>
    /// Three code units: an 8-bit register and a 32-bit signed branch offset.
    /// <para>3 个代码单元：8 位寄存器与 32 位有符号跳转偏移。</para>
    /// </summary>
    Format31t,
    /// <summary>
    /// Three code units: an 8-bit register and a 32-bit constant-pool index.
    /// <para>3 个代码单元：8 位寄存器与 32 位常量池索引。</para>
    /// </summary>
    Format31c,
    /// <summary>
    /// Three code units: up to five 4-bit registers and a 16-bit <see cref="RefKind"/>-typed index.
    /// <para>3 个代码单元：最多五个 4 位寄存器与一个按 <see cref="RefKind"/> 类型解析的 16 位索引。</para>
    /// </summary>
    Format35c,
    /// <summary>
    /// Three code units: a register range (start register plus count) and a 16-bit constant-pool index.
    /// <para>3 个代码单元：寄存器区间（起始寄存器加数量）与 16 位常量池索引。</para>
    /// </summary>
    Format3rc,
    /// <summary>
    /// Four code units: up to five 4-bit registers, a 16-bit method index and a 16-bit prototype index.
    /// <para>4 个代码单元：最多五个 4 位寄存器、16 位方法索引与 16 位原型索引。</para>
    /// </summary>
    Format45cc,
    /// <summary>
    /// Four code units: a register range, a 16-bit method index and a 16-bit prototype index.
    /// <para>4 个代码单元：寄存器区间、16 位方法索引与 16 位原型索引。</para>
    /// </summary>
    Format4rcc,
    /// <summary>
    /// Five code units: an 8-bit register and a 64-bit signed literal.
    /// <para>5 个代码单元：8 位寄存器与 64 位有符号字面量。</para>
    /// </summary>
    Format51l,
    /// <summary>
    /// Pseudo-format for switch and array-data payloads, which occupy their own run of code units.
    /// <para>switch 与数组数据有效负载的伪格式，占用独立的若干代码单元。</para>
    /// </summary>
    Payload
}

/// <summary>
/// Kind of constant-pool region an instruction's index operand points into.
/// <para>指令索引操作数所指向的常量池区域类型。</para>
/// </summary>
public enum RefKind
{
    /// <summary>
    /// The opcode carries no index operand.
    /// <para>操作码不含索引操作数。</para>
    /// </summary>
    None,
    /// <summary>
    /// Index into the string identifiers region.
    /// <para>指向字符串标识符区域。</para>
    /// </summary>
    String,
    /// <summary>
    /// Index into the type identifiers region.
    /// <para>指向类型标识符区域。</para>
    /// </summary>
    Type,
    /// <summary>
    /// Index into the field identifiers region.
    /// <para>指向字段标识符区域。</para>
    /// </summary>
    Field,
    /// <summary>
    /// Index into the method identifiers region.
    /// <para>指向方法标识符区域。</para>
    /// </summary>
    Method,
    /// <summary>
    /// Index into the method prototype identifiers region.
    /// <para>指向方法原型标识符区域。</para>
    /// </summary>
    Proto,
    /// <summary>
    /// Index into the call site identifiers region.
    /// <para>指向调用点标识符区域。</para>
    /// </summary>
    CallSite,
    /// <summary>
    /// Index into the method handle region.
    /// <para>指向方法句柄区域。</para>
    /// </summary>
    MethodHandle
}

/// <summary>
/// Immutable metadata for one Dalvik opcode: smali mnemonic, encoding format and constant-pool reference kind.
/// <para>单个 Dalvik 操作码的不可变元数据：smali 助记符、编码格式与常量池引用类型。</para>
/// </summary>
/// <param name="Mnemonic">
/// Smali-style mnemonic as used in disassembly output.
/// <para>反汇编输出中使用的 smali 风格助记符。</para>
/// </param>
/// <param name="Format">
/// Encoding layout of the instruction.
/// <para>指令的编码布局。</para>
/// </param>
/// <param name="RefKind">
/// Constant-pool kind the index operand points into, or <see cref="RefKind.None"/> when there is no reference.
/// <para>索引操作数指向的常量池类型；无引用时为 <see cref="RefKind.None"/>。</para>
/// </param>
public sealed record OpCodeInfo(string Mnemonic, InstructionFormat Format, RefKind RefKind);

/// <summary>
/// Static lookup table for standard DEX opcodes, including the DEX 038 and 039 additions,
/// exposing mnemonic, format, reference kind and instruction width per opcode.
/// <para>标准 DEX 操作码的静态查找表，包含 DEX 038 与 039 的新增指令，
/// 提供每个操作码的助记符、格式、引用类型与指令宽度查询。</para>
/// </summary>
public static class OpCodes
{
    private static readonly OpCodeInfo?[] Table = BuildTable();
    private static readonly byte[] Sizes = BuildSizes();

    /// <summary>
    /// Gets metadata for the given opcode; returns null for unknown opcodes.
    /// <para>获取指定操作码的元数据；未知操作码返回 null。</para>
    /// </summary>
    /// <param name="opcode">
    /// Opcode byte, taken from the low byte of a little-endian code unit.
    /// <para>操作码字节，取自小端代码单元的低字节。</para>
    /// </param>
    /// <returns>
    /// The opcode metadata, or <see langword="null"/> when no entry occupies the byte.
    /// <para>操作码元数据；该字节没有表项时返回 <see langword="null"/>。</para>
    /// </returns>
    public static OpCodeInfo? Get(byte opcode) => Table[opcode];

    /// <summary>Resolves an opcode by its smali mnemonic.
    /// <para>按 smali 助记符解析操作码。</para></summary>
    /// <param name="mnemonic">
    /// Mnemonic to match case-insensitively; <see langword="null"/> matches nothing.
    /// <para>以不区分大小写方式匹配的助记符；<see langword="null"/> 不匹配任何条目。</para>
    /// </param>
    /// <returns>
    /// The opcode byte together with its metadata for the first matching table entry,
    /// or <see langword="null"/> when nothing matches.
    /// <para>首个匹配表项的操作码字节及其元数据；无匹配时返回 <see langword="null"/>。</para>
    /// </returns>
    public static (byte Opcode, OpCodeInfo Info)? Find(string mnemonic)
    {
        for (var i = 0; i < Table.Length; i++)
        {
            if (Table[i]?.Mnemonic.Equals(mnemonic, StringComparison.OrdinalIgnoreCase) == true)
                return ((byte)i, Table[i]!);
        }
        return null;
    }

    /// <summary>
    /// Gets the instruction size in 16-bit code units.
    /// <para>获取指令大小（16 位代码单元数）。</para>
    /// </summary>
    /// <param name="opcode">Opcode byte to look up. <para>待查询的操作码字节。</para></param>
    /// <returns>
    /// Instruction width from 1 to 5 code units; the payload pseudo-format reports 1.
    /// <para>指令宽度，1 到 5 个代码单元；有效负载伪格式报告 1。</para>
    /// </returns>
    public static int GetSize(byte opcode) => Sizes[opcode];

    private static byte[] BuildSizes()
    {
        var sizes = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            sizes[i] = Table[i] is { } info ? SizeOf(info.Format) : (byte)1;
        }
        return sizes;
    }

    private static byte SizeOf(InstructionFormat format) => format switch
    {
        InstructionFormat.Format10x or InstructionFormat.Format12x or InstructionFormat.Format11n
            or InstructionFormat.Format11x or InstructionFormat.Format10t => 1,
        InstructionFormat.Format20t or InstructionFormat.Format22x or InstructionFormat.Format21t
            or InstructionFormat.Format21s or InstructionFormat.Format21h or InstructionFormat.Format21c
            or InstructionFormat.Format23x or InstructionFormat.Format22b or InstructionFormat.Format22t
            or InstructionFormat.Format22s or InstructionFormat.Format22c => 2,
        InstructionFormat.Format30t or InstructionFormat.Format32x or InstructionFormat.Format31i
            or InstructionFormat.Format31t or InstructionFormat.Format31c or InstructionFormat.Format35c
            or InstructionFormat.Format3rc => 3,
        InstructionFormat.Format45cc or InstructionFormat.Format4rcc => 4,
        InstructionFormat.Format51l => 5,
        _ => 1
    };

    private static OpCodeInfo[] BuildTable()
    {


        (byte Op, string Name, InstructionFormat Fmt, RefKind Ref)[] raw =
        [
            (0x00, "nop", InstructionFormat.Format10x, RefKind.None),
            (0x01, "move", InstructionFormat.Format12x, RefKind.None),
            (0x02, "move/from16", InstructionFormat.Format22x, RefKind.None),
            (0x03, "move/16", InstructionFormat.Format32x, RefKind.None),
            (0x04, "move-wide", InstructionFormat.Format12x, RefKind.None),
            (0x05, "move-wide/from16", InstructionFormat.Format22x, RefKind.None),
            (0x06, "move-wide/16", InstructionFormat.Format32x, RefKind.None),
            (0x07, "move-object", InstructionFormat.Format12x, RefKind.None),
            (0x08, "move-object/from16", InstructionFormat.Format22x, RefKind.None),
            (0x09, "move-object/16", InstructionFormat.Format32x, RefKind.None),
            (0x0a, "move-result", InstructionFormat.Format11x, RefKind.None),
            (0x0b, "move-result-wide", InstructionFormat.Format11x, RefKind.None),
            (0x0c, "move-result-object", InstructionFormat.Format11x, RefKind.None),
            (0x0d, "move-exception", InstructionFormat.Format11x, RefKind.None),
            (0x0e, "return-void", InstructionFormat.Format10x, RefKind.None),
            (0x0f, "return", InstructionFormat.Format11x, RefKind.None),
            (0x10, "return-wide", InstructionFormat.Format11x, RefKind.None),
            (0x11, "return-object", InstructionFormat.Format11x, RefKind.None),
            (0x12, "const/4", InstructionFormat.Format11n, RefKind.None),
            (0x13, "const/16", InstructionFormat.Format21s, RefKind.None),
            (0x14, "const", InstructionFormat.Format31i, RefKind.None),
            (0x15, "const/high16", InstructionFormat.Format21h, RefKind.None),
            (0x16, "const-wide/16", InstructionFormat.Format21s, RefKind.None),
            (0x17, "const-wide/32", InstructionFormat.Format31i, RefKind.None),
            (0x18, "const-wide", InstructionFormat.Format51l, RefKind.None),
            (0x19, "const-wide/high16", InstructionFormat.Format21h, RefKind.None),
            (0x1a, "const-string", InstructionFormat.Format21c, RefKind.String),
            (0x1b, "const-string/jumbo", InstructionFormat.Format31c, RefKind.String),
            (0x1c, "const-class", InstructionFormat.Format21c, RefKind.Type),
            (0x1d, "monitor-enter", InstructionFormat.Format11x, RefKind.None),
            (0x1e, "monitor-exit", InstructionFormat.Format11x, RefKind.None),
            (0x1f, "check-cast", InstructionFormat.Format21c, RefKind.Type),
            (0x20, "instance-of", InstructionFormat.Format22c, RefKind.Type),
            (0x21, "array-length", InstructionFormat.Format12x, RefKind.None),
            (0x22, "new-instance", InstructionFormat.Format21c, RefKind.Type),
            (0x23, "new-array", InstructionFormat.Format22c, RefKind.Type),
            (0x24, "filled-new-array", InstructionFormat.Format35c, RefKind.Type),
            (0x25, "filled-new-array/range", InstructionFormat.Format3rc, RefKind.Type),
            (0x26, "fill-array-data", InstructionFormat.Format31t, RefKind.None),
            (0x27, "throw", InstructionFormat.Format11x, RefKind.None),
            (0x28, "goto", InstructionFormat.Format10t, RefKind.None),
            (0x29, "goto/16", InstructionFormat.Format20t, RefKind.None),
            (0x2a, "goto/32", InstructionFormat.Format30t, RefKind.None),
            (0x2b, "packed-switch", InstructionFormat.Format31t, RefKind.None),
            (0x2c, "sparse-switch", InstructionFormat.Format31t, RefKind.None),
            (0x2d, "cmpl-float", InstructionFormat.Format23x, RefKind.None),
            (0x2e, "cmpg-float", InstructionFormat.Format23x, RefKind.None),
            (0x2f, "cmpl-double", InstructionFormat.Format23x, RefKind.None),
            (0x30, "cmpg-double", InstructionFormat.Format23x, RefKind.None),
            (0x31, "cmp-long", InstructionFormat.Format23x, RefKind.None),
            (0x32, "if-eq", InstructionFormat.Format22t, RefKind.None),
            (0x33, "if-ne", InstructionFormat.Format22t, RefKind.None),
            (0x34, "if-lt", InstructionFormat.Format22t, RefKind.None),
            (0x35, "if-ge", InstructionFormat.Format22t, RefKind.None),
            (0x36, "if-gt", InstructionFormat.Format22t, RefKind.None),
            (0x37, "if-le", InstructionFormat.Format22t, RefKind.None),
            (0x38, "if-eqz", InstructionFormat.Format21t, RefKind.None),
            (0x39, "if-nez", InstructionFormat.Format21t, RefKind.None),
            (0x3a, "if-ltz", InstructionFormat.Format21t, RefKind.None),
            (0x3b, "if-gez", InstructionFormat.Format21t, RefKind.None),
            (0x3c, "if-gtz", InstructionFormat.Format21t, RefKind.None),
            (0x3d, "if-lez", InstructionFormat.Format21t, RefKind.None),
            (0x44, "aget", InstructionFormat.Format23x, RefKind.None),
            (0x45, "aget-wide", InstructionFormat.Format23x, RefKind.None),
            (0x46, "aget-object", InstructionFormat.Format23x, RefKind.None),
            (0x47, "aget-boolean", InstructionFormat.Format23x, RefKind.None),
            (0x48, "aget-byte", InstructionFormat.Format23x, RefKind.None),
            (0x49, "aget-char", InstructionFormat.Format23x, RefKind.None),
            (0x4a, "aget-short", InstructionFormat.Format23x, RefKind.None),
            (0x4b, "aput", InstructionFormat.Format23x, RefKind.None),
            (0x4c, "aput-wide", InstructionFormat.Format23x, RefKind.None),
            (0x4d, "aput-object", InstructionFormat.Format23x, RefKind.None),
            (0x4e, "aput-boolean", InstructionFormat.Format23x, RefKind.None),
            (0x4f, "aput-byte", InstructionFormat.Format23x, RefKind.None),
            (0x50, "aput-char", InstructionFormat.Format23x, RefKind.None),
            (0x51, "aput-short", InstructionFormat.Format23x, RefKind.None),
            (0x52, "iget", InstructionFormat.Format22c, RefKind.Field),
            (0x53, "iget-wide", InstructionFormat.Format22c, RefKind.Field),
            (0x54, "iget-object", InstructionFormat.Format22c, RefKind.Field),
            (0x55, "iget-boolean", InstructionFormat.Format22c, RefKind.Field),
            (0x56, "iget-byte", InstructionFormat.Format22c, RefKind.Field),
            (0x57, "iget-char", InstructionFormat.Format22c, RefKind.Field),
            (0x58, "iget-short", InstructionFormat.Format22c, RefKind.Field),
            (0x59, "iput", InstructionFormat.Format22c, RefKind.Field),
            (0x5a, "iput-wide", InstructionFormat.Format22c, RefKind.Field),
            (0x5b, "iput-object", InstructionFormat.Format22c, RefKind.Field),
            (0x5c, "iput-boolean", InstructionFormat.Format22c, RefKind.Field),
            (0x5d, "iput-byte", InstructionFormat.Format22c, RefKind.Field),
            (0x5e, "iput-char", InstructionFormat.Format22c, RefKind.Field),
            (0x5f, "iput-short", InstructionFormat.Format22c, RefKind.Field),
            (0x60, "sget", InstructionFormat.Format21c, RefKind.Field),
            (0x61, "sget-wide", InstructionFormat.Format21c, RefKind.Field),
            (0x62, "sget-object", InstructionFormat.Format21c, RefKind.Field),
            (0x63, "sget-boolean", InstructionFormat.Format21c, RefKind.Field),
            (0x64, "sget-byte", InstructionFormat.Format21c, RefKind.Field),
            (0x65, "sget-char", InstructionFormat.Format21c, RefKind.Field),
            (0x66, "sget-short", InstructionFormat.Format21c, RefKind.Field),
            (0x67, "sput", InstructionFormat.Format21c, RefKind.Field),
            (0x68, "sput-wide", InstructionFormat.Format21c, RefKind.Field),
            (0x69, "sput-object", InstructionFormat.Format21c, RefKind.Field),
            (0x6a, "sput-boolean", InstructionFormat.Format21c, RefKind.Field),
            (0x6b, "sput-byte", InstructionFormat.Format21c, RefKind.Field),
            (0x6c, "sput-char", InstructionFormat.Format21c, RefKind.Field),
            (0x6d, "sput-short", InstructionFormat.Format21c, RefKind.Field),
            (0x6e, "invoke-virtual", InstructionFormat.Format35c, RefKind.Method),
            (0x6f, "invoke-super", InstructionFormat.Format35c, RefKind.Method),
            (0x70, "invoke-direct", InstructionFormat.Format35c, RefKind.Method),
            (0x71, "invoke-static", InstructionFormat.Format35c, RefKind.Method),
            (0x72, "invoke-interface", InstructionFormat.Format35c, RefKind.Method),
            (0x74, "invoke-virtual/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x75, "invoke-super/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x76, "invoke-direct/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x77, "invoke-static/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x78, "invoke-interface/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x7b, "neg-int", InstructionFormat.Format12x, RefKind.None),
            (0x7c, "not-int", InstructionFormat.Format12x, RefKind.None),
            (0x7d, "neg-long", InstructionFormat.Format12x, RefKind.None),
            (0x7e, "not-long", InstructionFormat.Format12x, RefKind.None),
            (0x7f, "neg-float", InstructionFormat.Format12x, RefKind.None),
            (0x80, "neg-double", InstructionFormat.Format12x, RefKind.None),
            (0x81, "int-to-long", InstructionFormat.Format12x, RefKind.None),
            (0x82, "int-to-float", InstructionFormat.Format12x, RefKind.None),
            (0x83, "int-to-double", InstructionFormat.Format12x, RefKind.None),
            (0x84, "long-to-int", InstructionFormat.Format12x, RefKind.None),
            (0x85, "long-to-float", InstructionFormat.Format12x, RefKind.None),
            (0x86, "long-to-double", InstructionFormat.Format12x, RefKind.None),
            (0x87, "float-to-int", InstructionFormat.Format12x, RefKind.None),
            (0x88, "float-to-long", InstructionFormat.Format12x, RefKind.None),
            (0x89, "float-to-double", InstructionFormat.Format12x, RefKind.None),
            (0x8a, "double-to-int", InstructionFormat.Format12x, RefKind.None),
            (0x8b, "double-to-long", InstructionFormat.Format12x, RefKind.None),
            (0x8c, "double-to-float", InstructionFormat.Format12x, RefKind.None),
            (0x8d, "int-to-byte", InstructionFormat.Format12x, RefKind.None),
            (0x8e, "int-to-char", InstructionFormat.Format12x, RefKind.None),
            (0x8f, "int-to-short", InstructionFormat.Format12x, RefKind.None),
            (0x90, "add-int", InstructionFormat.Format23x, RefKind.None),
            (0x91, "sub-int", InstructionFormat.Format23x, RefKind.None),
            (0x92, "mul-int", InstructionFormat.Format23x, RefKind.None),
            (0x93, "div-int", InstructionFormat.Format23x, RefKind.None),
            (0x94, "rem-int", InstructionFormat.Format23x, RefKind.None),
            (0x95, "and-int", InstructionFormat.Format23x, RefKind.None),
            (0x96, "or-int", InstructionFormat.Format23x, RefKind.None),
            (0x97, "xor-int", InstructionFormat.Format23x, RefKind.None),
            (0x98, "shl-int", InstructionFormat.Format23x, RefKind.None),
            (0x99, "shr-int", InstructionFormat.Format23x, RefKind.None),
            (0x9a, "ushr-int", InstructionFormat.Format23x, RefKind.None),
            (0x9b, "add-long", InstructionFormat.Format23x, RefKind.None),
            (0x9c, "sub-long", InstructionFormat.Format23x, RefKind.None),
            (0x9d, "mul-long", InstructionFormat.Format23x, RefKind.None),
            (0x9e, "div-long", InstructionFormat.Format23x, RefKind.None),
            (0x9f, "rem-long", InstructionFormat.Format23x, RefKind.None),
            (0xa0, "and-long", InstructionFormat.Format23x, RefKind.None),
            (0xa1, "or-long", InstructionFormat.Format23x, RefKind.None),
            (0xa2, "xor-long", InstructionFormat.Format23x, RefKind.None),
            (0xa3, "shl-long", InstructionFormat.Format23x, RefKind.None),
            (0xa4, "shr-long", InstructionFormat.Format23x, RefKind.None),
            (0xa5, "ushr-long", InstructionFormat.Format23x, RefKind.None),
            (0xa6, "add-float", InstructionFormat.Format23x, RefKind.None),
            (0xa7, "sub-float", InstructionFormat.Format23x, RefKind.None),
            (0xa8, "mul-float", InstructionFormat.Format23x, RefKind.None),
            (0xa9, "div-float", InstructionFormat.Format23x, RefKind.None),
            (0xaa, "rem-float", InstructionFormat.Format23x, RefKind.None),
            (0xab, "add-double", InstructionFormat.Format23x, RefKind.None),
            (0xac, "sub-double", InstructionFormat.Format23x, RefKind.None),
            (0xad, "mul-double", InstructionFormat.Format23x, RefKind.None),
            (0xae, "div-double", InstructionFormat.Format23x, RefKind.None),
            (0xaf, "rem-double", InstructionFormat.Format23x, RefKind.None),
            (0xb0, "add-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb1, "sub-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb2, "mul-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb3, "div-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb4, "rem-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb5, "and-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb6, "or-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb7, "xor-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb8, "shl-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb9, "shr-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xba, "ushr-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbb, "add-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbc, "sub-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbd, "mul-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbe, "div-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbf, "rem-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc0, "and-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc1, "or-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc2, "xor-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc3, "shl-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc4, "shr-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc5, "ushr-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc6, "add-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc7, "sub-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc8, "mul-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc9, "div-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xca, "rem-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xcb, "add-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xcc, "sub-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xcd, "mul-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xce, "div-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xcf, "rem-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xd0, "add-int/lit16", InstructionFormat.Format22s, RefKind.None),
            (0xd1, "rsub-int", InstructionFormat.Format22s, RefKind.None),
            (0xd2, "mul-int/lit16", InstructionFormat.Format22s, RefKind.None),
            (0xd3, "div-int/lit16", InstructionFormat.Format22s, RefKind.None),
            (0xd4, "rem-int/lit16", InstructionFormat.Format22s, RefKind.None),
            (0xd5, "and-int/lit16", InstructionFormat.Format22s, RefKind.None),
            (0xd6, "or-int/lit16", InstructionFormat.Format22s, RefKind.None),
            (0xd7, "xor-int/lit16", InstructionFormat.Format22s, RefKind.None),
            (0xd8, "add-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd9, "rsub-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xda, "mul-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xdb, "div-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xdc, "rem-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xdd, "and-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xde, "or-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xdf, "xor-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xe0, "shl-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xe1, "shr-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xe2, "ushr-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xfa, "invoke-polymorphic", InstructionFormat.Format45cc, RefKind.Method),
            (0xfb, "invoke-polymorphic/range", InstructionFormat.Format4rcc, RefKind.Method),
            (0xfc, "invoke-custom", InstructionFormat.Format35c, RefKind.CallSite),
            (0xfd, "invoke-custom/range", InstructionFormat.Format3rc, RefKind.CallSite),
            (0xfe, "const-method-handle", InstructionFormat.Format21c, RefKind.MethodHandle),
            (0xff, "const-method-type", InstructionFormat.Format21c, RefKind.Proto),
        ];

        var table = new OpCodeInfo[256];
        foreach (var (op, name, fmt, refKind) in raw)
        {
            table[op] = new OpCodeInfo(name, fmt, refKind);
        }
        return table;
    }
}