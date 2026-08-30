using System.Collections.Generic;
using UotanStudio.Firmware.Dex.Instructions;
using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>控制流图：基本块列表 + 入口块 + 按起始偏移索引。</para>
/// Control-flow graph: the basic block list, the entry block and a start-offset index.
/// </summary>
public sealed class Cfg
{
    /// <summary>全部基本块（构建顺序）。<para>All basic blocks (build order).</para></summary>
    public List<BasicBlock> Blocks { get; } = [];

    /// <summary>入口块；空方法为 <c>null</c>。<para>Entry block; <c>null</c> for empty methods.</para></summary>
    public BasicBlock? Entry { get; set; }

    /// <summary>按起始字节偏移索引块。<para>Index of blocks by start byte offset.</para></summary>
    public Dictionary<int, BasicBlock> ByStartOffset { get; } = [];

    /// <summary>是否存在指令（非空 CFG）。<para>Whether the CFG has instructions.</para></summary>
    public bool HasInstructions => Blocks.Count > 0;
}

/// <summary>
/// <para>控制流图构建器：把方法指令流切分为基本块并连接控制流边，
/// 分支目标与 switch case 目标均解析为块引用。</para>
/// Control-flow graph builder: splits a method's instruction stream into basic blocks and wires
/// control-flow edges, resolving branch targets and switch case targets into block references.
/// </summary>
public static class CfgBuilder
{
    /// <summary>
    /// <para>从代码项构建控制流图（未解码的指令按需解码）。</para>
    /// Builds a control-flow graph from a code item (decoding instructions on demand).
    /// </summary>
    /// <param name="code">代码项。<para>The code item.</para></param>
    /// <param name="dex">所属 DEX 文件（用于按需解码）。<para>The owning DEX file (for on-demand decoding).</para></param>
    /// <returns>控制流图；空方法返回无块图。<para>The CFG; empty methods yield a block-less graph.</para></returns>
    public static Cfg Build(CodeItem code, DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(dex);

        var instructions = code.Instructions.Count > 0
            ? code.Instructions
            : InstructionDecoder.Decode(code.RawInsns, dex);
        var cfg = new Cfg();
        if (instructions.Count == 0) return cfg;

        // 第一遍：收集 leader 偏移——偏移 0、分支目标、终止指令后的下一条、switch case 目标。
        // Pass 1: collect leader offsets — offset 0, branch targets, the instruction after a
        // terminator, and switch case targets.
        var leaders = new SortedSet<int> { 0 };
        var offsetToIndex = new Dictionary<int, int>(instructions.Count);
        for (var i = 0; i < instructions.Count; i++)
        {
            offsetToIndex[instructions[i].Offset] = i;
        }

        for (var i = 0; i < instructions.Count; i++)
        {
            var insn = instructions[i];
            var isTerminator = false;
            foreach (var operand in insn.Operands)
            {
                switch (operand)
                {
                    case BranchTargetOperand target:
                        leaders.Add(target.Offset);
                        break;
                    case PayloadOperand payload when insn.Mnemonic is "packed-switch" or "sparse-switch":
                        // 31t 的 payload 目标由 BranchTargetOperand 收集；此处收集 case 目标。
                        // The 31t payload target is collected via BranchTargetOperand; here we
                        // collect the case targets from the payload values.
                        CollectSwitchCaseTargets(leaders, instructions, insn, payload, offsetToIndex);
                        break;
                }
            }

            if (IsTerminator(insn.Mnemonic))
            {
                isTerminator = true;
            }

            if (isTerminator && i + 1 < instructions.Count)
            {
                leaders.Add(instructions[i + 1].Offset);
            }
        }

        // 第二遍：按 leader 切块。
        // Pass 2: slice blocks at leaders.
        var blockByStart = new Dictionary<int, BasicBlock>();
        var id = 0;
        BasicBlock? current = null;
        foreach (var insn in instructions)
        {
            if (leaders.Contains(insn.Offset))
            {
                current = new BasicBlock { Id = id++, StartOffset = insn.Offset };
                cfg.Blocks.Add(current);
                blockByStart[insn.Offset] = current;
                cfg.ByStartOffset[insn.Offset] = current;
            }

            current!.EndOffset = insn.Offset + insn.Size * 2;
            current.Instructions.Add(insn);
        }

        // 第三遍：确定块种类并连接控制流边。
        // Pass 3: decide block kinds and wire control-flow edges.
        for (var i = 0; i < cfg.Blocks.Count; i++)
        {
            var block = cfg.Blocks[i];
            var terminator = block.Instructions[^1];
            var next = i + 1 < cfg.Blocks.Count ? cfg.Blocks[i + 1] : null;

            if (terminator.Mnemonic.StartsWith("if-", System.StringComparison.Ordinal))
            {
                block.Kind = BlockKind.Conditional;
                block.BranchTarget = FindBranchTarget(block, terminator, blockByStart);
                block.Fallthrough = next;
                AddSuccessor(block, block.BranchTarget);
                AddSuccessor(block, next);
            }
            else if (terminator.Mnemonic.StartsWith("goto", System.StringComparison.Ordinal))
            {
                block.Kind = BlockKind.UnconditionalGoto;
                block.GotoTarget = FindBranchTarget(block, terminator, blockByStart);
                AddSuccessor(block, block.GotoTarget);
            }
            else if (terminator.Mnemonic.StartsWith("return", System.StringComparison.Ordinal))
            {
                block.Kind = BlockKind.Return;
            }
            else if (terminator.Mnemonic == "throw")
            {
                block.Kind = BlockKind.Throw;
            }
            else if (terminator.Mnemonic is "packed-switch" or "sparse-switch")
            {
                block.Kind = BlockKind.Switch;
                CollectSwitchTargets(block, terminator, instructions, blockByStart);
                block.Fallthrough = next;
                AddSuccessor(block, next);
            }
            else
            {
                block.Kind = BlockKind.Sequential;
                block.Fallthrough = next;
                AddSuccessor(block, next);
            }
        }

        cfg.Entry = cfg.Blocks[0];
        return cfg;
    }

