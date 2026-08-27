namespace UotanStudio.Firmware.Dex.Model;

/// <summary>
/// <para>数值字面量操作数（const 系列，展示时带 # 前缀）。</para>
/// Numeric literal operand (const family; shown with a '#' prefix).
/// </summary>
public sealed record LiteralOperand(long Value);

/// <summary>
/// <para>类型引用操作数（const-class / new-instance / check-cast 等）。</para>
/// Type reference operand (const-class / new-instance / check-cast etc.).
/// </summary>
public sealed record TypeRefOperand(string Descriptor);

/// <summary>
/// <para>字段引用操作数（iget / iput / sget / sput 等）。</para>
/// Field reference operand (iget / iput / sget / sput etc.).
/// </summary>
public sealed record FieldRefOperand(string OwnerClass, string Name, string Type);

/// <summary>
/// <para>方法引用操作数（invoke-* 系列）。</para>
/// Method reference operand (invoke-* family).
/// </summary>
public sealed record MethodRefOperand(string OwnerClass, string Name, string ReturnType, string[] Parameters);

/// <summary>
/// <para>分支目标操作数（绝对字节偏移，展示时换算为相对偏移）。</para>
/// Branch target operand (absolute byte offset; converted to a relative offset for display).
/// </summary>
public sealed record BranchTargetOperand(int Offset);

/// <summary>
/// <para>伪指令 payload 操作数（packed/sparse-switch、fill-array-data）。</para>
/// Pseudo-instruction payload operand (packed/sparse-switch, fill-array-data).
/// </summary>
public sealed record PayloadOperand(string Kind, long FirstKey, int[] Values);

/// <summary>
/// <para>解码后的单条指令：操作码、助记符、操作数与大小。</para>
/// Decoded single instruction: opcode, mnemonic, operands and size.
/// </summary>
public sealed class Instruction
{
    /// <summary>指令在方法内的字节偏移（相对 insns 起始）。<para>Byte offset within the method (relative to the insns start).</para></summary>
    public int Offset { get; init; }

    /// <summary>操作码字节。<para>Opcode byte.</para></summary>
    public byte OpCode { get; init; }

    /// <summary>助记符（如 "const/4"）。<para>Mnemonic (e.g. "const/4").</para></summary>
    public string Mnemonic { get; init; } = string.Empty;

    /// <summary>解码后的操作数（int 寄存器/字面量、long 宽字面量、string 字符串、各引用记录、分支目标、payload）。</summary>
    /// Decoded operands (int registers/literals, long wide literals, string content, reference records, branch targets, payloads).
    public IReadOnlyList<object?> Operands { get; init; } = [];

    /// <summary>指令大小（16 位代码单元）。<para>Instruction size in 16-bit code units.</para></summary>
    public int Size { get; init; }
}