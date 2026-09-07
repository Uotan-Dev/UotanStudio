using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Model;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

public sealed class MethodContext
{
    public required DexFile Dex { get; init; }

    public required EncodedMethod Method { get; init; }

    public ClassDefinition? Class { get; init; }

    public required MethodId MethodId { get; init; }

    public ProtoDefinition? Proto { get; init; }

    public required string MethodName { get; init; }

    public required string ReturnType { get; init; }

    public required string[] ParameterTypes { get; init; }

    public int RegistersSize { get; init; }

    public int InsSize { get; init; }

    public bool IsStatic { get; init; }

    public bool IsConstructor { get; init; }

    public string? ClassName { get; init; }

    /// <summary>
    /// Builds a method context from the model.
    /// <para>从模型构造方法上下文。</para>
    /// </summary>
    public static MethodContext Create(DexFile dex, EncodedMethod method, ClassDefinition? cls)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(method);

        var methodId = dex.GetMethod(method.MethodIndex)
            ?? throw new DexException($"Method index {method.MethodIndex} is out of range.");
        var proto = dex.GetProto(methodId.ProtoIndex);

        string[] parameterTypes;
        if (proto is null)
        {
            parameterTypes = [];
        }
        else
        {
            var indices = proto.ParameterTypeIndices;
            parameterTypes = new string[indices.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                parameterTypes[i] = dex.FormatType(indices[i]);
            }
        }

        var name = dex.GetString(methodId.NameIndex);
        return new MethodContext
        {
            Dex = dex,
            Method = method,
            Class = cls,
            MethodId = methodId,
            Proto = proto,
            MethodName = name,
            ReturnType = proto is null ? "?" : dex.FormatType(proto.ReturnTypeIndex),
            ParameterTypes = parameterTypes,
            RegistersSize = method.Code?.RegistersSize ?? 0,
            InsSize = method.Code?.InsSize ?? 0,
            IsStatic = (method.AccessFlags & AccessFlags.Static) != 0,
            IsConstructor = name == "<init>",
            ClassName = cls is null ? null : dex.FormatType(cls.TypeIndex)
        };
    }
}

public sealed class LinearItem
{
    public Instruction Insn { get; }

    public JavaStmt? Stmt { get; set; }

    public JavaExpr? IfCondition { get; set; }

    public JavaExpr? SwitchValue { get; set; }

    /// <summary>
    /// Creates a linear analysis item.
    /// <para>创建线性分析条目。</para>
    /// </summary>
    public LinearItem(Instruction insn) => Insn = insn;
}

public sealed class LinearAnalysis
{
    public List<LinearItem> Items { get; } = [];

    public MethodContext Context { get; }

    /// <summary>
    /// Creates a linear analysis result.
    /// <para>创建线性分析结果。</para>
    /// </summary>
    public LinearAnalysis(MethodContext context) => Context = context;
}

public static class RegisterAnalyzer
{
    /// <summary>
    /// Analyzes a method's instruction stream.
    /// <para>分析方法的指令流。</para>
    /// </summary>
    public static LinearAnalysis Analyze(MethodContext ctx, List<Instruction> instructions, MethodResolver? resolver = null, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(instructions);
        options ??= DecompileOptions.Default;

        var analysis = new LinearAnalysis(ctx);
        var state = new State();
        foreach (var insn in instructions)
        {
            analysis.Items.Add(Process(insn, ctx, state, resolver, options));
        }

        return analysis;
    }

    /// <summary>
    /// Produces a readable name for a register (arguments p0..pn / this, others v0..vn).
    /// <para>按寄存器编号生成可读名称（参数 p0..pn / this，其余 v0..vn）。</para>
    /// </summary>
    public static string RegisterName(MethodContext ctx, int reg)
    {
        var argStart = ctx.RegistersSize - ctx.InsSize;
        if (reg >= argStart)
        {
            var j = reg - argStart;
            if (!ctx.IsStatic && j == 0) return "this";
            return $"p{j}";
        }

        return $"v{reg}";
    }

