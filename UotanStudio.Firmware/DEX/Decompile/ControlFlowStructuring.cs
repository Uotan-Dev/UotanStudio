using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>控制流结构化器：在 CFG 与线性分析之上恢复 while / do-while / if-else / switch 等结构化
/// 控制流；无法恢复的分支以 goto 指导注释保留（方法指导模式）。</para>
/// Control-flow structurer: recovers structured control flow (while / do-while / if-else / switch)
/// on top of the CFG and linear analysis; branches that cannot be recovered are kept as goto
/// guidance comments (method-guide mode).
/// </summary>
public static class ControlFlowStructuring
{
    /// <summary>
    /// <para>结构化方法的完整语句列表。</para>
    /// Structures the method into a complete statement list.
    /// </summary>
    /// <param name="analysis">线性分析结果。<para>The linear analysis result.</para></param>
    /// <param name="cfg">控制流图。<para>The control-flow graph.</para></param>
    /// <param name="options">反编译选项。<para>Decompiler options.</para></param>
    /// <returns>结构化语句列表。<para>The structured statement list.</para></returns>
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

            // 循环出口或已访问（回边保护）。
            // Loop exit or already visited (back-edge guard).
            if (block.Visited || (loop is not null && ReferenceEquals(block, loop.Exit)))
            {
                return emitted;
            }

            block.Visited = true;

            switch (block.Kind)
            {
                case BlockKind.Sequential:
                    emitted.AddRange(Stmts(block));
                    // 顺序块沿 fallthrough 续走；循环出口与已访问由入口守卫拦截。
                    // A sequential block continues along its fallthrough; the loop-exit and
                    // visited guards at the entry handle the boundaries.
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

                    // 回退：条件以指导注释保留，两个目标都继续遍历。
                    // Fallback: keep the condition as a guidance comment and walk both targets.
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
                        // 回边：循环延续（不产生语句）。
                        // Back edge: loop continuation (no statement).
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
        /// <para>尝试 while 循环：条件块 + 体区域含回边 goto 到本块。</para>
        /// Tries a while loop: a conditional block whose body region contains a back-edge goto to itself.
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

            // 循环后的出口继续结构化。
            // Continue structuring after the loop exit.
            if (!exit.Visited)
            {
                emitted.AddRange(EmitBlock(exit, loop));
            }

            return true;
        }

