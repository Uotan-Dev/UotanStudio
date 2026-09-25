namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

/// <summary>
/// Selects how much raw bytecode detail the decompiler puts into its output.
/// <para>决定反编译器在输出中保留多少原始字节码细节。</para>
/// </summary>
public enum DecompileOutputMode
{
    /// <summary>Statements only, with no per-instruction comments.
    /// <para>只输出语句，不带逐指令注释。</para></summary>
    PseudoJava,

    /// <summary>Statements plus Smali-style comments for each decoded instruction and an offset
    /// comment line above statements that carry one. <para>语句之外还为每条解码指令输出 Smali
    /// 风格注释，并在带偏移的语句上方输出偏移注释行。</para></summary>
    BytecodeGuide
}

/// <summary>
/// Output settings shared by the DEX decompilation stages.
/// <para>DEX 反编译各阶段共用的输出设置。</para>
/// </summary>
public sealed class DecompileOptions
{
    /// <summary>Detail level of the rendered output.
    /// <para>渲染输出的详细程度。</para></summary>
    /// <value>Defaults to <see cref="DecompileOutputMode.PseudoJava"/>;
    /// <see cref="DecompileOutputMode.BytecodeGuide"/> additionally emits instruction comments and
    /// offset comment lines. <para>默认为 <see cref="DecompileOutputMode.PseudoJava"/>；
    /// <see cref="DecompileOutputMode.BytecodeGuide"/> 会额外输出指令注释与偏移注释行。</para></value>
    public DecompileOutputMode Mode { get; init; } = DecompileOutputMode.PseudoJava;

    /// <summary>Caller-facing request for bytecode offset annotations.
    /// <para>面向调用方的字节码偏移标注开关。</para></summary>
    /// <value><c>false</c> unless the caller sets it. Offset comment lines in the rendered output are
    /// produced by <see cref="Mode"/> set to <see cref="DecompileOutputMode.BytecodeGuide"/>; no
    /// stage of the current pipeline reads this flag.
    /// <para>调用方未设置时为 <c>false</c>。渲染输出中的偏移注释行由 <see cref="Mode"/> 取
    /// <see cref="DecompileOutputMode.BytecodeGuide"/> 产生，当前流水线没有任何阶段读取该标志。</para></value>
    public bool ShowBytecodeOffsets { get; init; }

    /// <summary>Shared option set used when a caller passes no options.
    /// <para>调用方未提供选项时使用的共享选项集。</para></summary>
    /// <value>A single immutable instance with <see cref="Mode"/> equal to
    /// <see cref="DecompileOutputMode.PseudoJava"/>. <para>单一不可变实例，其
    /// <see cref="Mode"/> 为 <see cref="DecompileOutputMode.PseudoJava"/>。</para></value>
    public static DecompileOptions Default { get; } = new();
}