    /// <summary>
    /// Infers a static type hint (readable form) for an expression; null when unknown.
    /// <para>推断表达式的静态类型提示（可读形式）；无法推断为 null。</para>
    /// </summary>
    public static string? InferType(JavaExpr? expr) => expr switch
    {
        null => null,
        LiteralExpr l => l.Value is < int.MinValue or > int.MaxValue ? "long" : "int",
        StringExpr => "java.lang.String",
        ClassLiteralExpr => "java.lang.Class",
        NullExpr => null,
        LocalExpr l => l.Type,
        FieldExpr f => f.Type,
        MethodCallExpr m => m.ReturnType,
        NewInstanceExpr n => n.Type,
        NewArrayExpr n => n.ElementType + "[]",
        CastExpr c => c.Type,
        ArrayAccessExpr a => a.Type,
        InstanceOfExpr => "boolean",
        UnaryExpr u => InferType(u.Inner),
        BinaryExpr b => InferType(b.Left) ?? InferType(b.Right),
        TernaryExpr t => InferType(t.Then) ?? InferType(t.Else),
        _ => null
    };

    private sealed class State
    {
        public Dictionary<int, JavaExpr> Regs { get; } = [];
        public JavaExpr? PendingResult;
        public NewInstanceExpr? PendingNew;
    }

