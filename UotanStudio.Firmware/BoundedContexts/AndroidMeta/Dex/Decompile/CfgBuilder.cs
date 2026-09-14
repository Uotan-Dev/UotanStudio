using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Instructions;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Model;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

public sealed class Cfg
{
    public List<BasicBlock> Blocks { get; } = [];

    public BasicBlock? Entry { get; set; }

    public Dictionary<int, BasicBlock> ByStartOffset { get; } = [];

    public bool HasInstructions => Blocks.Count > 0;
}

public static class CfgBuilder
{
    /// <summary>
    /// Builds a control-flow graph from a code item (decoding instructions on demand).
    /// <para>从代码项构建控制流图（未解码的指令按需解码）。</para>
    /// </summary>
    public static Cfg Build(CodeItem code, DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(dex);

        var instructions = code.Instructions.Count > 0
            ? code.Instructions
            : InstructionDecoder.Decode(code.RawInsns, dex);
        var cfg = new Cfg();
        if (instructions.Count == 0) return cfg;


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
    /// Determines whether a mnemonic terminates a block.
    /// <para>判断助记符是否为块终止指令。</para>
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