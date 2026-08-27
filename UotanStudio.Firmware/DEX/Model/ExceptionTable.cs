namespace UotanStudio.Firmware.Dex.Model;

/// <summary>
/// <para>异常处理条目（encoded_catch_handler 中的一项）：捕获类型索引 + 处理入口地址。</para>
/// Catch handler entry (one entry of encoded_catch_handler): caught type index + handler address.
/// </summary>
public sealed class CatchHandlerEntry
{
    /// <summary>捕获的异常类型索引（type_ids）。<para>Caught exception type index (type_ids).</para></summary>
    public int TypeIndex { get; set; }

    /// <summary>处理入口地址（16 位代码单元，相对 insns 起始）。<para>Handler entry address (16-bit code units, relative to the insns start).</para></summary>
    public int Address { get; set; }
}

/// <summary>
/// <para>异常处理器（encoded_catch_handler）：类型化条目列表 + 可选 catch-all。</para>
/// Catch handler (encoded_catch_handler): typed entries plus an optional catch-all.
/// </summary>
public sealed class CatchHandler
{
    /// <summary>类型化处理条目（文件顺序）。<para>Typed handler entries (file order).</para></summary>
    public List<CatchHandlerEntry> Entries { get; } = [];

    /// <summary>catch-all 处理入口地址（16 位代码单元）；无则为 -1。<para>Catch-all handler address (code units); -1 when absent.</para></summary>
    public int CatchAllAddress { get; set; } = -1;

    /// <summary>是否含 catch-all。<para>Whether a catch-all is present.</para></summary>
    public bool HasCatchAll => CatchAllAddress >= 0;
}

/// <summary>
/// <para>异常保护块（try_item）：受保护地址区间 + 指向处理器列表的偏移。</para>
/// Try block (try_item): protected address range + an offset into the handler list.
/// </summary>
public sealed class TryBlock
{
    /// <summary>受保护区间起始地址（16 位代码单元，相对 insns 起始）。<para>Protected range start (16-bit code units, relative to insns start).</para></summary>
    public int StartAddress { get; set; }

    /// <summary>受保护区间长度（代码单元）。<para>Protected range length (code units).</para></summary>
    public ushort InsnCount { get; set; }

    /// <summary>指向 <see cref="CodeItem.Handlers"/> 的索引（写入时换算为 handler_off）。<para>Index into <see cref="CodeItem.Handlers"/> (converted to handler_off on write).</para></summary>
    public int HandlerIndex { get; set; }

    /// <summary>受保护区间结束地址（不含）。<para>Exclusive end of the protected range.</para></summary>
    public int EndAddress => StartAddress + InsnCount;
}