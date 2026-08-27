namespace UotanStudio.Firmware.Dex.Instructions;

/// <summary>
/// <para>指令格式（按 Google dalvik-bytecode 规范定义）。</para>
/// Instruction format (per the Google dalvik-bytecode specification).
/// </summary>
public enum InstructionFormat
{
    /// <summary>10x：无操作数，1 个代码单元。<para>No operands, 1 code unit.</para></summary>
    Format10x,
    /// <summary>12x：vA, vB（各 4 位）。<para>vA, vB (4 bits each).</para></summary>
    Format12x,
    /// <summary>11n：vA, #+B（4 位字面量）。<para>vA, #+B (4-bit literal).</para></summary>
    Format11n,
    /// <summary>11x：vAA。<para>vAA.</para></summary>
    Format11x,
    /// <summary>10t：+AA 分支。</summary>
    Format10t,
    /// <summary>20t：+AAAA 分支。</summary>
    Format20t,
    /// <summary>22x：vAA, vBBBB。</summary>
    Format22x,
    /// <summary>21t：vAA, +BBBB 分支。</summary>
    Format21t,
    /// <summary>21s：vAA, #+BBBB。</summary>
    Format21s,
    /// <summary>21h：vAA, #+BBBB0000。</summary>
    Format21h,
    /// <summary>21c：vAA, ref@BBBB。</summary>
    Format21c,
    /// <summary>23x：vAA, vBB, vCC。</summary>
    Format23x,
    /// <summary>22b：vAA, vBB, #+CC。</summary>
    Format22b,
    /// <summary>22t：vA, vB, +CCCC 分支。</summary>
    Format22t,
    /// <summary>22s：vA, vB, #+CCCC。</summary>
    Format22s,
    /// <summary>22c：vA, vB, ref@CCCC。</summary>
    Format22c,
    /// <summary>30t：+AAAAAAAA 分支。</summary>
    Format30t,
    /// <summary>32x：vAAAA, vBBBB。</summary>
    Format32x,
    /// <summary>31i：vAA, #+BBBBBBBB。</summary>
    Format31i,
    /// <summary>31t：vAA, +BBBBBBBB 分支。</summary>
    Format31t,
    /// <summary>31c：vAA, ref@BBBBBBBB。</summary>
    Format31c,
    /// <summary>35c：{vC..vG}, ref@BBBB。</summary>
    Format35c,
    /// <summary>3rc：{vCCCC..}, ref@BBBB。</summary>
    Format3rc,
    /// <summary>45cc：{vC..vG}, ref@BBBB, proto@HHHH。</summary>
    Format45cc,
    /// <summary>4rcc：{vCCCC..}, ref@BBBB, proto@HHHH。</summary>
    Format4rcc,
    /// <summary>51l：vAA, #+64 位字面量。</summary>
    Format51l,
    /// <summary>伪指令 payload（switch / fill-array-data）。</summary>
    Payload
}

/// <summary>
/// <para>引用型操作数种类（21c/22c/35c 等的索引目标）。</para>
/// Kind of reference operand (index target of 21c/22c/35c etc.).
/// </summary>
public enum RefKind
{
    /// <summary>无引用。<para>No reference.</para></summary>
    None,
    /// <summary>字符串索引。<para>String index.</para></summary>
    String,
    /// <summary>类型索引。<para>Type index.</para></summary>
    Type,
    /// <summary>字段索引。<para>Field index.</para></summary>
    Field,
    /// <summary>方法索引。<para>Method index.</para></summary>
    Method,
    /// <summary>方法原型索引（invoke-polymorphic）。<para>Prototype index (invoke-polymorphic).</para></summary>
    Proto,
    /// <summary>调用点索引（invoke-custom）。<para>Call-site index (invoke-custom).</para></summary>
    CallSite,
    /// <summary>方法句柄索引（const-method-handle）。<para>Method-handle index (const-method-handle).</para></summary>
    MethodHandle
}

