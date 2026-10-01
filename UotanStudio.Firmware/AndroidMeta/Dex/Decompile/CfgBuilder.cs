using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Decompile;

/// <summary>
/// Basic-block control-flow graph of one DEX method body.
/// <para>单个 DEX 方法体的基本块控制流图。</para>
/// </summary>
internal sealed class Cfg
{
    /// <summary>Blocks of the method body.
    /// <para>方法体的全部基本块。</para></summary>
    /// <value>Ordered by ascending start offset, with the entry block first; empty for a body with no
    /// instructions. <para>按起始偏移升序排列，入口块在最前；方法体没有指令时为空。</para></value>
    public List<BasicBlock> Blocks { get; } = [];

    /// <summary>Block where execution starts.
    /// <para>执行起始块。</para></summary>
    /// <value>The first element of <see cref="Blocks"/>, or <see langword="null"/> when the graph has
    /// no blocks. <para><see cref="Blocks"/> 的首个元素；图中没有块时为
    /// <see langword="null"/>。</para></value>
    public BasicBlock? Entry { get; set; }

    /// <summary>Blocks indexed by their first instruction offset.
    /// <para>按首个指令偏移索引的块。</para></summary>
    /// <value>Key is the block start offset in bytes within the code item; every entry matches the
    /// <see cref="BasicBlock.StartOffset"/> of the mapped block.
    /// <para>键为块在代码项内以字节计算的起始偏移；每项的键都等于所映射块的
    /// <see cref="BasicBlock.StartOffset"/>。</para></value>
    public Dictionary<int, BasicBlock> ByStartOffset { get; } = [];

    /// <summary>Whether the body carries any block.
    /// <para>方法体是否包含任何块。</para></summary>
    /// <value><see langword="true"/> when <see cref="Blocks"/> is not empty.
    /// <para><see cref="Blocks"/> 非空时为 <see langword="true"/>。</para></value>
    public bool HasInstructions => Blocks.Count > 0;
}

/// <summary>
/// Splits decoded DEX instructions into basic blocks and wires their successors.
/// <para>把解码后的 DEX 指令切分为基本块并连接后继关系。</para>
/// </summary>
internal static class CfgBuilder
{
    /// <summary>
    /// Builds a control-flow graph from a code item (decoding instructions on demand).
    /// <para>从代码项构建控制流图（未解码的指令按需解码）。</para>
    /// </summary>
    /// <param name="code">Code item holding either decoded instructions or raw instruction bytes.
    /// <para>持有已解码指令或原始指令字节的代码项。</para></param>
    /// <param name="dex">DEX model used to decode <paramref name="code"/> when its instruction list is
    /// empty. <para>当 <paramref name="code"/> 的指令列表为空时用于解码的 DEX 模型。</para></param>
    /// <returns>A graph whose blocks run from offset <c>0</c> to the last instruction, with leaders
    /// taken from branch targets, switch case targets and the instruction after each terminator. A
    /// code item with no instructions yields an empty graph whose <see cref="Cfg.Entry"/> is
    /// <see langword="null"/>. Targets that fall outside a block boundary are left as
    /// <see langword="null"/> references on the block.
    /// <para>返回的图中块从偏移 <c>0</c> 一直排列到最后一条指令，leader 取自分支目标、switch 分支目标以及
    /// 每条终止指令的后继指令。没有指令的代码项产生空图，其 <see cref="Cfg.Entry"/> 为
    /// <see langword="null"/>。落在块边界之外的目标在块上保留为 <see langword="null"/> 引用。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="dex"/> is
    /// <see langword="null"/>. <para><paramref name="code"/> 或 <paramref name="dex"/> 为
    /// <see langword="null"/>。</para></exception>
    public static Cfg Build(CodeItem code, DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(dex);

        var instructions = code.Instructions.Count > 0
            ? code.Instructions
            : InstructionDecoder.Decode(code.RawInsns, dex);
        var cfg = new Cfg();
        if (instructions.Count == 0) return cfg;


        SortedSet<int> leaders = [0];
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

                var payloadUnit = terminator.Offset / 2;
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
        var payloadUnit = insn.Offset / 2;
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