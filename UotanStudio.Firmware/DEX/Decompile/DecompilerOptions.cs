namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>反编译输出模式。</para>
/// Decompiler output modes.
/// </summary>
public enum DecompileOutputMode
{
    /// <summary>伪 Java：结构化输出，无法恢复的指令以注释保留。<para>Pseudo-Java: structured output; instructions that cannot be recovered are kept as comments.</para></summary>
    PseudoJava,

    /// <summary>字节码指导：伪 Java 骨架 + 每条语句对应的字节码偏移与原始助记符注释。<para>Bytecode guide: pseudo-Java skeleton plus bytecode offset/mnemonic comments for each statement.</para></summary>
    BytecodeGuide
}

/// <summary>
/// <para>轻量化反编译选项。</para>
/// Lightweight decompiler options.
/// </summary>
public sealed class DecompileOptions
{
    /// <summary>
    /// <para>输出模式；默认 <see cref="DecompileOutputMode.PseudoJava"/>。</para>
    /// Output mode; defaults to <see cref="DecompileOutputMode.PseudoJava"/>.
    /// </summary>
    public DecompileOutputMode Mode { get; init; } = DecompileOutputMode.PseudoJava;

    /// <summary>
    /// <para>是否在语句行首标注源字节码偏移（BytecodeGuide 模式自动启用）。</para>
    /// Whether to prefix statements with source bytecode offsets (auto-enabled in BytecodeGuide mode).
    /// </summary>
    public bool ShowBytecodeOffsets { get; init; }

    /// <summary>
    /// <para>获取默认选项。</para>
    /// Gets the default options.
    /// </summary>
    public static DecompileOptions Default { get; } = new();
}