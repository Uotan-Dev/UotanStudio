using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

/// <summary>
/// Terminator category of a basic block, which decides how the structurer renders it.
/// <para>基本块的终止指令类别，决定结构化器如何渲染该块。</para>
/// </summary>
public enum BlockKind
{
    /// <summary>Falls through to the next block. <para>顺序落入下一个块。</para></summary>
    Sequential,

    /// <summary>Ends with an <c>if-*</c> test branching to one target with a fallthrough path.
    /// <para>以 <c>if-*</c> 判断结束，分支到一个目标并保留直落路径。</para></summary>
    Conditional,

    /// <summary>Ends with a <c>goto</c> that has a single target.
    /// <para>以只有单一目标的 <c>goto</c> 结束。</para></summary>
    UnconditionalGoto,

    /// <summary>Ends with a <c>return-*</c> and has no successor.
    /// <para>以 <c>return-*</c> 结束，没有后继块。</para></summary>
    Return,

    /// <summary>Ends with <c>throw</c> and has no successor.
    /// <para>以 <c>throw</c> 结束，没有后继块。</para></summary>
    Throw,

    /// <summary>Ends with a <c>packed-switch</c> or <c>sparse-switch</c> over several case targets.
    /// <para>以覆盖多个分支目标的 <c>packed-switch</c> 或 <c>sparse-switch</c> 结束。</para></summary>
    Switch
}

/// <summary>
/// One basic block of a decompiled DEX method body.
/// <para>反编译 DEX 方法体中的一个基本块。</para>
/// </summary>
public sealed class BasicBlock
{
    /// <summary>Sequential identifier assigned during graph construction.
    /// <para>建图期间分配的顺序标识。</para></summary>
    /// <value>Zero-based ordinal equal to the block's position in <see cref="Cfg.Blocks"/>; assigned
    /// by <see cref="CfgBuilder"/>. <para>从零开始的序号，等于该块在 <see cref="Cfg.Blocks"/> 中的
    /// 位置；由 <see cref="CfgBuilder"/> 分配。</para></value>
    public int Id { get; set; }

    /// <summary>Offset of the block's first instruction.
    /// <para>块首条指令的偏移。</para></summary>
    /// <value>Byte offset into the code item, matching the key in
    /// <see cref="Cfg.ByStartOffset"/>. <para>代码项内的字节偏移，与
    /// <see cref="Cfg.ByStartOffset"/> 中的键一致。</para></value>
    public int StartOffset { get; set; }

    /// <summary>Offset just past the block's last instruction.
    /// <para>紧接块末条指令之后的偏移。</para></summary>
    /// <value>Byte offset computed as the last instruction offset plus twice its size in code units.
    /// <para>字节偏移，按末条指令偏移加上其代码单元大小的两倍计算得出。</para></value>
    public int EndOffset { get; set; }

    /// <summary>Instructions contained in the block.
    /// <para>块内包含的指令。</para></summary>
    /// <value>Append-only list in ascending offset order; the last element is the terminator that
    /// decides <see cref="Kind"/>. <para>按偏移升序的只增列表；末元素是决定
    /// <see cref="Kind"/> 的终止指令。</para></value>
    public List<Instruction> Instructions { get; } = [];

    /// <summary>Terminator category of the block.
    /// <para>块的终止指令类别。</para></summary>
    /// <value>Assigned by <see cref="CfgBuilder"/> from the last instruction mnemonic; a block whose
    /// terminator is unrecognised is <see cref="BlockKind.Sequential"/>.
    /// <para>由 <see cref="CfgBuilder"/> 依据末条指令助记符赋值；终止指令无法识别的块归类为
    /// <see cref="BlockKind.Sequential"/>。</para></value>
    public BlockKind Kind { get; set; }

