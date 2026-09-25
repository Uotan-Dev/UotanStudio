using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

/// <summary>Immutable facts about one encoded method used while decompiling its register stream.
/// <para>反编译方法寄存器流时使用的、关于单个已编码方法的不可变事实。</para></summary>
public sealed class MethodContext
{
    /// <summary>Dex file that owns the method. <para>方法所属的已解析 DEX 文件。</para></summary>
    /// <value>Required; used to decode names, types and identifiers.
    /// <para>必填；用于解码名称、类型与标识。</para></value>
    public required DexFile Dex { get; init; }

    /// <summary>Encoded method holding the access flags and code item.
    /// <para>承载访问标志与代码项的已编码方法。</para></summary>
    /// <value>Required; <see cref="RegistersSize"/> and <see cref="InsSize"/> derive from its code item.
    /// <para>必填；<see cref="RegistersSize"/> 与 <see cref="InsSize"/> 取自其代码项。</para></value>
    public required EncodedMethod Method { get; init; }

    /// <summary>Class definition that declares the method. <para>声明该方法的类定义。</para></summary>
    /// <value>Nullable; <see langword="null"/> when the declaring class is unknown to the caller.
    /// <para>可空；调用方无法确定声明类时为 <see langword="null"/>。</para></value>
    public ClassDefinition? Class { get; init; }

    /// <summary>Method identifier record selected by the method index.
    /// <para>按方法索引取出的方法标识记录。</para></summary>
    /// <value>Required; carries the name, prototype and class indices.
    /// <para>必填；包含名称、原型与类索引。</para></value>
    public required MethodId MethodId { get; init; }

    /// <summary>Prototype record describing the parameter and return types.
    /// <para>描述参数与返回类型的原型记录。</para></summary>
    /// <value><see langword="null"/> when the prototype index does not resolve.
    /// <para>原型索引无法解析时为 <see langword="null"/>。</para></value>
    public ProtoDefinition? Proto { get; init; }

    /// <summary>Decoded method name. <para>解码后的方法名。</para></summary>
    /// <value>Required; <c>&lt;init&gt;</c> marks a constructor.
    /// <para>必填；<c>&lt;init&gt;</c> 表示构造器。</para></value>
    public required string MethodName { get; init; }

    /// <summary>Human-readable return type of the method. <para>方法返回类型的可读形式。</para></summary>
    /// <value>Required; <c>?</c> when the prototype does not resolve.
    /// <para>必填；原型无法解析时为 <c>?</c>。</para></value>
    public required string ReturnType { get; init; }

    /// <summary>Human-readable parameter types in declaration order.
    /// <para>按声明顺序排列的参数可读类型。</para></summary>
    /// <value>Required; empty when the prototype does not resolve.
    /// <para>必填；原型无法解析时为空数组。</para></value>
    public required string[] ParameterTypes { get; init; }

    /// <summary>Total register count declared by the code item. <para>代码项声明的寄存器总数。</para></summary>
    /// <value><c>0</c> when the method has no code item; the boundary between locals and arguments.
    /// <para>方法无代码项时为 <c>0</c>；是局部寄存器与参数寄存器的分界。</para></value>
    public int RegistersSize { get; init; }

    /// <summary>Number of incoming argument registers. <para>入参寄存器的数量。</para></summary>
    /// <value><c>0</c> when the method has no code item; instance methods reserve the lowest argument slot for <c>this</c>.
    /// <para>方法无代码项时为 <c>0</c>；实例方法会占用最低的参数槽位作为 <c>this</c>。</para></value>
    public int InsSize { get; init; }

    /// <summary>Whether the method carries the static access flag. <para>方法是否带有 static 访问标志。</para></summary>
    /// <value>Static methods have no <c>this</c> register. <para>static 方法没有 <c>this</c> 寄存器。</para></value>
    public bool IsStatic { get; init; }

    /// <summary>Whether the method is an instance constructor. <para>方法是否为实例构造器。</para></summary>
    /// <value>True when the decoded name equals <c>&lt;init&gt;</c>. <para>解码名称等于 <c>&lt;init&gt;</c> 时为 true。</para></value>
    public bool IsConstructor { get; init; }

    /// <summary>Human-readable name of the declaring class. <para>声明类的可读名称。</para></summary>
    /// <value><see langword="null"/> when <see cref="Class"/> is <see langword="null"/>.
    /// <para><see cref="Class"/> 为 <see langword="null"/> 时本值也为 <see langword="null"/>。</para></value>
    public string? ClassName { get; init; }

    /// <summary>
    /// Builds a method context from the model.
    /// <para>从模型构造方法上下文。</para>
    /// </summary>
    /// <param name="dex">Dex file used to resolve the method, prototype, names and types.
    /// <para>用于解析方法、原型、名称与类型的 DEX 文件。</para></param>
    /// <param name="method">Encoded method to describe. <para>待描述方法的已编码形式。</para></param>
    /// <param name="cls">Declaring class, or <see langword="null"/> when unknown.
    /// <para>声明类；未知时为 <see langword="null"/>。</para></param>
    /// <returns>A context with names, types and register geometry decoded from the model.
    /// <para>已从模型解码出名称、类型与寄存器几何信息的上下文。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> or <paramref name="method"/> is null.
    /// <para><paramref name="dex"/> 或 <paramref name="method"/> 为 null。</para></exception>
    /// <exception cref="DexException">The method index is out of range in the dex.
    /// <para>方法索引超出 DEX 范围。</para></exception>
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

/// <summary>Per-instruction result of the linear register analysis.
/// <para>线性寄存器分析的逐指令结果。</para></summary>
public sealed class LinearItem
{
    /// <summary>Instruction this item describes. <para>本条目对应的指令。</para></summary>
    /// <value>Read-only; assigned by the constructor. <para>只读；由构造函数赋值。</para></value>
    public Instruction Insn { get; }

