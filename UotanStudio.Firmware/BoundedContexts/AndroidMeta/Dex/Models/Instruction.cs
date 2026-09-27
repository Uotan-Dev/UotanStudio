namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

/// <summary>
/// An immediate operand decoded from one instruction format and widened to <see cref="long"/>.
/// <para>从某个指令格式解码并加宽为 <see cref="long"/> 的立即数操作数。</para>
/// </summary>
/// <param name="Value">Literal value after the sign handling the decoder applies per format; it is negative
/// whenever the format sign-extends or the widened pattern has its high bit set, and a wide literal keeps all
/// 64 bits. <para>经解码器按格式施加的符号处理后的字面值；只要格式带符号扩展，或加宽后的位模式最高位为 <c>1</c>，其值即为负；
/// 宽立即数保留全部 64 位。</para></param>
public sealed record LiteralOperand(long Value);

/// <summary>
/// A reference to a <c>type_ids</c> entry carried as the descriptor text stored in the table.
/// <para>指向 <c>type_ids</c> 表项的引用，保存表中记录的原样描述符文本。</para>
/// </summary>
/// <param name="Descriptor">Descriptor exactly as recorded in <c>type_ids</c>, for example
/// <c>Ljava/lang/String;</c>; <c>"?"</c> when the reference index was out of range.
/// <para>与 <c>type_ids</c> 中完全一致的描述符，例如 <c>Ljava/lang/String;</c>；引用索引越界时为 <c>"?"</c>。</para></param>
public sealed record TypeRefOperand(string Descriptor);

/// <summary>
/// A resolved <c>field_ids</c> reference used by the <c>iget</c>/<c>iput</c>/<c>sget</c>/<c>sput</c> families.
/// <para>已解析的 <c>field_ids</c> 引用，用于 <c>iget</c>/<c>iput</c>/<c>sget</c>/<c>sput</c> 系列指令。</para>
/// </summary>
/// <param name="OwnerClass">Owning class in readable descriptor form, for example <c>com.x.Foo</c>;
/// <c>"?"</c> when the class index does not resolve.
/// <para>所属类的可读描述符形式，例如 <c>com.x.Foo</c>；类索引无法解析时为 <c>"?"</c>。</para></param>
/// <param name="Name">Field name decoded from <c>string_ids</c>; empty when the name index is out of range.
/// <para>由 <c>string_ids</c> 解码的字段名；名称索引越界时为空字符串。</para></param>
/// <param name="Type">Field type in readable descriptor form, for example <c>int</c> or <c>com.x.Foo[]</c>.
/// <para>字段类型的可读描述符形式，例如 <c>int</c> 或 <c>com.x.Foo[]</c>。</para></param>
public sealed record FieldRefOperand(string OwnerClass, string Name, string Type);

/// <summary>
/// A resolved <c>method_ids</c> reference used by the invoke instructions.
/// <para>已解析的 <c>method_ids</c> 引用，用于 invoke 系列指令。</para>
/// </summary>
/// <param name="OwnerClass">Declaring class in readable descriptor form; <c>"?"</c> when the class index
/// does not resolve. <para>声明类的可读描述符形式；类索引无法解析时为 <c>"?"</c>。</para></param>
/// <param name="Name">Method name decoded from <c>string_ids</c>; empty when the name index is out of range.
/// <para>由 <c>string_ids</c> 解码的方法名；名称索引越界时为空字符串。</para></param>
/// <param name="ReturnType">Return type in readable descriptor form; <c>"?"</c> when the proto index of the
/// method does not resolve. <para>返回类型的可读描述符形式；方法的 proto 索引无法解析时为 <c>"?"</c>。</para></param>
/// <param name="Parameters">Parameter type descriptors in declaration order; empty when the method takes no
/// parameters or its proto index does not resolve.
/// <para>按声明顺序排列的参数类型描述符；方法无参数或 proto 索引无法解析时为空数组。</para></param>
public sealed record MethodRefOperand(string OwnerClass, string Name, string ReturnType, string[] Parameters);

