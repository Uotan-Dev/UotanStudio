namespace UotanStudio.Firmware.AndroidMeta.Dex.Models;

/// <summary>
/// A DEX <c>code_item</c>: register counts, the raw Dalvik bytecode and the parsed exception table plus debug bytes for one method.
/// <para>DEX 的 <c>code_item</c>：单个方法的寄存器数量、原始 Dalvik 字节码，以及解析出的异常表与调试信息字节。</para>
/// </summary>
public sealed class CodeItem
{
    /// <summary>
    /// Register count declared by <c>registers_size</c>, covering parameters and locals.
    /// <para><c>registers_size</c> 声明的寄存器总数，包含参数与局部变量。</para>
    /// </summary>
    /// <value>A raw 16-bit value from the header of the code item, with no upper bound enforced here.
    /// <para>来自 code item 头部的原始 16 位值，本类型不做额外上限约束。</para></value>
    public ushort RegistersSize { get; set; }

    /// <summary>
    /// Number of parameter registers at the end of the register range, from <c>ins_size</c>.
    /// <para>来自 <c>ins_size</c> 的参数寄存器数量，位于寄存器区间末尾。</para>
    /// </summary>
    /// <value>Register count including the <c>this</c> reference for instance methods; <c>0</c> for a static method without parameters.
    /// <para>实例方法计入 <c>this</c> 引用；无参数的静态方法为 <c>0</c>。</para></value>
    public ushort InsSize { get; set; }

    /// <summary>
    /// Number of registers used to pass arguments to invoked methods, from <c>outs_size</c>.
    /// <para>来自 <c>outs_size</c> 的寄存器数量，用于向被调用方法传递实参。</para>
    /// </summary>
    /// <value>The highest argument-slot width observed in the bytecode; <c>0</c> when the method makes no calls.
    /// <para>等于字节码中观察到的最大实参槽位宽度；方法无调用时为 <c>0</c>。</para></value>
    public ushort OutsSize { get; set; }

    /// <summary>
    /// The bytecode as stored: little-endian 16-bit code units in original order.
    /// <para>按存储原样保留的字节码：小端字节序的 16 位代码单元，顺序不变。</para>
    /// </summary>
    /// <value>The authority for disassembly, decompilation and rewriting; empty for a method with no code.
    /// <para>反汇编、反编译与回写的权威数据来源；无代码方法为空数组。</para></value>
    public ushort[] RawInsns { get; set; } = [];

    /// <summary>
    /// An optional decoded view of the bytecode supplied by tooling instead of the raw units.
    /// <para>由工具层提供的可选解码视图，用于代替原始代码单元。</para>
    /// </summary>
    /// <value>When non-empty, the disassembler and decompiler use it as-is; while empty they decode <see cref="RawInsns"/> on demand. It is never serialized to a DEX file.
    /// <para>非空时反汇编器与反编译器直接使用；为空时它们按需解码 <see cref="RawInsns"/>。该列表不会序列化进 DEX 文件。</para></value>
    public List<Instruction> Instructions { get; } = [];

    /// <summary>
    /// Length of the bytecode measured in 16-bit code units.
    /// <para>以 16 位代码单元计量的字节码长度。</para>
    /// </summary>
    /// <value>Byte length is twice this count, matching the <c>insns_size</c> field written for the item.
    /// <para>字节长度为该计数的两倍，与写出的 <c>insns_size</c> 字段含义一致。</para></value>
    public int InsnsSize => RawInsns.Length;

    /// <summary>
    /// The <c>try_item</c> ranges of the exception table.
    /// <para>异常表中的 <c>try_item</c> 区间。</para>
    /// </summary>
    /// <value>Empty when the method has no <c>tries_size</c>; each range points into <see cref="Handlers"/> by index.
    /// <para>方法没有 <c>tries_size</c> 时为空；每个区间按下标指向 <see cref="Handlers"/>。</para></value>
    public List<TryBlock> Tries { get; } = [];

    /// <summary>
    /// The <c>encoded_catch_handler_list</c> entries referenced by <see cref="Tries"/>.
    /// <para>被 <see cref="Tries"/> 引用的 <c>encoded_catch_handler_list</c> 条目。</para>
    /// </summary>
    /// <value>Positional index equals <c>TryBlock.HandlerIndex</c>; a try keeps <c>-1</c> when its raw handler offset matches no entry.
    /// <para>列表下标即 <c>TryBlock.HandlerIndex</c>；若 try 的原始 handler 偏移找不到对应条目，其值为 <c>-1</c>。</para></value>
    public List<CatchHandler> Handlers { get; } = [];

    /// <summary>
    /// The verbatim <c>debug_info_item</c> bytes, walked to its <c>END_SEQUENCE</c> opcode during parsing.
    /// <para>原样保留的 <c>debug_info_item</c> 字节，解析时按状态机扫描到 <c>END_SEQUENCE</c> 操作码。</para>
    /// </summary>
    /// <value><see langword="null"/> when the method declares no debug information; otherwise written back unchanged.
    /// <para>方法未声明调试信息时为 <see langword="null"/>；否则回写时保持字节不变。</para></value>
    public byte[]? DebugInfo { get; set; }
}