    /// <summary>
    /// <para>判断助记符是否为块终止指令。</para>
    /// Determines whether a mnemonic terminates a block.
    /// </summary>
    private static bool IsTerminator(string mnemonic) => mnemonic.StartsWith("if-", System.StringComparison.Ordinal)
        || mnemonic.StartsWith("goto", System.StringComparison.Ordinal)
        || mnemonic.StartsWith("return", System.StringComparison.Ordinal)
        || mnemonic == "throw"
        || mnemonic is "packed-switch" or "sparse-switch";

    private static BasicBlock? FindBranchTarget(BasicBlock block, Instruction terminator, Dictionary<int, BasicBlock> blockByStart)
    {
        foreach (var operand in terminator.Operands)
        {
            if (operand is BranchTargetOperand target && blockByStart.TryGetValue(target.Offset, out var targetBlock))
            {
                return targetBlock;
            }
        }

        return null;
    }

    private static void CollectSwitchTargets(BasicBlock block, Instruction terminator, List<Instruction> instructions, Dictionary<int, BasicBlock> blockByStart)
    {
        // 定位 switch 指令引用的 payload 伪指令，取其 case 目标。
        // Locate the payload pseudo-instruction referenced by the switch and read its case targets.
        foreach (var operand in terminator.Operands)
        {
            if (operand is not BranchTargetOperand payloadRef) continue;

            foreach (var payload in instructions)
            {
                if (payload.Offset != payloadRef.Offset || payload.Operands.Count == 0) continue;

                var payloadUnit = payload.Offset / 2;
                foreach (var payloadOperand in payload.Operands)
                {
                    if (payloadOperand is not PayloadOperand p) continue;
                    if (p.Kind == "packed-switch" && terminator.Mnemonic == "packed-switch")
                    {
                        foreach (var rel in p.Values)
                        {
                            if (blockByStart.TryGetValue((payloadUnit + rel) * 2, out var target))
                            {
                                block.SwitchTargets.Add(target);
                            }
                        }
                    }
                    else if (p.Kind == "targets")
                    {
                        foreach (var rel in p.Values)
                        {
                            if (blockByStart.TryGetValue((payloadUnit + rel) * 2, out var target))
                            {
                                block.SwitchTargets.Add(target);
                            }
                        }
                    }
                }

                break;
            }
        }
    }

    private static void CollectSwitchCaseTargets(SortedSet<int> leaders, List<Instruction> instructions, Instruction insn, PayloadOperand payload, Dictionary<int, int> offsetToIndex)
    {
        // 该调用来自 switch 指令本身（携带 31t payload 引用），需先解析 payload 偏移。
        // This call originates from the switch instruction itself (holding the 31t payload ref),
        // so resolve the payload offset first.
        var payloadOffset = -1;
        foreach (var operand in insn.Operands)
        {
            if (operand is BranchTargetOperand target)
            {
                payloadOffset = target.Offset;
                break;
            }
        }

        if (payloadOffset < 0) return;
        if (!offsetToIndex.TryGetValue(payloadOffset, out var payloadIndex) || payloadIndex >= instructions.Count)
        {
            return;
        }

        var payloadInsn = instructions[payloadIndex];
        var payloadUnit = payloadInsn.Offset / 2;
        foreach (var payloadOperand in payloadInsn.Operands)
        {
            if (payloadOperand is not PayloadOperand p) continue;
            if (p.Kind is "packed-switch" or "targets")
            {
                foreach (var rel in p.Values)
                {
                    leaders.Add((payloadUnit + rel) * 2);
                }
            }
        }
    }

    private static void AddSuccessor(BasicBlock block, BasicBlock? successor)
    {
        if (successor is null || block.Successors.Contains(successor)) return;
        block.Successors.Add(successor);
        successor.Predecessors.Add(block);
    }
}