    private static LinearItem Process(Instruction insn, MethodContext ctx, State state, MethodResolver? resolver, DecompileOptions options)
    {
        var item = new LinearItem(insn);
        var m = insn.Mnemonic;
        var ops = insn.Operands;


        if (m is "const/4" or "const/16" or "const" or "const/high16"
            or "const-wide/16" or "const-wide/32" or "const-wide" or "const-wide/high16")
        {
            var a = IntAt(ops, 0);
            var lit = LiteralAt(ops, 1);
            if (a is int ra && lit is not null)
            {
                SetReg(state, ra, new LiteralExpr(lit.Value));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }

        if (m is "const-string" or "const-string/jumbo")
        {
            var a = IntAt(ops, 0);
            var value = ops.Count > 1 ? ops[1] as string : null;
            if (a is int ra)
            {
                SetReg(state, ra, new StringExpr(value ?? string.Empty));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }

        if (m == "const-class")
        {
            var a = IntAt(ops, 0);
            var t = TypeRefAt(ops, 1);
            if (a is int ra && t is not null)
            {
                SetReg(state, ra, new ClassLiteralExpr(DexFile.FormatDescriptor(t.Descriptor)));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }


        if (m is "move-result" or "move-result-wide" or "move-result-object")
        {
            var a = IntAt(ops, 0);
            if (a is int ra && state.PendingResult is not null)
            {
                SetReg(state, ra, state.PendingResult);
                if (m == "move-result-wide") state.Regs.Remove(ra + 1);
                state.PendingResult = null;
            }

            return item;
        }

        if (m == "move-exception")
        {
            var a = IntAt(ops, 0);
            if (a is int ra)
            {
                SetReg(state, ra, new LocalExpr(RegisterName(ctx, ra), "java.lang.Throwable"));
            }

            item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
            return item;
        }


        if (m == "move" || m == "move/from16" || m == "move/16"
            || m == "move-wide" || m == "move-wide/from16" || m == "move-wide/16"
            || m == "move-object" || m == "move-object/from16" || m == "move-object/16")
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            if (a is int ra && b is int rb)
            {
                var value = RegValue(state, ctx, rb);
                SetReg(state, ra, value);
                if (m.StartsWith("move-wide", System.StringComparison.Ordinal)) state.Regs.Remove(ra + 1);
                item.Stmt = new AssignStmt(new LocalExpr(RegisterName(ctx, ra)), value) { Offset = insn.Offset };
            }

            return item;
        }


        if (m.StartsWith("iget", System.StringComparison.Ordinal) || m.StartsWith("sget", System.StringComparison.Ordinal))
        {
            var isStatic = m.StartsWith("sget", System.StringComparison.Ordinal);
            var f = FieldRefAt(ops, isStatic ? 1 : 2);
            if (f is null)
            {
                item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
                return item;
            }

            var a = IntAt(ops, 0);
            var b = isStatic ? null : IntAt(ops, 1);
            if (a is int ra)
            {
                var target = isStatic ? null : RegValue(state, ctx, b ?? 0);
                SetReg(state, ra, new FieldExpr(target, f.OwnerClass, f.Name, f.Type, isStatic));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }


        if (m.StartsWith("iput", System.StringComparison.Ordinal) || m.StartsWith("sput", System.StringComparison.Ordinal))
        {
            var isStatic = m.StartsWith("sput", System.StringComparison.Ordinal);
            var f = FieldRefAt(ops, isStatic ? 1 : 2);
            if (f is null)
            {
                item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
                return item;
            }

            var a = IntAt(ops, 0);
            var b = isStatic ? null : IntAt(ops, 1);
            if (a is int ra)
            {
                var target = new FieldExpr(isStatic ? null : RegValue(state, ctx, b ?? 0), f.OwnerClass, f.Name, f.Type, isStatic);
                item.Stmt = new AssignStmt(target, RegValue(state, ctx, ra)) { Offset = insn.Offset };
            }

            return item;
        }


        if (m.StartsWith("aget", System.StringComparison.Ordinal))
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            var c = IntAt(ops, 2);
            if (a is int ra && b is int rb && c is int rc)
            {
                SetReg(state, ra, new ArrayAccessExpr(ElementTypeOf(m), RegValue(state, ctx, rb), RegValue(state, ctx, rc)));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }

        if (m.StartsWith("aput", System.StringComparison.Ordinal))
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            var c = IntAt(ops, 2);
            if (a is int ra && b is int rb && c is int rc)
            {
                item.Stmt = new AssignStmt(
                    new ArrayAccessExpr(ElementTypeOf(m), RegValue(state, ctx, rb), RegValue(state, ctx, rc)),
                    RegValue(state, ctx, ra))
                { Offset = insn.Offset };
            }

            return item;
        }

        if (m == "array-length")
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            if (a is int ra && b is int rb)
            {
                SetReg(state, ra, new FieldExpr(RegValue(state, ctx, rb), string.Empty, "length", "int", isStatic: false));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }


        if (m == "new-instance")
        {
            var a = IntAt(ops, 0);
            var t = TypeRefAt(ops, 1);
            if (a is int ra && t is not null)
            {
                var created = new NewInstanceExpr(DexFile.FormatDescriptor(t.Descriptor));
                SetReg(state, ra, created);
                state.PendingNew = created;
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }

        if (m == "new-array")
        {
            var a = IntAt(ops, 0);
            var t = TypeRefAt(ops, 1);
            if (a is int ra && t is not null)
            {
                state.PendingResult = new NewArrayExpr(DexFile.FormatDescriptor(t.Descriptor), RegValue(state, ctx, ra));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }

        if (m is "filled-new-array" or "filled-new-array/range")
        {
            var regs = RegsAt(ops, 0);
            var t = TypeRefAt(ops, 1);
            if (regs is not null && t is not null)
            {
                var formatted = DexFile.FormatDescriptor(t.Descriptor);
                var elementType = formatted.EndsWith("[]", System.StringComparison.Ordinal) ? formatted[..^2] : formatted;
                var created = new NewArrayExpr(elementType, size: null);
                foreach (var r in regs)
                {
                    created.Initializers.Add(RegValue(state, ctx, r));
                }

                state.PendingResult = created;
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }

        if (m == "fill-array-data")
        {
            item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
            return item;
        }


        if (m.StartsWith("invoke-", System.StringComparison.Ordinal)
            && !m.Contains("polymorphic", System.StringComparison.Ordinal)
            && !m.Contains("custom", System.StringComparison.Ordinal)
            && !m.Contains("object-init", System.StringComparison.Ordinal))
        {
            var regs = RegsAt(ops, 0);
            var methodRef = MethodRefAt(ops, 1);
            if (regs is null || methodRef is null)
            {
                item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
                return item;
            }

            var isStatic = m.Contains("static", System.StringComparison.Ordinal);
            var isSuper = m.Contains("super", System.StringComparison.Ordinal);
            var isInit = methodRef.Name == "<init>";
            var receiver = regs.Length > 0 ? RegValue(state, ctx, regs[0]) : null;
            var argStart = isStatic ? 0 : 1;

            if (isInit)
            {
                if (receiver is NewInstanceExpr pending && ReferenceEquals(state.PendingNew, pending))
                {


                    pending.Args = ResolveArgs(regs, argStart, methodRef, state, ctx, resolver);
                    state.PendingNew = null;
                }
                else if (receiver is LocalExpr { Name: "this" })
                {
                    var isThisCall = ctx.ClassName == methodRef.OwnerClass;
                    item.Stmt = new ExprStmt(new ConstructorCallExpr(isThisCall, ResolveArgs(regs, argStart, methodRef, state, ctx, resolver)))
                    {
                        Offset = insn.Offset
                    };
                }
                else
                {
                    item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
                }

                return item;
            }

            var call = new MethodCallExpr(
                isStatic ? null : receiver,
                methodRef.OwnerClass,
                methodRef.Name,
                methodRef.ReturnType,
                ResolveArgs(regs, argStart, methodRef, state, ctx, resolver),
                isStatic,
                isSuperCall: isSuper && receiver is LocalExpr { Name: "this" });
            item.Stmt = new ExprStmt(call) { Offset = insn.Offset };
            if (methodRef.ReturnType != "void") state.PendingResult = call;
            return item;
        }


        if (m == "return-void")
        {
            item.Stmt = new ReturnStmt(null) { Offset = insn.Offset };
            return item;
        }

        if (m is "return" or "return-wide" or "return-object")
        {
            var a = IntAt(ops, 0);
            item.Stmt = new ReturnStmt(a is int ra ? RegValue(state, ctx, ra) : null) { Offset = insn.Offset };
            return item;
        }

        if (m == "throw")
        {
            var a = IntAt(ops, 0);
            item.Stmt = new ThrowStmt(a is int ra ? RegValue(state, ctx, ra) : new UnknownExpr("?")) { Offset = insn.Offset };
            return item;
        }


        if (m.StartsWith("if-", System.StringComparison.Ordinal))
        {
            if (m.EndsWith("z", System.StringComparison.Ordinal))
            {
                var a = IntAt(ops, 0);
                if (a is int ra)
                {
                    item.IfCondition = new BinaryExpr(IfOpZ(m), RegValue(state, ctx, ra), new LiteralExpr(0));
                }
            }
            else
            {
                var a = IntAt(ops, 0);
                var b = IntAt(ops, 1);
                if (a is int ra && b is int rb)
                {
                    item.IfCondition = new BinaryExpr(IfOp(m), RegValue(state, ctx, ra), RegValue(state, ctx, rb));
                }
            }

            return item;
        }

        if (m is "packed-switch" or "sparse-switch")
        {
            var a = IntAt(ops, 0);
            if (a is int ra)
            {
                item.SwitchValue = RegValue(state, ctx, ra);
            }

            return item;
        }


        var op = ArithOp(m);
        if (op is not null)
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            JavaExpr left;
            JavaExpr right;
            if (m.Contains("/lit8", System.StringComparison.Ordinal) || m.Contains("/lit16", System.StringComparison.Ordinal))
            {
                var lit = LiteralAt(ops, 2);
                left = b is int rb ? RegValue(state, ctx, rb) : new UnknownExpr("?");
                right = lit is not null ? new LiteralExpr(lit.Value) : new UnknownExpr("?");
            }
            else if (m.Contains("/2addr", System.StringComparison.Ordinal))
            {
                left = a is int ra ? RegValue(state, ctx, ra) : new UnknownExpr("?");
                right = b is int rb ? RegValue(state, ctx, rb) : new UnknownExpr("?");
            }
            else
            {
                var c = IntAt(ops, 2);
                left = b is int rb ? RegValue(state, ctx, rb) : new UnknownExpr("?");
                right = c is int rc ? RegValue(state, ctx, rc) : new UnknownExpr("?");
            }

            if (a is int raSet)
            {
                SetReg(state, raSet, new BinaryExpr(op, left, right));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }

        if (m is "rsub-int" or "rsub-int/lit8")
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            var lit = LiteralAt(ops, 2);
            if (a is int ra && lit is not null)
            {
                SetReg(state, ra, new BinaryExpr("-", new LiteralExpr(lit.Value), b is int rb ? RegValue(state, ctx, rb) : new UnknownExpr("?")));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }


        if (m is "neg-int" or "neg-long" or "neg-float" or "neg-double" or "not-int" or "not-long")
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            if (a is int ra && b is int rb)
            {
                SetReg(state, ra, new UnaryExpr(m.StartsWith("not", System.StringComparison.Ordinal) ? "~" : "-", RegValue(state, ctx, rb)));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }


        if (m.Contains("-to-", System.StringComparison.Ordinal))
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            if (a is int ra && b is int rb)
            {
                var target = m[(m.IndexOf("-to-", System.StringComparison.Ordinal) + 4)..];
                SetReg(state, ra, new CastExpr(target, RegValue(state, ctx, rb)));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }


        if (m is "cmp-long" or "cmpl-float" or "cmpg-float" or "cmpl-double" or "cmpg-double")
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            var c = IntAt(ops, 2);
            if (a is int ra && b is int rb && c is int rc)
            {
                var owner = m.Contains("long", System.StringComparison.Ordinal) ? "java.lang.Long"
                    : m.Contains("float", System.StringComparison.Ordinal) ? "java.lang.Float"
                    : "java.lang.Double";
                var compare = new MethodCallExpr(null, owner, "compare", "int",
                    [RegValue(state, ctx, rb), RegValue(state, ctx, rc)], isStatic: true);
                SetReg(state, ra, compare);
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }


        if (m == "check-cast")
        {
            var a = IntAt(ops, 0);
            var t = TypeRefAt(ops, 1);
            if (a is int ra && t is not null)
            {
                SetReg(state, ra, new CastExpr(DexFile.FormatDescriptor(t.Descriptor), RegValue(state, ctx, ra)));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }

        if (m == "instance-of")
        {
            var a = IntAt(ops, 0);
            var b = IntAt(ops, 1);
            var t = TypeRefAt(ops, 2);
            if (a is int ra && b is int rb && t is not null)
            {
                SetReg(state, ra, new InstanceOfExpr(RegValue(state, ctx, rb), DexFile.FormatDescriptor(t.Descriptor)));
            }

            item.Stmt = GuideComment(insn, options);
            return item;
        }


        if (m == "goto" || m == "goto/16" || m == "goto/32" || m == "nop")
        {
            return item;
        }

        item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
        return item;
    }

    /// <summary>
    /// Resolves call arguments, applying explicit casts per the overload resolution result.
    /// <para>解析调用实参：按重载解析结果对实参施加显式转型。</para>
    /// </summary>
    private static List<JavaExpr> ResolveArgs(int[] regs, int argStart, MethodRefOperand methodRef, State state, MethodContext ctx, MethodResolver? resolver)
    {
        var raw = new List<JavaExpr>(regs.Length - argStart);
        for (var i = argStart; i < regs.Length; i++)
        {
            raw.Add(RegValue(state, ctx, regs[i]));
        }

        if (resolver is null || raw.Count == 0) return raw;

        var inferred = new string?[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            inferred[i] = InferType(raw[i]);
        }

        var resolved = resolver.Resolve(methodRef, inferred);
        if (resolved is null || !resolved.Changed) return raw;

        var args = new List<JavaExpr>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var arg = raw[i];
            if (i < resolved.NeedsCast.Length && resolved.NeedsCast[i] && i < resolved.Candidate.Parameters.Length)
            {
                args.Add(new CastExpr(resolved.Candidate.Parameters[i], arg));
            }
            else
            {
                args.Add(arg);
            }
        }

        return args;
    }

    private static void SetReg(State state, int reg, JavaExpr value)
    {
        state.Regs[reg] = value;
        state.Regs.Remove(reg + 1);
    }

    private static JavaExpr RegValue(State state, MethodContext ctx, int reg)
        => state.Regs.TryGetValue(reg, out var value) ? value : new UnknownExpr(RegisterName(ctx, reg));

    private static int? IntAt(IReadOnlyList<object?> ops, int i)
    {
        if (ops.Count <= i) return null;
        return ops[i] switch
        {
            int v => v,
            byte b => b,
            ushort u => u,
            _ => null
        };
    }

    private static LiteralOperand? LiteralAt(IReadOnlyList<object?> ops, int i) => ops.Count > i ? ops[i] as LiteralOperand : null;

    private static FieldRefOperand? FieldRefAt(IReadOnlyList<object?> ops, int i) => ops.Count > i ? ops[i] as FieldRefOperand : null;

    private static MethodRefOperand? MethodRefAt(IReadOnlyList<object?> ops, int i) => ops.Count > i ? ops[i] as MethodRefOperand : null;

    private static TypeRefOperand? TypeRefAt(IReadOnlyList<object?> ops, int i) => ops.Count > i ? ops[i] as TypeRefOperand : null;

    private static int[]? RegsAt(IReadOnlyList<object?> ops, int i) => ops.Count > i ? ops[i] as int[] : null;

    private static string ElementTypeOf(string m) => m switch
    {
        "aget" or "aput" => "int",
        "aget-wide" or "aput-wide" => "long",
        "aget-object" or "aput-object" => "java.lang.Object",
        "aget-boolean" or "aput-boolean" => "boolean",
        "aget-byte" or "aput-byte" => "byte",
        "aget-char" or "aput-char" => "char",
        "aget-short" or "aput-short" => "short",
        _ => "int"
    };

    private static string IfOp(string m) => m switch
    {
        "if-eq" => "==",
        "if-ne" => "!=",
        "if-lt" => "<",
        "if-ge" => ">=",
        "if-gt" => ">",
        "if-le" => "<=",
        _ => "=="
    };

    private static string IfOpZ(string m) => m switch
    {
        "if-eqz" => "==",
        "if-nez" => "!=",
        "if-ltz" => "<",
        "if-gez" => ">=",
        "if-gtz" => ">",
        "if-lez" => "<=",
        _ => "=="
    };

    private static string? ArithOp(string m)
    {
        var baseName = m.Contains('/', System.StringComparison.Ordinal) ? m[..m.IndexOf('/')] : m;
        var dash = baseName.IndexOf('-');
        if (dash <= 0) return null;
        var name = baseName[..dash];
        return name switch
        {
            "add" => "+",
            "sub" => "-",
            "mul" => "*",
            "div" => "/",
            "rem" => "%",
            "and" => "&",
            "or" => "|",
            "xor" => "^",
            "shl" => "<<",
            "shr" => ">>",
            "ushr" => ">>>",
            _ => null
        };
    }

    private static JavaStmt? GuideComment(Instruction insn, DecompileOptions options)
        => options.Mode == DecompileOutputMode.BytecodeGuide
            ? new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset }
            : null;

    /// <summary>
    /// Renders an instruction as Smali-style text (for comments).
    /// <para>把指令渲染为 Smali 风格文本（用于注释）。</para>
    /// </summary>
    private static string FormatRaw(Instruction insn)
    {
        var sb = new StringBuilder();
        sb.Append(insn.Mnemonic);
        if (insn.Operands.Count == 0) return sb.ToString();

        sb.Append(' ');
        var ops = insn.Operands;
        if (ops[0] is int[] regs)
        {
            sb.Append('{');
            for (var i = 0; i < regs.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('v').Append(regs[i]);
            }

            sb.Append('}');
            for (var i = 1; i < ops.Count; i++)
            {
                sb.Append(", ");
                AppendRaw(sb, ops[i]);
            }

            return sb.ToString();
        }

        for (var i = 0; i < ops.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            AppendRaw(sb, ops[i]);
        }

        return sb.ToString();
    }

    private static void AppendRaw(StringBuilder sb, object? operand)
    {
        switch (operand)
        {
            case null:
                sb.Append("<null>");
                break;
            case BranchTargetOperand target:
                sb.Append(":label_").Append(target.Offset.ToString("X4"));
                break;
            case LiteralOperand literal:
                sb.Append('#').Append(literal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case TypeRefOperand type:
                sb.Append(type.Descriptor);
                break;
            case FieldRefOperand field:
                sb.Append(field.OwnerClass).Append("->").Append(field.Name).Append(':').Append(field.Type);
                break;
            case MethodRefOperand method:
                sb.Append(method.OwnerClass).Append("->").Append(method.Name).Append('(');
                for (var i = 0; i < method.Parameters.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(method.Parameters[i]);
                }

                sb.Append(')').Append(method.ReturnType);
                break;
            case PayloadOperand payload:
                sb.Append(payload.Kind).Append('(');
                for (var i = 0; i < payload.Values.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(payload.Values[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                sb.Append(')');
                break;
            case int reg:
                sb.Append('v').Append(reg);
                break;
            case byte reg:
                sb.Append('v').Append(reg);
                break;
            case ushort reg:
                sb.Append('v').Append(reg);
                break;
            case string value:
                sb.Append('"').Append(value).Append('"');
                break;
            default:
                sb.Append(System.Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture) ?? "?");
                break;
        }
    }
}
