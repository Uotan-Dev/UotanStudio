using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>基本块种类：由块内最后一条指令决定。</para>
/// Basic block kinds, decided by the block's last instruction.
/// </summary>
public enum BlockKind
{
    /// <summary>顺序块：无分支，落入下一块。<para>Sequential block: no branch, falls through.</para></summary>
    Sequential,

    /// <summary>条件分支块：以 if-* 结尾。<para>Conditional branch block: ends with if-*.</para></summary>
    Conditional,

    /// <summary>无条件跳转块：以 goto 结尾。<para>Unconditional jump block: ends with goto.</para></summary>
    UnconditionalGoto,

    /// <summary>返回块：以 return-* 结尾。<para>Return block: ends with return-*.</para></summary>
    Return,

    /// <summary>抛出块：以 throw 结尾。<para>Throw block: ends with throw.</para></summary>
    Throw,

    /// <summary>开关块：以 packed/sparse-switch 结尾。<para>Switch block: ends with packed/sparse-switch.</para></summary>
    Switch
}

/// <summary>
/// <para>基本块：连续无分支的指令序列，携带控制流边（后继/前驱）与分支目标。</para>
/// Basic block: a contiguous branch-free instruction sequence, carrying control-flow edges
/// (successors/predecessors) and branch targets.
/// </summary>
public sealed class BasicBlock
{
    /// <summary>块编号（按构建顺序）。<para>Block id (build order).</para></summary>
    public int Id { get; set; }

    /// <summary>首条指令的字节偏移。<para>Byte offset of the first instruction.</para></summary>
    public int StartOffset { get; set; }

    /// <summary>末条指令结束后的字节偏移。<para>Byte offset after the last instruction.</para></summary>
    public int EndOffset { get; set; }

    /// <summary>块内指令。<para>Instructions in the block.</para></summary>
    public List<Instruction> Instructions { get; } = [];

    /// <summary>块种类。<para>Block kind.</para></summary>
    public BlockKind Kind { get; set; }

    /// <summary>后继块。<para>Successor blocks.</para></summary>
    public List<BasicBlock> Successors { get; } = [];

    /// <summary>前驱块。<para>Predecessor blocks.</para></summary>
    public List<BasicBlock> Predecessors { get; } = [];

    /// <summary>条件分支目标（条件为真）；仅 <see cref="BlockKind.Conditional"/> 使用。<para>Conditional branch target; used only by <see cref="BlockKind.Conditional"/>.</para></summary>
    public BasicBlock? BranchTarget { get; set; }

    /// <summary>条件不成立或顺序块落入的下一块。<para>Fallthrough block for condition-false or sequential flow.</para></summary>
    public BasicBlock? Fallthrough { get; set; }

    /// <summary>无条件跳转目标；仅 <see cref="BlockKind.UnconditionalGoto"/> 使用。<para>Unconditional jump target; used only by <see cref="BlockKind.UnconditionalGoto"/>.</para></summary>
    public BasicBlock? GotoTarget { get; set; }

    /// <summary>开关各 case 目标；仅 <see cref="BlockKind.Switch"/> 使用。<para>Switch case targets; used only by <see cref="BlockKind.Switch"/>.</para></summary>
    public List<BasicBlock> SwitchTargets { get; } = [];

    /// <summary>结构化遍历访问标记（由结构化器管理）。<para>Visited marker for structured traversal (managed by the structurer).</para></summary>
    public bool Visited { get; set; }
}