/// <summary>
/// <para>操作码元数据：助记符、格式与引用种类。</para>
/// Opcode metadata: mnemonic, format and reference kind.
/// </summary>
public sealed record OpCodeInfo(string Mnemonic, InstructionFormat Format, RefKind RefKind);

/// <summary>
/// <para>完整 Dalvik 操作码表（0x00-0xFF），按 Google dalvik-bytecode 官方定义实现。</para>
/// Complete Dalvik opcode table (0x00-0xFF), implemented per the Google dalvik-bytecode official definition.
/// </summary>
public static class OpCodes
{
    private static readonly OpCodeInfo[] Table = BuildTable();
    private static readonly byte[] Sizes = BuildSizes();

    /// <summary>
    /// <para>获取指定操作码的元数据；未知操作码返回 null。</para>
    /// Gets metadata for the given opcode; returns null for unknown opcodes.
    /// </summary>
    public static OpCodeInfo? Get(byte opcode) => Table[opcode];

    /// <summary>
    /// <para>获取指令大小（16 位代码单元数）。</para>
    /// Gets the instruction size in 16-bit code units.
    /// </summary>
    public static int GetSize(byte opcode) => Sizes[opcode];

    private static byte[] BuildSizes()
    {
        var sizes = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            sizes[i] = SizeOf(Table[i].Format);
        }
        return sizes;
    }

    private static byte SizeOf(InstructionFormat format) => format switch
    {
        InstructionFormat.Format10x or InstructionFormat.Format12x or InstructionFormat.Format11n
            or InstructionFormat.Format11x or InstructionFormat.Format10t => 1,
        InstructionFormat.Format20t or InstructionFormat.Format22x or InstructionFormat.Format21t
            or InstructionFormat.Format21s or InstructionFormat.Format21h or InstructionFormat.Format21c
            or InstructionFormat.Format23x or InstructionFormat.Format22b => 2,
        InstructionFormat.Format22t or InstructionFormat.Format22s or InstructionFormat.Format22c
            or InstructionFormat.Format30t or InstructionFormat.Format32x or InstructionFormat.Format31i
            or InstructionFormat.Format31t or InstructionFormat.Format31c or InstructionFormat.Format35c
            or InstructionFormat.Format3rc => 3,
        InstructionFormat.Format45cc or InstructionFormat.Format4rcc => 4,
        InstructionFormat.Format51l => 5,
        _ => 1
    };

    private static OpCodeInfo[] BuildTable()
    {
        // (opcode, mnemonic, format, refKind) —— 完整官方表
        // (opcode, mnemonic, format, refKind) — the complete official table.
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
            (0x23, "new-array", InstructionFormat.Format21c, RefKind.Type),
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
            (0x3e, "aget", InstructionFormat.Format23x, RefKind.None),
            (0x3f, "aget-wide", InstructionFormat.Format23x, RefKind.None),
            (0x40, "aget-object", InstructionFormat.Format23x, RefKind.None),
            (0x41, "aget-boolean", InstructionFormat.Format23x, RefKind.None),
            (0x42, "aget-byte", InstructionFormat.Format23x, RefKind.None),
            (0x43, "aget-char", InstructionFormat.Format23x, RefKind.None),
            (0x44, "aget-short", InstructionFormat.Format23x, RefKind.None),
            (0x45, "aput", InstructionFormat.Format23x, RefKind.None),
            (0x46, "aput-wide", InstructionFormat.Format23x, RefKind.None),
            (0x47, "aput-object", InstructionFormat.Format23x, RefKind.None),
            (0x48, "aput-boolean", InstructionFormat.Format23x, RefKind.None),
            (0x49, "aput-byte", InstructionFormat.Format23x, RefKind.None),
            (0x4a, "aput-char", InstructionFormat.Format23x, RefKind.None),
            (0x4b, "aput-short", InstructionFormat.Format23x, RefKind.None),
            (0x4c, "iget", InstructionFormat.Format22c, RefKind.Field),
            (0x4d, "iget-wide", InstructionFormat.Format22c, RefKind.Field),
            (0x4e, "iget-object", InstructionFormat.Format22c, RefKind.Field),
            (0x4f, "iget-boolean", InstructionFormat.Format22c, RefKind.Field),
            (0x50, "iget-byte", InstructionFormat.Format22c, RefKind.Field),
            (0x51, "iget-char", InstructionFormat.Format22c, RefKind.Field),
            (0x52, "iget-short", InstructionFormat.Format22c, RefKind.Field),
            (0x53, "iput", InstructionFormat.Format22c, RefKind.Field),
            (0x54, "iput-wide", InstructionFormat.Format22c, RefKind.Field),
            (0x55, "iput-object", InstructionFormat.Format22c, RefKind.Field),
            (0x56, "iput-boolean", InstructionFormat.Format22c, RefKind.Field),
            (0x57, "iput-byte", InstructionFormat.Format22c, RefKind.Field),
            (0x58, "iput-char", InstructionFormat.Format22c, RefKind.Field),
            (0x59, "iput-short", InstructionFormat.Format22c, RefKind.Field),
            (0x5a, "sget", InstructionFormat.Format21c, RefKind.Field),
            (0x5b, "sget-wide", InstructionFormat.Format21c, RefKind.Field),
            (0x5c, "sget-object", InstructionFormat.Format21c, RefKind.Field),
            (0x5d, "sget-boolean", InstructionFormat.Format21c, RefKind.Field),
            (0x5e, "sget-byte", InstructionFormat.Format21c, RefKind.Field),
            (0x5f, "sget-char", InstructionFormat.Format21c, RefKind.Field),
            (0x60, "sget-short", InstructionFormat.Format21c, RefKind.Field),
            (0x61, "sput", InstructionFormat.Format21c, RefKind.Field),
            (0x62, "sput-wide", InstructionFormat.Format21c, RefKind.Field),
            (0x63, "sput-object", InstructionFormat.Format21c, RefKind.Field),
            (0x64, "sput-boolean", InstructionFormat.Format21c, RefKind.Field),
            (0x65, "sput-byte", InstructionFormat.Format21c, RefKind.Field),
            (0x66, "sput-char", InstructionFormat.Format21c, RefKind.Field),
            (0x67, "sput-short", InstructionFormat.Format21c, RefKind.Field),
            (0x68, "invoke-virtual", InstructionFormat.Format35c, RefKind.Method),
            (0x69, "invoke-super", InstructionFormat.Format35c, RefKind.Method),
            (0x6a, "invoke-direct", InstructionFormat.Format35c, RefKind.Method),
            (0x6b, "invoke-static", InstructionFormat.Format35c, RefKind.Method),
            (0x6c, "invoke-interface", InstructionFormat.Format35c, RefKind.Method),
            (0x6d, "invoke-virtual/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x6e, "invoke-super/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x6f, "invoke-direct/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x70, "invoke-static/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x71, "invoke-interface/range", InstructionFormat.Format3rc, RefKind.Method),
            (0x72, "neg-int", InstructionFormat.Format10x, RefKind.None),
            (0x73, "not-int", InstructionFormat.Format12x, RefKind.None),
            (0x74, "neg-long", InstructionFormat.Format10x, RefKind.None),
            (0x75, "not-long", InstructionFormat.Format12x, RefKind.None),
            (0x76, "neg-float", InstructionFormat.Format10x, RefKind.None),
            (0x77, "neg-double", InstructionFormat.Format10x, RefKind.None),
            (0x78, "int-to-long", InstructionFormat.Format12x, RefKind.None),
            (0x79, "int-to-float", InstructionFormat.Format12x, RefKind.None),
            (0x7a, "int-to-double", InstructionFormat.Format12x, RefKind.None),
            (0x7b, "long-to-int", InstructionFormat.Format12x, RefKind.None),
            (0x7c, "long-to-float", InstructionFormat.Format12x, RefKind.None),
            (0x7d, "long-to-double", InstructionFormat.Format12x, RefKind.None),
            (0x7e, "float-to-int", InstructionFormat.Format12x, RefKind.None),
            (0x7f, "float-to-long", InstructionFormat.Format12x, RefKind.None),
            (0x80, "float-to-double", InstructionFormat.Format12x, RefKind.None),
            (0x81, "double-to-int", InstructionFormat.Format12x, RefKind.None),
            (0x82, "double-to-long", InstructionFormat.Format12x, RefKind.None),
            (0x83, "double-to-float", InstructionFormat.Format12x, RefKind.None),
            (0x84, "int-to-byte", InstructionFormat.Format12x, RefKind.None),
            (0x85, "int-to-char", InstructionFormat.Format12x, RefKind.None),
            (0x86, "int-to-short", InstructionFormat.Format12x, RefKind.None),
            (0x87, "add-int", InstructionFormat.Format23x, RefKind.None),
            (0x88, "sub-int", InstructionFormat.Format23x, RefKind.None),
            (0x89, "mul-int", InstructionFormat.Format23x, RefKind.None),
            (0x8a, "div-int", InstructionFormat.Format23x, RefKind.None),
            (0x8b, "rem-int", InstructionFormat.Format23x, RefKind.None),
            (0x8c, "and-int", InstructionFormat.Format23x, RefKind.None),
            (0x8d, "or-int", InstructionFormat.Format23x, RefKind.None),
            (0x8e, "xor-int", InstructionFormat.Format23x, RefKind.None),
            (0x8f, "shl-int", InstructionFormat.Format23x, RefKind.None),
            (0x90, "shr-int", InstructionFormat.Format23x, RefKind.None),
            (0x91, "ushr-int", InstructionFormat.Format23x, RefKind.None),
            (0x92, "add-long", InstructionFormat.Format23x, RefKind.None),
            (0x93, "sub-long", InstructionFormat.Format23x, RefKind.None),
            (0x94, "mul-long", InstructionFormat.Format23x, RefKind.None),
            (0x95, "div-long", InstructionFormat.Format23x, RefKind.None),
            (0x96, "rem-long", InstructionFormat.Format23x, RefKind.None),
            (0x97, "and-long", InstructionFormat.Format23x, RefKind.None),
            (0x98, "or-long", InstructionFormat.Format23x, RefKind.None),
            (0x99, "xor-long", InstructionFormat.Format23x, RefKind.None),
            (0x9a, "shl-long", InstructionFormat.Format23x, RefKind.None),
            (0x9b, "shr-long", InstructionFormat.Format23x, RefKind.None),
            (0x9c, "ushr-long", InstructionFormat.Format23x, RefKind.None),
            (0x9d, "add-float", InstructionFormat.Format23x, RefKind.None),
            (0x9e, "sub-float", InstructionFormat.Format23x, RefKind.None),
            (0x9f, "mul-float", InstructionFormat.Format23x, RefKind.None),
            (0xa0, "div-float", InstructionFormat.Format23x, RefKind.None),
            (0xa1, "rem-float", InstructionFormat.Format23x, RefKind.None),
            (0xa2, "add-double", InstructionFormat.Format23x, RefKind.None),
            (0xa3, "sub-double", InstructionFormat.Format23x, RefKind.None),
            (0xa4, "mul-double", InstructionFormat.Format23x, RefKind.None),
            (0xa5, "div-double", InstructionFormat.Format23x, RefKind.None),
            (0xa6, "rem-double", InstructionFormat.Format23x, RefKind.None),
            (0xa7, "add-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xa8, "sub-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xa9, "mul-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xaa, "div-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xab, "rem-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xac, "and-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xad, "or-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xae, "xor-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xaf, "shl-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb0, "shr-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb1, "ushr-int/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb2, "add-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb3, "sub-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb4, "mul-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb5, "div-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb6, "rem-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb7, "and-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb8, "or-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xb9, "xor-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xba, "shl-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbb, "shr-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbc, "ushr-long/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbd, "add-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbe, "sub-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xbf, "mul-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc0, "div-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc1, "rem-float/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc2, "add-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc3, "sub-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc4, "mul-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc5, "div-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc6, "rem-double/2addr", InstructionFormat.Format12x, RefKind.None),
            (0xc7, "add-int/lit16", InstructionFormat.Format22b, RefKind.None),
            (0xc8, "rsub-int", InstructionFormat.Format22s, RefKind.None),
            (0xc9, "mul-int/lit16", InstructionFormat.Format22b, RefKind.None),
            (0xca, "div-int/lit16", InstructionFormat.Format22b, RefKind.None),
            (0xcb, "rem-int/lit16", InstructionFormat.Format22b, RefKind.None),
            (0xcc, "and-int/lit16", InstructionFormat.Format22b, RefKind.None),
            (0xcd, "or-int/lit16", InstructionFormat.Format22b, RefKind.None),
            (0xce, "xor-int/lit16", InstructionFormat.Format22b, RefKind.None),
            (0xcf, "add-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd0, "rsub-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd1, "mul-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd2, "div-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd3, "rem-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd4, "and-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd5, "or-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd6, "xor-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd7, "shl-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd8, "shr-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xd9, "ushr-int/lit8", InstructionFormat.Format22b, RefKind.None),
            (0xda, "iget-volatile", InstructionFormat.Format22c, RefKind.Field),
            (0xdb, "iput-volatile", InstructionFormat.Format22c, RefKind.Field),
            (0xdc, "sget-volatile", InstructionFormat.Format21c, RefKind.Field),
            (0xdd, "sput-volatile", InstructionFormat.Format21c, RefKind.Field),
            (0xde, "iget-object-volatile", InstructionFormat.Format22c, RefKind.Field),
            (0xdf, "iput-object-volatile", InstructionFormat.Format22c, RefKind.Field),
            (0xe0, "sget-object-volatile", InstructionFormat.Format21c, RefKind.Field),
            (0xe1, "sput-object-volatile", InstructionFormat.Format21c, RefKind.Field),
            (0xe2, "throw-verification-error", InstructionFormat.Format20t, RefKind.None),
            (0xe3, "execute-inline", InstructionFormat.Format35c, RefKind.Method),
            (0xe4, "execute-inline/range", InstructionFormat.Format3rc, RefKind.Method),
            (0xe5, "invoke-object-init", InstructionFormat.Format35c, RefKind.Method),
            (0xe6, "invoke-object-init/range", InstructionFormat.Format3rc, RefKind.Method),
            (0xe7, "iget-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xe8, "iget-wide-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xe9, "iget-object-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xea, "iput-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xeb, "iput-wide-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xec, "iput-object-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xed, "iget-boolean-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xee, "iget-byte-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xef, "iget-char-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xf0, "iget-short-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xf1, "iput-boolean-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xf2, "iput-byte-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xf3, "iput-char-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xf4, "iput-short-quick", InstructionFormat.Format22c, RefKind.Field),
            (0xf5, "iget-volatile", InstructionFormat.Format22c, RefKind.Field),
            (0xf6, "iput-volatile", InstructionFormat.Format22c, RefKind.Field),
            (0xf7, "sget-volatile", InstructionFormat.Format21c, RefKind.Field),
            (0xf8, "sput-volatile", InstructionFormat.Format21c, RefKind.Field),
            (0xf9, "iget-object-volatile", InstructionFormat.Format22c, RefKind.Field),
            (0xfa, "iput-object-volatile", InstructionFormat.Format22c, RefKind.Field),
            (0xfb, "sget-object-volatile", InstructionFormat.Format21c, RefKind.Field),
            (0xfc, "sput-object-volatile", InstructionFormat.Format21c, RefKind.Field),
            (0xfd, "invoke-polymorphic", InstructionFormat.Format45cc, RefKind.Method),
            (0xfe, "invoke-polymorphic/range", InstructionFormat.Format4rcc, RefKind.Method),
            (0xff, "invoke-custom", InstructionFormat.Format45cc, RefKind.CallSite),
        ];

        var table = new OpCodeInfo[256];
        foreach (var (op, name, fmt, refKind) in raw)
        {
            table[op] = new OpCodeInfo(name, fmt, refKind);
        }
        return table;
    }
}