    /// <summary>Blocks this block can transfer control to.
    /// <para>本块可将控制转移到的块。</para></summary>
    /// <value>Append-only list without duplicates, covering the branch, goto, case and fallthrough
    /// edges as recorded by <see cref="CfgBuilder"/>. <para>不含重复项的只增列表，涵盖
    /// <see cref="CfgBuilder"/> 记录的分支、goto、switch 分支与直落边。</para></value>
    public List<BasicBlock> Successors { get; } = [];

    /// <summary>Blocks that can transfer control into this block.
    /// <para>可将控制转入本块的块。</para></summary>
    /// <value>Append-only list maintained as the mirror of the referencing blocks'
    /// <see cref="Successors"/>; a back edge from a later block can make the graph entry block have
    /// predecessors too. <para>只增列表，与各引用块的 <see cref="Successors"/> 互为镜像；后面块的
    /// 回边也会让图入口块拥有前驱。</para></value>
    public List<BasicBlock> Predecessors { get; } = [];

    /// <summary>Target taken when the <c>if-*</c> test holds.
    /// <para><c>if-*</c> 判断成立时跳转的目标块。</para></summary>
    /// <value>Set only for <see cref="BlockKind.Conditional"/> blocks, and <see langword="null"/>
    /// when the branch offset does not name a block start. <para>仅对
    /// <see cref="BlockKind.Conditional"/> 块赋值；分支偏移不指向某块起点时为
    /// <see langword="null"/>。</para></value>
    public BasicBlock? BranchTarget { get; set; }

    /// <summary>Block reached when control falls past the terminator.
    /// <para>控制越过终止指令后到达的块。</para></summary>
    /// <value>The next block in <see cref="Cfg.Blocks"/> for conditional, switch and sequential
    /// blocks; <see langword="null"/> for the last block, for <see cref="BlockKind.Return"/>,
    /// <see cref="BlockKind.Throw"/> and <see cref="BlockKind.UnconditionalGoto"/>.
    /// <para>条件、switch 与顺序块取 <see cref="Cfg.Blocks"/> 中的下一个块；最后一个块以及
    /// <see cref="BlockKind.Return"/>、<see cref="BlockKind.Throw"/>、
    /// <see cref="BlockKind.UnconditionalGoto"/> 为 <see langword="null"/>。</para></value>
    public BasicBlock? Fallthrough { get; set; }

    /// <summary>Single target of a <c>goto</c> terminator.
    /// <para><c>goto</c> 终止指令的唯一目标块。</para></summary>
    /// <value>Set only for <see cref="BlockKind.UnconditionalGoto"/> blocks, and <see langword="null"/>
    /// when the target offset does not name a block start. <para>仅对
    /// <see cref="BlockKind.UnconditionalGoto"/> 块赋值；目标偏移不指向某块起点时为
    /// <see langword="null"/>。</para></value>
    public BasicBlock? GotoTarget { get; set; }

    /// <summary>Case targets of a switch terminator.
    /// <para>switch 终止指令的分支目标。</para></summary>
    /// <value>Append-only list in payload order for <see cref="BlockKind.Switch"/> blocks; entries
    /// are only added when the case offset names a block start, so it can be shorter than the
    /// payload. Empty for other kinds. <para>对 <see cref="BlockKind.Switch"/> 块按载荷顺序追加；
    /// 只有分支偏移指向某块起点时才会加入条目，因此长度可能小于载荷条目数。其他类别为空。</para></value>
    public List<BasicBlock> SwitchTargets { get; } = [];

    /// <summary>Traversal marker used while structuring.
    /// <para>结构化过程中使用的遍历标记。</para></summary>
    /// <value>Cleared for every block at the start of
    /// <see cref="ControlFlowStructuring.Structure(LinearAnalysis, Cfg, DecompileOptions)"/> and set
    /// when the block is emitted; it carries no meaning outside that traversal.
    /// <para>在 <see cref="ControlFlowStructuring.Structure(LinearAnalysis, Cfg, DecompileOptions)"/>
    /// 开始时为每个块清除，在块被输出时置位；离开该遍历过程没有含义。</para></value>
    public bool Visited { get; set; }
}