/// <summary>Describes a method prototype operand using DEX type descriptors.
/// <para>使用 DEX 类型描述符表示方法原型操作数。</para></summary>
/// <param name="ReturnType">Return type descriptor. <para>返回类型描述符。</para></param>
/// <param name="Parameters">Parameter descriptors in declaration order. <para>按声明顺序排列的参数描述符。</para></param>
public sealed record ProtoRefOperand(string ReturnType, string[] Parameters);

/// <summary>Identifies a method-handle table operand.
/// <para>标识方法句柄表操作数。</para></summary>
/// <param name="Index">Zero-based index in the owning DEX method-handle table.
/// <para>所属 DEX 方法句柄表中的零基索引。</para></param>
public sealed record MethodHandleRefOperand(int Index);

/// <summary>Identifies a call-site table operand used by invoke-custom instructions.
/// <para>标识 invoke-custom 指令使用的调用点表操作数。</para></summary>
/// <param name="Index">Zero-based index in the owning DEX call-site table.
/// <para>所属 DEX 调用点表中的零基索引。</para></param>
public sealed record CallSiteRefOperand(int Index);

/// <summary>
/// The destination of a branch, expressed as an offset inside the same method body.
/// <para>跳转目标，以同一方法体内的偏移量表示。</para>
/// </summary>
/// <param name="Offset">Absolute byte offset computed as <c>(unit index + relative units) * 2</c> from the
/// start of the instruction stream. The decoder keeps the arithmetic result without checking that it lands on
/// a decoded instruction or stays inside the stream.
/// <para>由 <c>(代码单元下标 + 相对单元数) * 2</c> 得到的、相对指令流起点的绝对字节偏移。解码器保留运算结果，
/// 不校验其是否落在某条已解码指令上，也不校验是否仍在流内。</para></param>
public sealed record BranchTargetOperand(int Offset);

/// <summary>
/// A switch or array-data payload decoded at the branch target of a wide literal reference instruction.
/// <para>在宽立即数引用指令的跳转目标处解码出的 switch 或数组数据有效负载。</para>
/// </summary>
/// <param name="Kind">Payload discriminator: <c>packed-switch</c>, <c>sparse-switch</c>,
/// <c>fill-array-data</c>, or <c>targets</c> for the companion offset list of a sparse switch. The smali
/// writer combines these operands into complete directive blocks.
/// <para>负载类型标识：<c>packed-switch</c>、<c>sparse-switch</c>、<c>fill-array-data</c>，
/// 或 <c>targets</c>（稀疏 switch 的配套偏移列表）。smali 输出将这些操作数组合成完整指令块。</para></param>
/// <param name="FirstKey">First switch key for <c>packed-switch</c>; element width in bytes for
/// <c>fill-array-data</c>; <c>0</c> for <c>sparse-switch</c> and <c>targets</c>.
/// <para><c>packed-switch</c> 的首个 switch 键值；<c>fill-array-data</c> 的元素字节宽度；
/// <c>sparse-switch</c> 与 <c>targets</c> 为 <c>0</c>。</para></param>
/// <param name="Values">Relative branch offsets in code units for <c>packed-switch</c> and <c>targets</c>;
/// sparse-switch match keys for <c>sparse-switch</c>; the raw 16-bit data units backing the array for
/// <c>fill-array-data</c>. The decoder validates the complete payload against the instruction stream before allocation.
/// <para><c>packed-switch</c> 与 <c>targets</c> 为以代码单元计的相对跳转偏移；<c>sparse-switch</c>
/// 为稀疏 switch 的匹配键；<c>fill-array-data</c> 为数组对应的原始 16 位数据单元。解码器在分配之前校验完整负载不越过指令流。</para></param>
public sealed record PayloadOperand(string Kind, long FirstKey, int[] Values)
{
    /// <summary>Gets the array element count independently of code-unit padding.
    /// <para>获取独立于代码单元填充的数组元素数量。</para></summary>
    /// <value>The array payload's unsigned count, or null for non-array payloads.
    /// <para>数组负载中的无符号数量，非数组负载为 null。</para></value>
    public uint? ElementCount { get; init; }
}

