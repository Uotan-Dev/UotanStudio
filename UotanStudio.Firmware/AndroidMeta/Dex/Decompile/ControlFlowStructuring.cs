using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Decompile;

/// <summary>
/// Recovers structured Java control flow (if, while, do-while, switch) from a DEX method's
/// control-flow graph.
/// <para>从 DEX 方法的控制流图还原结构化 Java 控制流（if、while、do-while、switch）。</para>
/// </summary>
internal static class ControlFlowStructuring
{
    /// <summary>Structures the method into a complete statement list.
    /// <para>结构化方法的完整语句列表。</para></summary>
    /// <param name="analysis">Linear register analysis whose per-offset items supply the statements, branch conditions and switch values.
    /// <para>线性寄存器分析结果，其按偏移索引的条目提供语句、分支条件与 switch 值。</para></param>
    /// <param name="cfg">Control-flow graph of the method body.<para>方法体的控制流图。</para></param>
    /// <param name="options">Output settings for the decompilation stages.
    /// <para>反编译各阶段的输出设置。</para></param>
    /// <returns>Statements reachable from the graph entry in emission order; an empty list when <paramref name="cfg"/> has no entry block. Blocks that cannot be structured are emitted as their plain statements plus a raw comment describing the unresolved branch. Each block's <see cref="BasicBlock.Visited"/> flag is cleared before traversal, so the result depends only on the graph shape.
    /// <para>按输出顺序返回从图入口可达的语句；<paramref name="cfg"/> 没有入口块时返回空列表。无法结构化的块 会输出其普通语句，并附带一条描述未解析分支的原始注释。遍历前会清除每个块的 <see cref="BasicBlock.Visited"/> 标志，因此结果只取决于图结构。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="analysis"/>, <paramref name="cfg"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="analysis"/>、<paramref name="cfg"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    public static List<JavaStmt> Structure(LinearAnalysis analysis, Cfg cfg, DecompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(options);