        /// <summary>
        /// <para>尝试 do-while 循环：顺序体链尾的条件块回边到链首。</para>
        /// Tries a do-while loop: the conditional latch at the tail of a sequential chain branches
        /// back to the chain head.
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
            // latch 是条件回边块：其语句已由 EmitLoopBody 保留，条件构成 do-while 的尾条件。
            // The latch is the conditional back-edge block: EmitLoopBody keeps its statements and
            // its condition becomes the do-while tail condition.

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
        /// <para>尝试 if / if-else：条件块 + 前向目标（含显式 merge 模式）。</para>
        /// Tries if / if-else: a conditional block with forward targets (including the explicit-merge pattern).
        /// </summary>
        private bool TryEmitIf(BasicBlock block, LoopContext? loop, List<JavaStmt> emitted)
        {
            var target = block.BranchTarget;
            var fallthrough = block.Fallthrough;
            if (target is null || fallthrough is null) return false;
            if (ReferenceEquals(target, fallthrough)) return false;
            if (target.StartOffset < block.StartOffset && !IsLoopBackTarget(target, loop))
            {
                return false;   // 向后分支留给循环检测。
            }

            var cond = GetCondition(block);
            if (cond is null) return false;

            var ifStmt = new IfStmt(cond) { Offset = block.StartOffset };

            // Pattern A：沿 fallthrough 链（顺序块序列）寻找 goto-merge 或 return/throw 终结；
            // 链首为 goto 时 else 为空，链为实体时构成 else 体。then 体为从 target 到 merge 的区域。
            // Pattern A: walk the fallthrough chain (sequential blocks) for a goto-merge or a
            // return/throw end; a leading goto means an empty else, real blocks form the else body.
            // The then body is the region from the target to the merge.
            var chainBlocks = new List<BasicBlock>();
            var chainCur = fallthrough;
            var merge = (BasicBlock?)null;
            var chainEnds = false;
            var chainVisited = new HashSet<BasicBlock>();
            while (chainCur is not null && !chainCur.Visited && chainVisited.Add(chainCur))
            {
                if (chainCur.Kind == BlockKind.UnconditionalGoto)
                {
                    // goto 块自身可能携带实体语句（else 体），计入链块。
                    // The goto block itself may carry real statements (the else body); keep it.
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
                    break;   // 链中出现条件/开关等复杂形态：放弃该模式。
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

            // Pattern B：then 目标跳回 fallthrough（if 无 else，then 汇入续体）。
            // Pattern B: the then target joins the fallthrough (if without else).
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
        /// <para>在循环体内发射语句序列：处理回边（latch）、break（goto/条件 → 出口）与内部 if。</para>
        /// Emits a statement sequence inside a loop body: handles back edges (latches), breaks
        /// (goto/condition → exit) and inner ifs.
        /// </summary>
        /// <param name="start">体起始块。<para>Body start block.</para></param>
        /// <param name="exit">循环出口块；<c>null</c> 表示无出口边界。<para>Loop exit block; <c>null</c> when there is no exit boundary.</para></param>
        /// <param name="loopHeader">循环头块。<para>Loop header block.</para></param>
        /// <param name="body">语句收集目标。<para>Statement sink.</para></param>
        private void EmitLoopBody(BasicBlock start, BasicBlock? exit, BasicBlock loopHeader, List<JavaStmt> body)
        {
            var cur = start;
            var visited = new HashSet<BasicBlock>();
            while (cur is not null && !ReferenceEquals(cur, exit) && !cur.Visited && visited.Add(cur))
            {
                if (ReferenceEquals(cur.GotoTarget, loopHeader))
                {
                    body.AddRange(Stmts(cur));
                    return;   // 回边 latch：循环继续。
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
                    return;   // 条件回边：由外部循环结构承接。
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
        /// <para>发射 then 区域：从目标块走到 merge 块（或 return/throw/goto 结束）。</para>
        /// Emits a then region: walks from the target block until the merge block (or a return/throw/goto).
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
        /// <para>尝试 switch：全部 case 汇聚到同一出口时输出 switch 语句，否则退化为 if-else 链。</para>
        /// Tries a switch: emits a switch statement when all cases converge on one exit, otherwise
        /// degrades to an if-else chain.
        /// </summary>
        private bool TryEmitSwitch(BasicBlock block, LoopContext? loop, List<JavaStmt> emitted)
        {
            if (!TryGetSwitchKeys(block, out var keys) || keys.Count != block.SwitchTargets.Count)
            {
                return false;
            }

            var targets = block.SwitchTargets;
            var caseTargetSet = new HashSet<BasicBlock>(targets);

            // 先探测公共出口：各 case 是否都以 goto 汇到同一块。
            // First probe a common exit: whether every case ends with a goto to the same block.
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
                // 公共出口即 switch 后的续体。
                // The common exit is the continuation after the switch.
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

            // if-else 链退化：从后往前构建，最内层（最后一个 key）先建，再逐层包进外层 else。
            // if-else chain fallback: build from the tail — the innermost (last key) first, then
            // wrap each level into the outer else branch.
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
        /// <para>探测单个 case 的出口块：case 体全部以 goto 汇到同一块时返回该块，否则 <c>null</c>。</para>
        /// Probes a case's exit block: returns the block when the whole case body converges on one
        /// goto target, otherwise <c>null</c>.
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
                        return null;   // 条件/嵌套等复杂形态不构成单一出口。
                }
            }

            return null;
        }

        /// <summary>
        /// <para>提取 switch 的键序列（packed 由 first_key 展开，sparse 直接取键表）。</para>
        /// Extracts the switch key sequence (packed expands first_key; sparse reads the key table).
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
        /// <para>块内全部非空语句（按指令顺序）。</para>
        /// All non-null statements of a block (in instruction order).
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
        /// <para>检测 fallthrough 区域是否存在回边 goto 到 <paramref name="header"/>。</para>
        /// Detects whether the fallthrough region contains a back-edge goto to <paramref name="header"/>.
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