    /// <summary>Java statement produced for this instruction.
    /// <para>为本指令生成的 Java 语句。</para></summary>
    /// <value>Writable hint for the code emitter; <see langword="null"/> when the instruction emits no statement.
    /// <para>供代码生成使用的可写提示；指令不产生语句时为 <see langword="null"/>。</para></value>
    public JavaStmt? Stmt { get; set; }

    /// <summary>Condition reconstructed for an if-test instruction.
    /// <para>为 if 判断指令重建的条件表达式。</para></summary>
    /// <value><see langword="null"/> unless the instruction is an if-test.
    /// <para>非 if 判断指令时为 <see langword="null"/>。</para></value>
    public JavaExpr? IfCondition { get; set; }

    /// <summary>Selector expression reconstructed for a switch instruction.
    /// <para>为 switch 指令重建的选择子表达式。</para></summary>
    /// <value><see langword="null"/> unless the instruction is a packed- or sparse-switch.
    /// <para>非 packed/sparse-switch 指令时为 <see langword="null"/>。</para></value>
    public JavaExpr? SwitchValue { get; set; }

    /// <summary>
    /// Creates a linear analysis item.
    /// <para>创建线性分析条目。</para>
    /// </summary>
    /// <param name="insn">Instruction the item carries. <para>条目所携带的指令。</para></param>
    public LinearItem(Instruction insn) => Insn = insn;
}

/// <summary>Linear pass over one method: per-instruction items plus the shared method context.
/// <para>对单个方法的线性遍历：逐指令条目与共享的方法上下文。</para></summary>
public sealed class LinearAnalysis
{
    /// <summary>Analysed items in original instruction order.
    /// <para>按原始指令顺序排列的分析条目。</para></summary>
    /// <value>One <see cref="LinearItem"/> per instruction; grows during the analysis pass.
    /// <para>每条指令对应一个 <see cref="LinearItem"/>；分析过程中会追加。</para></value>
    public List<LinearItem> Items { get; } = [];

    /// <summary>Method the items belong to. <para>这些条目所属的方法上下文。</para></summary>
    /// <value>Read-only; assigned by the constructor. <para>只读；由构造函数赋值。</para></value>
    public MethodContext Context { get; }

    /// <summary>
    /// Creates a linear analysis result.
    /// <para>创建线性分析结果。</para>
    /// </summary>
    /// <param name="context">Method context shared by all items. <para>所有条目共享的方法上下文。</para></param>
    public LinearAnalysis(MethodContext context) => Context = context;
}

/// <summary>Symbolic pass that maps Dalvik registers to Java expressions, statements, if conditions and switch selectors.
/// <para>对寄存器做符号执行的遍历，把 Dalvik 寄存器映射为 Java 表达式、语句、if 条件与 switch 选择子。</para></summary>
public static class RegisterAnalyzer
{
    /// <summary>
    /// Analyzes a method's instruction stream.
    /// <para>分析方法的指令流。</para>
    /// </summary>
    /// <param name="ctx">Method context supplying names and register geometry.
    /// <para>提供名称与寄存器几何信息的方法上下文。</para></param>
    /// <param name="instructions">Decoded instructions in execution order.
    /// <para>按执行顺序解码的指令。</para></param>
    /// <param name="resolver">Overload resolver used to insert argument casts; <see langword="null"/> skips overload resolution.
    /// <para>用于插入实参转型的重载解析器；为 <see langword="null"/> 时不做重载解析。</para></param>
    /// <param name="options">Emission options; <see langword="null"/> uses <c>DecompileOptions.Default</c>.
    /// <para>输出选项；为 <see langword="null"/> 时使用 <c>DecompileOptions.Default</c>。</para></param>
    /// <returns>Linear analysis whose items mirror the input order, one per instruction.
    /// <para>结果条目顺序与输入一致，每条指令一项。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="ctx"/> or <paramref name="instructions"/> is null.
    /// <para><paramref name="ctx"/> 或 <paramref name="instructions"/> 为 null。</para></exception>
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
    /// <param name="ctx">Method context supplying the register geometry and static flag.
    /// <para>提供寄存器几何与 static 标志的方法上下文。</para></param>
    /// <param name="reg">Raw Dalvik register number; values at or above <c>RegistersSize - InsSize</c> are argument slots.
    /// <para>原始 Dalvik 寄存器编号；不小于 <c>RegistersSize - InsSize</c> 的为参数槽位。</para></param>
    /// <returns><c>this</c> for the lowest argument slot of an instance method, <c>pN</c> for other argument slots, otherwise <c>vN</c>.
    /// <para>实例方法最低参数槽位返回 <c>this</c>，其余参数槽位返回 <c>pN</c>，否则返回 <c>vN</c>。</para></returns>
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
    /// <param name="expr">Expression to classify; <see langword="null"/> yields no hint.
    /// <para>待判定的表达式；为 <see langword="null"/> 时无提示。</para></param>
    /// <returns>Readable type name — literals collapse to <c>int</c> or <c>long</c>, arrays to element type plus <c>[]</c>, unary/ternary forms delegate to their operands — or <see langword="null"/> when the hint cannot be recovered.
    /// <para>可读类型名——字面量归为 <c>int</c> 或 <c>long</c>，数组为元素类型加 <c>[]</c>，一元与三元形式递归取操作数——无法恢复时返回 <see langword="null"/>。</para></returns>
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