        var structurer = new Structurer(analysis, cfg, options);
        return structurer.Run();
    }

    private sealed class LoopContext
    {
        public required BasicBlock Header { get; init; }
        public required BasicBlock? Exit { get; init; }
    }

    private sealed class Structurer
    {
        private readonly LinearAnalysis _analysis;
        private readonly Cfg _cfg;
        private readonly DecompileOptions _options;
        private readonly Dictionary<int, LinearItem> _itemsByOffset = [];
        private readonly List<JavaStmt> _result = [];

        public Structurer(LinearAnalysis analysis, Cfg cfg, DecompileOptions options)
        {
            _analysis = analysis;
            _cfg = cfg;
            _options = options;
            foreach (var item in analysis.Items)
            {
                _itemsByOffset[item.Insn.Offset] = item;
            }
        }

        public List<JavaStmt> Run()
        {
            foreach (var block in _cfg.Blocks)
            {
                block.Visited = false;
            }

            if (_cfg.Entry is not null)
            {
                _result.AddRange(EmitBlock(_cfg.Entry, loop: null));
            }

            return _result;
        }

        private List<JavaStmt> EmitBlock(BasicBlock block, LoopContext? loop)
        {
            var emitted = new List<JavaStmt>();


            if (block.Visited || (loop is not null && ReferenceEquals(block, loop.Exit)))
            {
                return emitted;
            }

            block.Visited = true;

            switch (block.Kind)
            {
                case BlockKind.Sequential:
                    emitted.AddRange(Stmts(block));


                    if (block.Fallthrough is not null)
                    {
                        emitted.AddRange(EmitBlock(block.Fallthrough, loop));
                    }

                    break;

                case BlockKind.Return:
                case BlockKind.Throw:
                    emitted.AddRange(Stmts(block));
                    break;

                case BlockKind.Conditional:
                    if (TryEmitWhile(block, loop, emitted) || TryEmitDoWhile(block, loop, emitted) || TryEmitIf(block, loop, emitted))
                    {
                        break;
                    }


                    emitted.AddRange(Stmts(block));
                    if (GetCondition(block) is { } cond)
                    {
                        emitted.Add(new RawCommentStmt($"if ({cond}) goto {LabelOf(block.BranchTarget)}") { Offset = block.StartOffset });
                    }

                    if (block.BranchTarget is not null)
                    {
                        emitted.AddRange(EmitBlock(block.BranchTarget, loop));
                    }

                    if (block.Fallthrough is not null)
                    {
                        emitted.AddRange(EmitBlock(block.Fallthrough, loop));
                    }

                    break;

                case BlockKind.UnconditionalGoto:
                    if (loop is not null && ReferenceEquals(block.GotoTarget, loop.Header))
                    {


                        emitted.AddRange(Stmts(block));
                    }
                    else if (loop is not null && ReferenceEquals(block.GotoTarget, loop.Exit))
                    {
                        emitted.AddRange(Stmts(block));
                        emitted.Add(new BreakStmt { Offset = block.StartOffset });
                    }
                    else
                    {
                        emitted.AddRange(Stmts(block));
                        emitted.Add(new GotoCommentStmt(LabelOf(block.GotoTarget)) { Offset = block.StartOffset });
                    }

                    break;

                case BlockKind.Switch:
                    if (!TryEmitSwitch(block, loop, emitted))
                    {
                        emitted.AddRange(Stmts(block));
                        emitted.Add(new RawCommentStmt("switch <unstructured>") { Offset = block.StartOffset });
                    }

                    break;
            }

            return emitted;
        }

        /// <summary>
        /// Tries a while loop: a conditional block whose body region contains a back-edge goto to itself.
        /// <para>尝试 while 循环：条件块 + 体区域含回边 goto 到本块。</para>
        /// </summary>
        private bool TryEmitWhile(BasicBlock block, LoopContext? loop, List<JavaStmt> emitted)
        {
            if (block.Fallthrough is null || block.BranchTarget is null) return false;
            if (ReferenceEquals(block.BranchTarget, block.Fallthrough)) return false;
            if (!RegionBackEdgesTo(block.Fallthrough, block)) return false;

            var exit = block.BranchTarget;
            var body = new List<JavaStmt>();
            EmitLoopBody(block.Fallthrough, exit, loopHeader: block, body);

            emitted.AddRange(Stmts(block));
            var whileStmt = new WhileStmt(Negate(GetCondition(block))) { Offset = block.StartOffset };
            whileStmt.Body.AddRange(body);
            emitted.Add(whileStmt);


            if (!exit.Visited)
            {
                emitted.AddRange(EmitBlock(exit, loop));
            }

            return true;
        }

        /// <summary>
        /// Tries a do-while loop: the conditional latch at the tail of a sequential chain branches back to the chain head.
        /// <para>尝试 do-while 循环：顺序体链尾的条件块回边到链首。</para>
        /// </summary>
        private bool TryEmitDoWhile(BasicBlock block, LoopContext? loop, List<JavaStmt> emitted)
        {
            if (block.Kind != BlockKind.Conditional) return false;
            if (block.Fallthrough is null || block.Fallthrough == block.BranchTarget) return false;

            var start = block.Fallthrough;
            var latch = start;
            while (latch is not null && latch.Kind == BlockKind.Sequential)
            {
                latch = latch.Fallthrough;
            }

            if (latch is null || latch.Kind != BlockKind.Conditional) return false;
            if (!ReferenceEquals(latch.BranchTarget, start) && !ReferenceEquals(latch.BranchTarget, block.Fallthrough))
            {
                return false;
            }

            var exit = latch.Fallthrough;
            var body = new List<JavaStmt>();
            EmitLoopBody(start, exit, loopHeader: start, body);


            var doWhile = new DoWhileStmt(GetCondition(latch) ?? new UnknownExpr("?")) { Offset = block.StartOffset };
            doWhile.Body.AddRange(body);
            emitted.Add(doWhile);

            if (exit is not null && !exit.Visited)
            {
                emitted.AddRange(EmitBlock(exit, loop));
            }

            return true;
        }

        /// <summary>
        /// Tries if / if-else: a conditional block with forward targets (including the explicit-merge pattern).
        /// <para>尝试 if / if-else：条件块 + 前向目标（含显式 merge 模式）。</para>
        /// </summary>
        private bool TryEmitIf(BasicBlock block, LoopContext? loop, List<JavaStmt> emitted)
        {
            var target = block.BranchTarget;
            var fallthrough = block.Fallthrough;
            if (target is null || fallthrough is null) return false;
            if (ReferenceEquals(target, fallthrough)) return false;
            if (target.StartOffset < block.StartOffset && !IsLoopBackTarget(target, loop))
            {
                return false;
            }

            var cond = GetCondition(block);
            if (cond is null) return false;

            var ifStmt = new IfStmt(cond) { Offset = block.StartOffset };


            var chainBlocks = new List<BasicBlock>();
            var chainCur = fallthrough;
            var merge = (BasicBlock?)null;
            var chainEnds = false;
            var chainVisited = new HashSet<BasicBlock>();
            while (chainCur is not null && !chainCur.Visited && chainVisited.Add(chainCur))
            {
                if (chainCur.Kind == BlockKind.UnconditionalGoto)
                {


                    chainBlocks.Add(chainCur);
                    merge = chainCur.GotoTarget;
                    chainEnds = true;
                    break;
                }

                if (chainCur.Kind is BlockKind.Return or BlockKind.Throw)
                {
                    chainBlocks.Add(chainCur);
                    chainEnds = true;
                    break;
                }

                if (chainCur.Kind != BlockKind.Sequential)
                {
                    break;
                }

                chainBlocks.Add(chainCur);
                chainCur = chainCur.Fallthrough;
            }

            if (chainEnds)
            {
                var thenBody = new List<JavaStmt>();
                EmitThenRegion(target, merge, thenBody, loop);
                ifStmt.Then.AddRange(thenBody);

                foreach (var chainBlock in chainBlocks)
                {
                    ifStmt.Else.AddRange(Stmts(chainBlock));
                }

                emitted.Add(ifStmt);
                if (merge is not null && !merge.Visited)
                {
                    emitted.AddRange(EmitBlock(merge, loop));
                }

                return true;
            }


            if (TargetJoins(target, fallthrough))
            {
                var thenBody = new List<JavaStmt>();
                EmitThenRegion(target, fallthrough, thenBody, loop);
                ifStmt.Then.AddRange(thenBody);
                emitted.Add(ifStmt);
                emitted.AddRange(EmitBlock(fallthrough, loop));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Emits a statement sequence inside a loop body: handles back edges (latches), breaks (goto/condition → exit) and inner ifs.
        /// <para>在循环体内发射语句序列：处理回边（latch）、break（goto/条件 → 出口）与内部 if。</para>
        /// </summary>
        private void EmitLoopBody(BasicBlock start, BasicBlock? exit, BasicBlock loopHeader, List<JavaStmt> body)
        {
            var cur = start;
            var visited = new HashSet<BasicBlock>();
            while (cur is not null && !ReferenceEquals(cur, exit) && !cur.Visited && visited.Add(cur))
            {
                if (ReferenceEquals(cur.GotoTarget, loopHeader))
                {
                    body.AddRange(Stmts(cur));
                    return;
                }

                if (ReferenceEquals(cur.GotoTarget, exit))
                {
                    body.AddRange(Stmts(cur));
                    body.Add(new BreakStmt { Offset = cur.StartOffset });
                    return;
                }

                if (cur.Kind == BlockKind.Conditional && ReferenceEquals(cur.BranchTarget, exit))
                {
                    body.AddRange(Stmts(cur));
                    if (GetCondition(cur) is { } cond)
                    {
                        var brk = new IfStmt(cond) { Offset = cur.StartOffset };
                        brk.Then.Add(new BreakStmt());
                        body.Add(brk);
                    }

                    cur = cur.Fallthrough;
                    continue;
                }

                if (cur.Kind == BlockKind.Conditional && ReferenceEquals(cur.BranchTarget, loopHeader))
                {
                    body.AddRange(Stmts(cur));
                    return;
                }

                if (cur.Kind == BlockKind.Conditional)
                {
                    var nested = new List<JavaStmt>();
                    if (TryEmitIf(cur, new LoopContext { Header = loopHeader, Exit = exit }, nested))
                    {
                        body.AddRange(nested);
                    }
                    else
                    {
                        body.AddRange(Stmts(cur));
                        if (GetCondition(cur) is { } cond)
                        {
                            body.Add(new RawCommentStmt($"if ({cond}) goto {LabelOf(cur.BranchTarget)}") { Offset = cur.StartOffset });
                        }
                    }

                    cur = cur.Fallthrough;
                    continue;
                }

                if (cur.Kind == BlockKind.UnconditionalGoto)
                {
                    body.AddRange(Stmts(cur));
                    body.Add(new GotoCommentStmt(LabelOf(cur.GotoTarget)) { Offset = cur.StartOffset });
                    return;
                }

                body.AddRange(Stmts(cur));
                cur = cur.Fallthrough;
            }
        }

        /// <summary>
        /// Emits a then region: walks from the target block until the merge block (or a return/throw/goto).
        /// <para>发射 then 区域：从目标块走到 merge 块（或 return/throw/goto 结束）。</para>
        /// </summary>
        private void EmitThenRegion(BasicBlock target, BasicBlock? merge, List<JavaStmt> sink, LoopContext? loop)
        {
            var cur = target;
            var visited = new HashSet<BasicBlock>();
            while (cur is not null && !ReferenceEquals(cur, merge) && !cur.Visited && visited.Add(cur))
            {
                switch (cur.Kind)
                {
                    case BlockKind.Return:
                    case BlockKind.Throw:
                        sink.AddRange(Stmts(cur));
                        return;
                    case BlockKind.UnconditionalGoto:
                        sink.AddRange(Stmts(cur));
                        if (!ReferenceEquals(cur.GotoTarget, merge))
                        {
                            sink.Add(new GotoCommentStmt(LabelOf(cur.GotoTarget)) { Offset = cur.StartOffset });
                        }

                        return;
                    case BlockKind.Conditional:
                        var nested = new List<JavaStmt>();
                        if (TryEmitIf(cur, loop, nested))
                        {
                            sink.AddRange(nested);
                        }
                        else
                        {
                            sink.AddRange(Stmts(cur));
                            if (GetCondition(cur) is { } cond)
                            {
                                sink.Add(new RawCommentStmt($"if ({cond}) goto {LabelOf(cur.BranchTarget)}") { Offset = cur.StartOffset });
                            }
                        }

                        cur = cur.Fallthrough;
                        continue;
                    case BlockKind.Switch:
                        if (!TryEmitSwitch(cur, loop, sink))
                        {
                            sink.AddRange(Stmts(cur));
                        }

                        cur = cur.Fallthrough;
                        continue;
                    default:
                        sink.AddRange(Stmts(cur));
                        cur = cur.Fallthrough;
                        continue;
                }
            }
        }

        /// <summary>
        /// Tries a switch: emits a switch statement when all cases converge on one exit, otherwise degrades to an if-else chain.
        /// <para>尝试 switch：全部 case 汇聚到同一出口时输出 switch 语句，否则退化为 if-else 链。</para>
        /// </summary>
        private bool TryEmitSwitch(BasicBlock block, LoopContext? loop, List<JavaStmt> emitted)
        {
            if (!TryGetSwitchKeys(block, out var keys) || keys.Count != block.SwitchTargets.Count)
            {
                return false;
            }

            var targets = block.SwitchTargets;
            var caseTargetSet = new HashSet<BasicBlock>(targets);


            var commonExit = (BasicBlock?)null;
            var structured = true;
            foreach (var target in targets)
            {
                var exit = ProbeCaseExit(target, caseTargetSet);
                if (exit is null)
                {
                    structured = false;
                    break;
                }

                commonExit ??= exit;
                if (!ReferenceEquals(commonExit, exit))
                {
                    structured = false;
                    break;
                }
            }

            var switchValue = block.SwitchTargets.Count > 0 || keys.Count > 0 ? SwitchValueOf(block) : null;
            if (switchValue is null)
            {
                emitted.AddRange(Stmts(block));
                return false;
            }

            if (structured && commonExit is not null && block.Fallthrough is not null)
            {


                var switchStmt = new SwitchStmt(switchValue) { Offset = block.StartOffset };
                for (var i = 0; i < keys.Count; i++)
                {
                    var caseBody = new List<JavaStmt>();
                    EmitThenRegion(targets[i], commonExit, caseBody, loop);
                    switchStmt.Cases.Add((keys[i], caseBody));
                }

                emitted.Add(switchStmt);
                if (!commonExit.Visited)
                {
                    emitted.AddRange(EmitBlock(commonExit, loop));
                }

                return true;
            }


            IfStmt? chainRoot = null;
            for (var i = keys.Count - 1; i >= 0; i--)
            {
                var caseBody = new List<JavaStmt>();
                EmitThenRegion(targets[i], commonExit, caseBody, loop);
                var link = new IfStmt(new BinaryExpr("==", switchValue, new LiteralExpr(keys[i]))) { Offset = block.StartOffset };
                link.Then.AddRange(caseBody);
                if (chainRoot is not null)
                {
                    link.Else.Add(chainRoot);
                }

                chainRoot = link;
            }

            if (chainRoot is not null)
            {
                emitted.Add(chainRoot);
            }

            if (block.Fallthrough is not null && !block.Fallthrough.Visited)
            {
                emitted.AddRange(EmitBlock(block.Fallthrough, loop));
            }

            return true;
        }

        /// <summary>
        /// Probes a case's exit block: returns the block when the whole case body converges on one goto target, otherwise null.
        /// <para>探测单个 case 的出口块：case 体全部以 goto 汇到同一块时返回该块，否则 null。</para>
        /// </summary>
        private BasicBlock? ProbeCaseExit(BasicBlock start, HashSet<BasicBlock> caseTargetSet)
        {
            var cur = start;
            var visited = new HashSet<BasicBlock>();
            while (cur is not null && !cur.Visited && visited.Add(cur))
            {
                switch (cur.Kind)
                {
                    case BlockKind.UnconditionalGoto:
                        return cur.GotoTarget;
                    case BlockKind.Return:
                    case BlockKind.Throw:
                        return cur;
                    case BlockKind.Sequential:
                        cur = cur.Fallthrough;
                        continue;
                    default:
                        return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts the switch key sequence (packed expands first_key; sparse reads the key table).
        /// <para>提取 switch 的键序列（packed 由 first_key 展开，sparse 直接取键表）。</para>
        /// </summary>
        private bool TryGetSwitchKeys(BasicBlock block, out List<long> keys)
        {
            keys = [];
            if (block.Instructions.Count == 0) return false;

            var switchInsn = block.Instructions[^1];
            foreach (var operand in switchInsn.Operands)
            {
                if (operand is not BranchTargetOperand payloadRef) continue;
                if (!_itemsByOffset.TryGetValue(payloadRef.Offset, out var payloadItem)) continue;

                foreach (var payloadOperand in payloadItem.Insn.Operands)
                {
                    if (payloadOperand is not PayloadOperand p) continue;
                    if (switchInsn.Mnemonic == "packed-switch" && p.Kind == "packed-switch")
                    {
                        for (var i = 0; i < p.Values.Length; i++)
                        {
                            keys.Add(p.FirstKey + i);
                        }

                        return true;
                    }

                    if (switchInsn.Mnemonic == "sparse-switch" && p.Kind == "sparse-switch")
                    {
                        foreach (var key in p.Values)
                        {
                            keys.Add(key);
                        }

                        return true;
                    }
                }
            }

            return false;
        }

        private JavaExpr? SwitchValueOf(BasicBlock block)
        {
            if (block.Instructions.Count == 0) return null;
            var item = _itemsByOffset.GetValueOrDefault(block.Instructions[^1].Offset);
            return item?.SwitchValue;
        }

        private JavaExpr? GetCondition(BasicBlock block)
        {
            if (block.Instructions.Count == 0) return null;
            var item = _itemsByOffset.GetValueOrDefault(block.Instructions[^1].Offset);
            return item?.IfCondition;
        }

        /// <summary>
        /// All non-null statements of a block (in instruction order).
        /// <para>块内全部非空语句（按指令顺序）。</para>
        /// </summary>
        private List<JavaStmt> Stmts(BasicBlock block)
        {
            var stmts = new List<JavaStmt>(block.Instructions.Count);
            foreach (var insn in block.Instructions)
            {
                if (_itemsByOffset.TryGetValue(insn.Offset, out var item) && item.Stmt is not null)
                {
                    stmts.Add(item.Stmt);
                }
            }

            return stmts;
        }

        /// <summary>
        /// Detects whether the fallthrough region contains a back-edge goto to header.
        /// <para>检测 fallthrough 区域是否存在回边 goto 到 header。</para>
        /// </summary>
        private static bool RegionBackEdgesTo(BasicBlock start, BasicBlock header)
        {
            var cur = start;
            var visited = new HashSet<BasicBlock>();
            while (cur is not null && visited.Add(cur))
            {
                if (ReferenceEquals(cur.GotoTarget, header)) return true;
                cur = cur.Kind == BlockKind.Sequential || cur.Kind == BlockKind.Conditional ? cur.Fallthrough : null;
            }

            return false;
        }

        private static bool TargetJoins(BasicBlock target, BasicBlock fallthrough)
            => (target.Kind == BlockKind.UnconditionalGoto && ReferenceEquals(target.GotoTarget, fallthrough))
               || (target.Kind == BlockKind.Sequential && ReferenceEquals(target.Fallthrough, fallthrough));

        private static bool IsLoopBackTarget(BasicBlock target, LoopContext? loop)
            => loop is not null && ReferenceEquals(target, loop.Header);

        private static string LabelOf(BasicBlock? block) => block is null ? ":?" : $":label_{block.StartOffset:X4}";

        private static JavaExpr Negate(JavaExpr? cond)
        {
            if (cond is BinaryExpr binary)
            {
                return new BinaryExpr(Swap(binary.Op), binary.Left, binary.Right);
            }

            return new UnaryExpr("!", cond ?? new UnknownExpr("?"));
        }

        private static string Swap(string op) => op switch
        {
            "==" => "!=",
            "!=" => "==",
            "<" => ">=",
            "<=" => ">",
            ">" => "<=",
            ">=" => "<",
            _ => op
        };
    }
}