/// <summary>
/// One decoded Dalvik instruction, including the pseudo entries the decoder produces for payloads and for
/// malformed opcodes.
/// <para>一条已解码的 Dalvik 指令，同时包含解码器为有效负载和异常操作码生成的伪条目。</para>
/// </summary>
public sealed class Instruction
{
    /// <summary>
    /// Position of the instruction inside its method body.
    /// <para>该指令在其方法体内的位置。</para></summary>
    /// <value>Byte offset from the first code unit, equal to the code-unit index multiplied by <c>2</c>. Branch
    /// targets are stored in the same unit and can be compared directly; try and handler addresses are code units
    /// that consumers double first. <para>相对首个代码单元的字节偏移，等于代码单元下标乘以 <c>2</c>。跳转目标以同一单位保存，
    /// 可直接比对；try 与处理程序地址以代码单元计，使用方需先乘以二换算。</para></value>
    public int Offset { get; init; }

    /// <summary>
    /// Opcode byte of the instruction.
    /// <para>指令的操作码字节。</para></summary>
    /// <value>The low byte of the first little-endian code unit; <c>0</c> for a payload pseudo entry, which is identified by
    /// <see cref="Mnemonic"/> instead. <para>首个小端代码单元的低字节；有效负载伪条目为 <c>0</c>，其身份由 <see cref="Mnemonic"/> 标识。</para></value>
    public byte OpCode { get; init; }

    /// <summary>
    /// Name the disassembly and decompilation print for this instruction.
    /// <para>反汇编与反编译为该指令输出的名称。</para></summary>
    /// <value>The opcode table mnemonic; <c>truncated-</c> plus the mnemonic when the stream ends before the
    /// format is complete; <c>unknown-0xNN</c> for an opcode with no table entry; a <c>-payload</c> suffixed
    /// name for a payload pseudo entry. Empty when the caller built the entry without one.
    /// <para>取自操作码表的助记符；流尾不足以读完该格式时为 <c>truncated-</c> 加上助记符；无表项的操作码为
    /// <c>unknown-0xNN</c>；有效负载伪条目为带 <c>-payload</c> 后缀的名称；调用方未提供时为空字符串。</para></value>
    public string Mnemonic { get; init; } = string.Empty;

    /// <summary>
    /// Operand list of the instruction in the order the smali writer prints it.
    /// <para>按 smali 输出顺序排列的操作数列表。</para></summary>
    /// <value>Register operands are boxed <see cref="int"/> values; the vector-register formats put their register
    /// numbers in a list in the first slot, which the smali writer reads back as an
    /// <see cref="IReadOnlyList{T}"/> of <see cref="int"/> and renders as <c>{v0, v1}</c>; constants, references and
    /// targets use the operand records of this namespace. An unresolvable field, method or proto reference stays a
    /// bare index, and a <see langword="null"/> slot is printed as <c>&lt;null&gt;</c>. Empty for <c>nop</c>-style
    /// formats and for truncated entries.
    /// <para>寄存器操作数是装箱的 <see cref="int"/>；向量寄存器格式把寄存器编号列表放在首位，smali 输出端把它读回为
    /// <see cref="int"/> 的 <see cref="IReadOnlyList{T}"/> 并渲染为 <c>{v0, v1}</c>；常量、引用与跳转目标使用本命名空间的
    /// 记录操作数。无法解析的字段、方法或 proto 引用保留为裸索引，<see langword="null"/> 槽位打印为 <c>&lt;null&gt;</c>。
    /// <c>nop</c> 类格式与被截断的条目为空列表。</para></value>
    public IReadOnlyList<object?> Operands { get; init; } = [];

    /// <summary>
    /// Width of the decoded entry.
    /// <para>已解码条目的宽度。</para></summary>
    /// <value>Length in 16-bit code units, so the byte width is twice the value; a payload entry spans its
    /// header plus data, and a truncated entry reports only the units still present in the stream.
    /// <para>以 16 位代码单元计的长度，字节宽度为其两倍；有效负载条目跨越其头部与数据，
    /// 被截断的条目仅报告流中仍存在单元数。</para></value>
    public int Size { get; init; }
}