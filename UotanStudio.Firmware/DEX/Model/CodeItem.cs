namespace UotanStudio.Firmware.Dex.Model;

/// <summary>
/// <para>代码项（code_item）：寄存器数、输入/输出数与指令流。</para>
/// Code item (code_item): register counts and the instruction stream.
/// </summary>
public sealed class CodeItem
{
    /// <summary>寄存器总数。<para>Total register count.</para></summary>
    public ushort RegistersSize { get; set; }

    /// <summary>输入参数寄存器数。<para>Input argument register count.</para></summary>
    public ushort InsSize { get; set; }

    /// <summary>外部调用寄存器数。<para>Outgoing argument register count.</para></summary>
    public ushort OutsSize { get; set; }

    /// <summary>原始指令流（16 位代码单元）。<para>Raw instruction stream (16-bit code units).</para></summary>
    public ushort[] RawInsns { get; set; } = [];

    /// <summary>
    /// <para>解码后的指令列表（含伪指令 payload），供反汇编与展示。</para>
    /// Decoded instruction list (including pseudo-instruction payloads), for disassembly and display.
    /// </summary>
    public List<Instruction> Instructions { get; } = [];

    /// <summary>
    /// <para>按 16 位代码单元计数的指令流字节长度。</para>
    /// Instruction stream byte length in 16-bit code units.
    /// </summary>
    public int InsnsSize => RawInsns.Length;

    /// <summary>
    /// <para>异常保护块（try_item 数组，按 start_addr 升序）。</para>
    /// Try blocks (try_item array, ascending by start_addr).
    /// </summary>
    public List<TryBlock> Tries { get; } = [];

    /// <summary>
    /// <para>异常处理器列表（encoded_catch_handler_list，文件顺序）；<see cref="TryBlock.HandlerIndex"/> 引用本表。</para>
    /// Catch handler list (encoded_catch_handler_list, file order); referenced by <see cref="TryBlock.HandlerIndex"/>.
    /// </summary>
    public List<CatchHandler> Handlers { get; } = [];

    /// <summary>
    /// <para>原始 debug_info_item 字节（含开头的 line_start 与参数名表）；无调试信息为 <c>null</c>。</para>
    /// Raw debug_info_item bytes (including the leading line_start and parameter name table);
    /// <c>null</c> when there is no debug information.
    /// </summary>
    public byte[]? DebugInfo { get; set; }
}