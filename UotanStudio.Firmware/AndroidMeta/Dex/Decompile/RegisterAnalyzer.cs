using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Decompile;

/// <summary>Immutable facts about one encoded method used while decompiling its register stream.
/// <para>反编译方法寄存器流时使用的、关于单个已编码方法的不可变事实。</para></summary>
internal sealed class MethodContext
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
internal sealed class LinearItem
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
internal sealed class LinearAnalysis
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
internal static class RegisterAnalyzer
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
        var mnemonic = insn.Mnemonic;

        if (mnemonic is "const/4" or "const/16" or "const" or "const/high16"
            or "const-wide/16" or "const-wide/32" or "const-wide" or "const-wide/high16")
        {
            return ProcessConstFamily(insn, state, options, item);
        }

        if (mnemonic is "const-string" or "const-string/jumbo")
        {
            return ProcessConstString(insn, state, options, item);
        }

        if (mnemonic == "const-class")
        {
            return ProcessConstClass(insn, state, options, item);
        }

        if (mnemonic is "move-result" or "move-result-wide" or "move-result-object")
        {
            return ProcessMoveResult(insn, state, item);
        }

        if (mnemonic == "move-exception")
        {
            return ProcessMoveException(insn, ctx, state, item);
        }

        if (mnemonic == "move" || mnemonic == "move/from16" || mnemonic == "move/16"
            || mnemonic == "move-wide" || mnemonic == "move-wide/from16" || mnemonic == "move-wide/16"
            || mnemonic == "move-object" || mnemonic == "move-object/from16" || mnemonic == "move-object/16")
        {
            return ProcessMove(insn, ctx, state, item);
        }

        if (mnemonic.StartsWith("iget", System.StringComparison.Ordinal) || mnemonic.StartsWith("sget", System.StringComparison.Ordinal))
        {
            return ProcessFieldGet(insn, ctx, state, options, item);
        }

        if (mnemonic.StartsWith("iput", System.StringComparison.Ordinal) || mnemonic.StartsWith("sput", System.StringComparison.Ordinal))
        {
            return ProcessFieldPut(insn, ctx, state, item);
        }

        if (mnemonic.StartsWith("aget", System.StringComparison.Ordinal))
        {
            return ProcessArrayGet(insn, ctx, state, options, item);
        }

        if (mnemonic.StartsWith("aput", System.StringComparison.Ordinal))
        {
            return ProcessArrayPut(insn, ctx, state, item);
        }

        if (mnemonic == "array-length")
        {
            return ProcessArrayLength(insn, ctx, state, options, item);
        }

        if (mnemonic == "new-instance")
        {
            return ProcessNewInstance(insn, state, options, item);
        }

        if (mnemonic == "new-array")
        {
            return ProcessNewArray(insn, ctx, state, options, item);
        }

        if (mnemonic is "filled-new-array" or "filled-new-array/range")
        {
            return ProcessFilledNewArray(insn, ctx, state, options, item);
        }

        if (mnemonic == "fill-array-data")
        {
            return ProcessFillArrayData(insn, item);
        }

        if (mnemonic.StartsWith("invoke-", System.StringComparison.Ordinal)
            && !mnemonic.Contains("polymorphic", System.StringComparison.Ordinal)
            && !mnemonic.Contains("custom", System.StringComparison.Ordinal)
            && !mnemonic.Contains("object-init", System.StringComparison.Ordinal))
        {
            return ProcessInvoke(insn, ctx, state, resolver, item);
        }

        if (mnemonic == "return-void")
        {
            return ProcessReturnVoid(insn, item);
        }

        if (mnemonic is "return" or "return-wide" or "return-object")
        {
            return ProcessReturn(insn, ctx, state, item);
        }

        if (mnemonic == "throw")
        {
            return ProcessThrow(insn, ctx, state, item);
        }

        if (mnemonic.StartsWith("if-", System.StringComparison.Ordinal))
        {
            return ProcessIf(insn, ctx, state, item);
        }

        if (mnemonic is "packed-switch" or "sparse-switch")
        {
            return ProcessSwitch(insn, ctx, state, item);
        }

        var operatorSymbol = ArithOp(mnemonic);
        if (operatorSymbol is not null)
        {
            return ProcessArith(insn, ctx, state, operatorSymbol, options, item);
        }

        if (mnemonic is "rsub-int" or "rsub-int/lit8")
        {
            return ProcessReverseSubtract(insn, ctx, state, options, item);
        }

        if (mnemonic is "neg-int" or "neg-long" or "neg-float" or "neg-double" or "not-int" or "not-long")
        {
            return ProcessUnary(insn, ctx, state, options, item);
        }

        if (mnemonic.Contains("-to-", System.StringComparison.Ordinal))
        {
            return ProcessNumericCast(insn, ctx, state, options, item);
        }

        if (mnemonic is "cmp-long" or "cmpl-float" or "cmpg-float" or "cmpl-double" or "cmpg-double")
        {
            return ProcessCompare(insn, ctx, state, options, item);
        }

        if (mnemonic == "check-cast")
        {
            return ProcessCheckCast(insn, ctx, state, options, item);
        }

        if (mnemonic == "instance-of")
        {
            return ProcessInstanceOf(insn, ctx, state, options, item);
        }

        if (mnemonic == "goto" || mnemonic == "goto/16" || mnemonic == "goto/32" || mnemonic == "nop")
        {
            return item;
        }

        item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
        return item;
    }

    /// <summary>Stores a const or const-wide literal into its destination register.
    /// <para>把 const 或 const-wide 字面量存入目标寄存器。</para></summary>
    private static LinearItem ProcessConstFamily(Instruction insn, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var literal = LiteralAt(operands, 1);
        if (dest is int targetRegister && literal is not null)
        {
            SetReg(state, targetRegister, new LiteralExpr(literal.Value));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Stores a const-string literal into its destination register.
    /// <para>把 const-string 字符串常量存入目标寄存器。</para></summary>
    private static LinearItem ProcessConstString(Instruction insn, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var stringValue = operands.Count > 1 ? operands[1] as string : null;
        if (dest is int targetRegister)
        {
            SetReg(state, targetRegister, new StringExpr(stringValue ?? string.Empty));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Stores a const-class class literal into its destination register.
    /// <para>把 const-class 的类字面量存入目标寄存器。</para></summary>
    private static LinearItem ProcessConstClass(Instruction insn, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var typeRef = TypeRefAt(operands, 1);
        if (dest is int targetRegister && typeRef is not null)
        {
            SetReg(state, targetRegister, new ClassLiteralExpr(DexFile.FormatDescriptor(typeRef.Descriptor)));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Consumes the pending invoke result into the destination register of a move-result instruction.
    /// <para>把待用的调用结果消费到 move-result 指令的目标寄存器。</para></summary>
    private static LinearItem ProcessMoveResult(Instruction insn, State state, LinearItem item)
    {
        var dest = IntAt(insn.Operands, 0);
        if (dest is int targetRegister && state.PendingResult is not null)
        {
            SetReg(state, targetRegister, state.PendingResult);
            if (insn.Mnemonic == "move-result-wide")
            {
                state.Regs.Remove(targetRegister + 1);
            }

            state.PendingResult = null;
        }

        return item;
    }

    /// <summary>Binds the caught throwable to the destination register of move-exception.
    /// <para>把捕获的异常对象绑定到 move-exception 的目标寄存器。</para></summary>
    private static LinearItem ProcessMoveException(Instruction insn, MethodContext ctx, State state, LinearItem item)
    {
        var dest = IntAt(insn.Operands, 0);
        if (dest is int targetRegister)
        {
            SetReg(state, targetRegister, new LocalExpr(RegisterName(ctx, targetRegister), "java.lang.Throwable"));
        }

        item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
        return item;
    }

    /// <summary>Copies a source register into the destination register for the move families.
    /// <para>对 move 族指令把源寄存器复制到目标寄存器。</para></summary>
    private static LinearItem ProcessMove(Instruction insn, MethodContext ctx, State state, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var source = IntAt(operands, 1);
        if (dest is int targetRegister && source is int sourceRegister)
        {
            var value = RegValue(state, ctx, sourceRegister);
            SetReg(state, targetRegister, value);
            if (insn.Mnemonic.StartsWith("move-wide", System.StringComparison.Ordinal))
            {
                state.Regs.Remove(targetRegister + 1);
            }

            item.Stmt = new AssignStmt(new LocalExpr(RegisterName(ctx, targetRegister)), value) { Offset = insn.Offset };
        }

        return item;
    }

    /// <summary>Reads an instance or static field into the destination register for the iget/sget families.
    /// <para>对 iget/sget 族指令把实例或静态字段读入目标寄存器。</para></summary>
    private static LinearItem ProcessFieldGet(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var mnemonic = insn.Mnemonic;
        var operands = insn.Operands;
        var isStatic = mnemonic.StartsWith("sget", System.StringComparison.Ordinal);
        var fieldRef = FieldRefAt(operands, isStatic ? 1 : 2);
        if (fieldRef is null)
        {
            item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
            return item;
        }

        var dest = IntAt(operands, 0);
        var objectRegister = isStatic ? null : IntAt(operands, 1);
        if (dest is int targetRegister)
        {
            var receiver = isStatic ? null : RegValue(state, ctx, objectRegister ?? 0);
            SetReg(state, targetRegister, new FieldExpr(receiver, fieldRef.OwnerClass, fieldRef.Name, fieldRef.Type, isStatic));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Stores a register value into an instance or static field for the iput/sput families.
    /// <para>对 iput/sput 族指令把寄存器值存入实例或静态字段。</para></summary>
    private static LinearItem ProcessFieldPut(Instruction insn, MethodContext ctx, State state, LinearItem item)
    {
        var mnemonic = insn.Mnemonic;
        var operands = insn.Operands;
        var isStatic = mnemonic.StartsWith("sput", System.StringComparison.Ordinal);
        var fieldRef = FieldRefAt(operands, isStatic ? 1 : 2);
        if (fieldRef is null)
        {
            item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
            return item;
        }

        var value = IntAt(operands, 0);
        var objectRegister = isStatic ? null : IntAt(operands, 1);
        if (value is int valueRegister)
        {
            var target = new FieldExpr(isStatic ? null : RegValue(state, ctx, objectRegister ?? 0), fieldRef.OwnerClass, fieldRef.Name, fieldRef.Type, isStatic);
            item.Stmt = new AssignStmt(target, RegValue(state, ctx, valueRegister)) { Offset = insn.Offset };
        }

        return item;
    }

    /// <summary>Reads an array element into the destination register for the aget family.
    /// <para>对 aget 族指令把数组元素读入目标寄存器。</para></summary>
    private static LinearItem ProcessArrayGet(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var array = IntAt(operands, 1);
        var index = IntAt(operands, 2);
        if (dest is int targetRegister && array is int arrayRegister && index is int indexRegister)
        {
            SetReg(state, targetRegister, new ArrayAccessExpr(ElementTypeOf(insn.Mnemonic), RegValue(state, ctx, arrayRegister), RegValue(state, ctx, indexRegister)));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Stores a register value into an array element for the aput family.
    /// <para>对 aput 族指令把寄存器值存入数组元素。</para></summary>
    private static LinearItem ProcessArrayPut(Instruction insn, MethodContext ctx, State state, LinearItem item)
    {
        var operands = insn.Operands;
        var value = IntAt(operands, 0);
        var array = IntAt(operands, 1);
        var index = IntAt(operands, 2);
        if (value is int valueRegister && array is int arrayRegister && index is int indexRegister)
        {
            item.Stmt = new AssignStmt(
                new ArrayAccessExpr(ElementTypeOf(insn.Mnemonic), RegValue(state, ctx, arrayRegister), RegValue(state, ctx, indexRegister)),
                RegValue(state, ctx, valueRegister))
            { Offset = insn.Offset };
        }

        return item;
    }

    /// <summary>Records the array length access of array-length into the destination register.
    /// <para>把 array-length 的数组长度访问记录到目标寄存器。</para></summary>
    private static LinearItem ProcessArrayLength(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var array = IntAt(operands, 1);
        if (dest is int targetRegister && array is int arrayRegister)
        {
            SetReg(state, targetRegister, new FieldExpr(RegValue(state, ctx, arrayRegister), string.Empty, "length", "int", isStatic: false));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Records a new-instance object and marks it as the pending constructor receiver.
    /// <para>记录 new-instance 对象并标记为待用的构造器接收者。</para></summary>
    private static LinearItem ProcessNewInstance(Instruction insn, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var typeRef = TypeRefAt(operands, 1);
        if (dest is int targetRegister && typeRef is not null)
        {
            var created = new NewInstanceExpr(DexFile.FormatDescriptor(typeRef.Descriptor));
            SetReg(state, targetRegister, created);
            state.PendingNew = created;
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Parks a new-array expression as the pending result.
    /// <para>把 new-array 表达式暂存为待用结果。</para></summary>
    private static LinearItem ProcessNewArray(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var typeRef = TypeRefAt(operands, 1);
        if (dest is int targetRegister && typeRef is not null)
        {
            state.PendingResult = new NewArrayExpr(DexFile.FormatDescriptor(typeRef.Descriptor), RegValue(state, ctx, targetRegister));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Builds a filled-new-array initializer list and parks it as the pending result.
    /// <para>构建 filled-new-array 的初始化列表并暂存为待用结果。</para></summary>
    private static LinearItem ProcessFilledNewArray(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var registers = RegsAt(operands, 0);
        var typeRef = TypeRefAt(operands, 1);
        if (registers is not null && typeRef is not null)
        {
            var formatted = DexFile.FormatDescriptor(typeRef.Descriptor);
            var elementType = formatted.EndsWith("[]", System.StringComparison.Ordinal) ? formatted[..^2] : formatted;
            var created = new NewArrayExpr(elementType, size: null);
            foreach (var register in registers)
            {
                created.Initializers.Add(RegValue(state, ctx, register));
            }

            state.PendingResult = created;
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Emits fill-array-data as a raw smali comment.
    /// <para>把 fill-array-data 输出为原始 smali 注释。</para></summary>
    private static LinearItem ProcessFillArrayData(Instruction insn, LinearItem item)
    {
        item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
        return item;
    }

    /// <summary>Rebuilds an invoke instruction into a method or constructor call and parks non-void results.
    /// <para>把 invoke 指令重建为方法或构造器调用，并把非 void 结果暂存。</para></summary>
    private static LinearItem ProcessInvoke(Instruction insn, MethodContext ctx, State state, MethodResolver? resolver, LinearItem item)
    {
        var operands = insn.Operands;
        var argRegisters = RegsAt(operands, 0);
        var methodRef = MethodRefAt(operands, 1);
        if (argRegisters is null || methodRef is null)
        {
            item.Stmt = new RawCommentStmt(FormatRaw(insn)) { Offset = insn.Offset };
            return item;
        }

        var mnemonic = insn.Mnemonic;
        var isStatic = mnemonic.Contains("static", System.StringComparison.Ordinal);
        var isSuper = mnemonic.Contains("super", System.StringComparison.Ordinal);
        var isInit = methodRef.Name == "<init>";
        var receiver = argRegisters.Length > 0 ? RegValue(state, ctx, argRegisters[0]) : null;
        var argStart = isStatic ? 0 : 1;

        if (isInit)
        {
            if (receiver is NewInstanceExpr pending && ReferenceEquals(state.PendingNew, pending))
            {
                pending.Args = ResolveArgs(argRegisters, argStart, methodRef, state, ctx, resolver);
                state.PendingNew = null;
            }
            else if (receiver is LocalExpr { Name: "this" })
            {
                var isThisCall = ctx.ClassName == methodRef.OwnerClass;
                item.Stmt = new ExprStmt(new ConstructorCallExpr(isThisCall, ResolveArgs(argRegisters, argStart, methodRef, state, ctx, resolver)))
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
            ResolveArgs(argRegisters, argStart, methodRef, state, ctx, resolver),
            isStatic,
            isSuperCall: isSuper && receiver is LocalExpr { Name: "this" });
        item.Stmt = new ExprStmt(call) { Offset = insn.Offset };
        if (methodRef.ReturnType != "void")
        {
            state.PendingResult = call;
        }

        return item;
    }

    /// <summary>Emits a return statement without a value for return-void.
    /// <para>为 return-void 生成不带值的 return 语句。</para></summary>
    private static LinearItem ProcessReturnVoid(Instruction insn, LinearItem item)
    {
        item.Stmt = new ReturnStmt(null) { Offset = insn.Offset };
        return item;
    }

    /// <summary>Emits a return statement carrying the source register for the return families.
    /// <para>对 return 族指令生成携带源寄存器的 return 语句。</para></summary>
    private static LinearItem ProcessReturn(Instruction insn, MethodContext ctx, State state, LinearItem item)
    {
        var source = IntAt(insn.Operands, 0);
        item.Stmt = new ReturnStmt(source is int sourceRegister ? RegValue(state, ctx, sourceRegister) : null) { Offset = insn.Offset };
        return item;
    }

    /// <summary>Emits a throw statement for the exception object register.
    /// <para>为异常对象寄存器生成 throw 语句。</para></summary>
    private static LinearItem ProcessThrow(Instruction insn, MethodContext ctx, State state, LinearItem item)
    {
        var source = IntAt(insn.Operands, 0);
        item.Stmt = new ThrowStmt(source is int sourceRegister ? RegValue(state, ctx, sourceRegister) : new UnknownExpr("?")) { Offset = insn.Offset };
        return item;
    }

    /// <summary>Rebuilds the comparison condition of an if or if-eqz style branch.
    /// <para>为 if 或 if-eqz 风格分支重建比较条件。</para></summary>
    private static LinearItem ProcessIf(Instruction insn, MethodContext ctx, State state, LinearItem item)
    {
        var mnemonic = insn.Mnemonic;
        var operands = insn.Operands;
        if (mnemonic.EndsWith("z", System.StringComparison.Ordinal))
        {
            var test = IntAt(operands, 0);
            if (test is int testRegister)
            {
                item.IfCondition = new BinaryExpr(IfOpZ(mnemonic), RegValue(state, ctx, testRegister), new LiteralExpr(0));
            }
        }
        else
        {
            var left = IntAt(operands, 0);
            var right = IntAt(operands, 1);
            if (left is int leftRegister && right is int rightRegister)
            {
                item.IfCondition = new BinaryExpr(IfOp(mnemonic), RegValue(state, ctx, leftRegister), RegValue(state, ctx, rightRegister));
            }
        }

        return item;
    }

    /// <summary>Records the selector register value of a packed- or sparse-switch.
    /// <para>记录 packed/sparse-switch 的选择子寄存器值。</para></summary>
    private static LinearItem ProcessSwitch(Instruction insn, MethodContext ctx, State state, LinearItem item)
    {
        var selector = IntAt(insn.Operands, 0);
        if (selector is int selectorRegister)
        {
            item.SwitchValue = RegValue(state, ctx, selectorRegister);
        }

        return item;
    }

    /// <summary>Rebuilds an arithmetic or bitwise operation, including its literal and two-address forms.
    /// <para>重建算术或位运算，包括其字面量与双地址形式。</para></summary>
    private static LinearItem ProcessArith(Instruction insn, MethodContext ctx, State state, string operatorSymbol, DecompileOptions options, LinearItem item)
    {
        var mnemonic = insn.Mnemonic;
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var first = IntAt(operands, 1);
        JavaExpr left;
        JavaExpr right;
        if (mnemonic.Contains("/lit8", System.StringComparison.Ordinal) || mnemonic.Contains("/lit16", System.StringComparison.Ordinal))
        {
            var literal = LiteralAt(operands, 2);
            left = first is int sourceRegister ? RegValue(state, ctx, sourceRegister) : new UnknownExpr("?");
            right = literal is not null ? new LiteralExpr(literal.Value) : new UnknownExpr("?");
        }
        else if (mnemonic.Contains("/2addr", System.StringComparison.Ordinal))
        {
            left = dest is int leftRegister ? RegValue(state, ctx, leftRegister) : new UnknownExpr("?");
            right = first is int sourceRegister ? RegValue(state, ctx, sourceRegister) : new UnknownExpr("?");
        }
        else
        {
            var second = IntAt(operands, 2);
            left = first is int sourceRegister ? RegValue(state, ctx, sourceRegister) : new UnknownExpr("?");
            right = second is int secondRegister ? RegValue(state, ctx, secondRegister) : new UnknownExpr("?");
        }

        if (dest is int destRegister)
        {
            SetReg(state, destRegister, new BinaryExpr(operatorSymbol, left, right));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Rebuilds rsub-int where the literal is the left operand.
    /// <para>重建字面量作为左操作数的 rsub-int。</para></summary>
    private static LinearItem ProcessReverseSubtract(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var source = IntAt(operands, 1);
        var literal = LiteralAt(operands, 2);
        if (dest is int targetRegister && literal is not null)
        {
            SetReg(state, targetRegister, new BinaryExpr("-", new LiteralExpr(literal.Value), source is int sourceRegister ? RegValue(state, ctx, sourceRegister) : new UnknownExpr("?")));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Rebuilds a negation or bitwise-not operation.
    /// <para>重建取负或按位取反运算。</para></summary>
    private static LinearItem ProcessUnary(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var source = IntAt(operands, 1);
        if (dest is int targetRegister && source is int sourceRegister)
        {
            var operatorSymbol = insn.Mnemonic.StartsWith("not", System.StringComparison.Ordinal) ? "~" : "-";
            SetReg(state, targetRegister, new UnaryExpr(operatorSymbol, RegValue(state, ctx, sourceRegister)));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Rebuilds a numeric conversion such as int-to-long as a cast.
    /// <para>把 int-to-long 之类的数值转换重建为转型。</para></summary>
    private static LinearItem ProcessNumericCast(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var mnemonic = insn.Mnemonic;
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var source = IntAt(operands, 1);
        if (dest is int targetRegister && source is int sourceRegister)
        {
            var targetType = mnemonic[(mnemonic.IndexOf("-to-", System.StringComparison.Ordinal) + 4)..];
            SetReg(state, targetRegister, new CastExpr(targetType, RegValue(state, ctx, sourceRegister)));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Rebuilds a cmp-style comparison as a static boxed compare call.
    /// <para>把 cmp 风格的比较重建为静态的装箱 compare 调用。</para></summary>
    private static LinearItem ProcessCompare(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var mnemonic = insn.Mnemonic;
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var first = IntAt(operands, 1);
        var second = IntAt(operands, 2);
        if (dest is int targetRegister && first is int firstRegister && second is int secondRegister)
        {
            var owner = mnemonic.Contains("long", System.StringComparison.Ordinal) ? "java.lang.Long"
                : mnemonic.Contains("float", System.StringComparison.Ordinal) ? "java.lang.Float"
                : "java.lang.Double";
            var compare = new MethodCallExpr(null, owner, "compare", "int",
                [RegValue(state, ctx, firstRegister), RegValue(state, ctx, secondRegister)], isStatic: true);
            SetReg(state, targetRegister, compare);
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Wraps the destination register value in a cast for check-cast.
    /// <para>对 check-cast 把目标寄存器的值包上一层转型。</para></summary>
    private static LinearItem ProcessCheckCast(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var typeRef = TypeRefAt(operands, 1);
        if (dest is int targetRegister && typeRef is not null)
        {
            SetReg(state, targetRegister, new CastExpr(DexFile.FormatDescriptor(typeRef.Descriptor), RegValue(state, ctx, targetRegister)));
        }

        item.Stmt = GuideComment(insn, options);
        return item;
    }

    /// <summary>Records an instanceof test into the destination register.
    /// <para>把 instanceof 判定结果记录到目标寄存器。</para></summary>
    private static LinearItem ProcessInstanceOf(Instruction insn, MethodContext ctx, State state, DecompileOptions options, LinearItem item)
    {
        var operands = insn.Operands;
        var dest = IntAt(operands, 0);
        var source = IntAt(operands, 1);
        var typeRef = TypeRefAt(operands, 2);
        if (dest is int targetRegister && source is int sourceRegister && typeRef is not null)
        {
            SetReg(state, targetRegister, new InstanceOfExpr(RegValue(state, ctx, sourceRegister), DexFile.FormatDescriptor(typeRef.Descriptor)));
        }

        item.Stmt = GuideComment(insn, options);
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