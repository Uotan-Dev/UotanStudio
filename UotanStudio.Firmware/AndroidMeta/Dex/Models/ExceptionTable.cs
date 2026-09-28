namespace UotanStudio.Firmware.AndroidMeta.Dex.Models;

/// <summary>
/// One typed <c>encoded_catch_handler</c> pair: the exception class and the handler address that catches it.
/// <para><c>encoded_catch_handler</c> 中的一组带类型配对：异常类与捕获它的处理地址。</para>
/// </summary>
public sealed class CatchHandlerEntry
{
    /// <summary>
    /// Exception class of this catch arm.
    /// <para>该捕获分支的异常类。</para></summary>
    /// <value>Zero-based <c>type_ids</c> index read as ULEB128. Values are not validated against the table:
    /// an out-of-range index renders as <c>"?"</c> in smali output and is written back as an unsigned LEB
    /// value. A negative value is not produced by parsing and is re-encoded as a large unsigned index.
    /// <para>以 ULEB128 读入的从零开始的 <c>type_ids</c> 索引。取值不与类型表比对：索引越界时在 smali 输出中渲染为
    /// <c>"?"</c>，回写时按无符号 LEB 写出。解析不会产生负值；负值会被重新编码为极大的无符号索引。</para></value>
    public int TypeIndex { get; set; }

    /// <summary>
    /// Start of the handler bytecode for this catch arm.
    /// <para>该捕获分支处理代码的起点。</para></summary>
    /// <value>Offset in 16-bit code units from the first instruction of the method body, so the byte offset is
    /// twice the value. The parser stores the ULEB128 field as read and does not check that it lands inside the
    /// instruction stream. <para>以 16 位代码单元计量、相对方法体首条指令的偏移，字节偏移为其两倍。
    /// 解析器按读入的 ULEB128 原样保存，不校验其是否落在指令流之内。</para></value>
    public int Address { get; set; }
}

/// <summary>
/// An <c>encoded_catch_handler</c>: the typed catch arms of one try range plus its optional catch-all arm.
/// <para><c>encoded_catch_handler</c>：单个 try 区间的各条带类型捕获分支，以及可选的捕获所有异常分支。</para>
/// </summary>
public sealed class CatchHandler
{
    /// <summary>
    /// Typed catch arms in stored order.
    /// <para>按存储顺序排列的带类型捕获分支。</para></summary>
    /// <value>Length equals the absolute value of the sleb128 <c>size</c> field; it is empty for a handler that
    /// only catches everything, which is the case the sign of that field encodes.
    /// <para>长度等于 sleb128 <c>size</c> 字段的绝对值；仅捕获所有异常时为空列表，这正是该字段符号所编码的含义。</para></value>
    public List<CatchHandlerEntry> Entries { get; } = [];

    /// <summary>
    /// Handler address of the catch-all arm.
    /// <para>捕获所有异常分支的处理地址。</para></summary>
    /// <value>Offset in 16-bit code units from the first instruction, present only when the <c>size</c> field was
    /// zero or negative; <c>-1</c> means the handler has no catch-all arm, and that is the value a freshly created
    /// instance keeps. <para>相对首条指令、以 16 位代码单元计的偏移，仅当 <c>size</c> 字段为零或负值时存在；
    /// <c>-1</c> 表示该处理程序没有捕获所有异常的分支，新建实例保持的就是该值。</para></value>
    public int CatchAllAddress { get; set; } = -1;

    /// <summary>
    /// Whether this handler also catches exceptions of any other type.
    /// <para>该处理程序是否还捕获其他任意类型的异常。</para></summary>
    /// <value><see langword="true"/> when <see cref="CatchAllAddress"/> is zero or greater, which the writer uses
    /// to decide whether to emit the trailing address and the smali printer uses to emit <c>.catchall</c>.
    /// <para>当 <see cref="CatchAllAddress"/> 大于或等于零时为 <see langword="true"/>；写出端据此决定是否输出末尾的地址，
    /// smali 打印端据此输出 <c>.catchall</c>。</para></value>
    public bool HasCatchAll => CatchAllAddress >= 0;
}

/// <summary>
/// A <c>try_item</c>: the instruction range the exception table covers and the handler set that applies to it.
/// <para><c>try_item</c>：异常表所覆盖的指令区间，以及作用于该区间的处理程序集合。</para>
/// </summary>
public sealed class TryBlock
{
    /// <summary>
    /// First code unit of the protected range.
    /// <para>受保护区间的首个代码单元。</para></summary>
    /// <value>Unit index stored as the raw little-endian <c>start_addr</c> field; byte offsets are twice the
    /// value and the field is not checked against the length of the instruction stream.
    /// <para>按原始小端序 <c>start_addr</c> 字段保存的单元下标；字节偏移为其两倍，该字段不与指令流长度比对。</para></value>
    public int StartAddress { get; set; }

    /// <summary>
    /// Number of protected code units.
    /// <para>受保护代码单元的数量。</para></summary>
    /// <value>Width in 16-bit code units from the raw <c>insn_count</c> field; <c>0</c> denotes an empty range and
    /// the parser does not require the range to end inside the method body.
    /// <para>取自原始 <c>insn_count</c> 字段、以 16 位代码单元计的宽度；<c>0</c> 表示空区间，解析器不要求区间终点落在方法体内。</para></value>
    public ushort InsnCount { get; set; }

    /// <summary>
    /// Position of this range's handler set in <see cref="CodeItem.Handlers"/>.
    /// <para>该区间对应处理程序集合在 <see cref="CodeItem.Handlers"/> 中的位置。</para></summary>
    /// <value>Resolved from the raw byte offset into the handler list, so it is a positional index rather than a
    /// file offset; <c>-1</c> when the stored offset matches no parsed handler, which makes the range print without
    /// <c>.catch</c> lines and lets a writer emit offset <c>0</c> for it.
    /// <para>由处理程序列表中的原始字节偏移解析得到，因此是列表下标而非文件偏移；存储偏移找不到已解析条目时为 <c>-1</c>，
    /// 此时该区间不会输出 <c>.catch</c> 行，写出端会为其写出偏移 <c>0</c>。</para></value>
    public int HandlerIndex { get; set; }

    /// <summary>
    /// One past the last protected code unit of the range.
    /// <para>受保护区间最后一个代码单元之后的位置。</para></summary>
    /// <value>Exclusive end in 16-bit code units, computed as <see cref="StartAddress"/> plus
    /// <see cref="InsnCount"/>; the sum is unchecked, and consumers turn it into a byte offset by doubling it.
    /// <para>以 16 位代码单元计的不包含终点，等于 <see cref="StartAddress"/> 加上 <see cref="InsnCount"/>；
    /// 相加时不做溢出检查，使用方通过乘以二得到其字节偏移。</para></value>
    public int EndAddress => StartAddress + InsnCount;
}