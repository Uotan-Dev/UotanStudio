using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Model;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

public enum BlockKind
{
    Sequential,

    Conditional,

    UnconditionalGoto,

    Return,

    Throw,

    Switch
}

public sealed class BasicBlock
{
    public int Id { get; set; }

    public int StartOffset { get; set; }

    public int EndOffset { get; set; }

    public List<Instruction> Instructions { get; } = [];

    public BlockKind Kind { get; set; }

    public List<BasicBlock> Successors { get; } = [];

    public List<BasicBlock> Predecessors { get; } = [];

    public BasicBlock? BranchTarget { get; set; }

    public BasicBlock? Fallthrough { get; set; }

    public BasicBlock? GotoTarget { get; set; }

    public List<BasicBlock> SwitchTargets { get; } = [];

    public bool Visited { get; set; }
}
