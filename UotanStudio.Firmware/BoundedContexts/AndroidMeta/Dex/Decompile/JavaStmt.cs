using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

/// <summary>
/// Base class for every statement node in the decompiled Java tree; subclasses render one statement via <see cref="WriteTo"/>.
/// <para>反编译 Java 语法树中所有语句节点的基类；子类通过 <see cref="WriteTo"/> 渲染一条语句。</para>
/// </summary>
public abstract class JavaStmt
{
    /// <summary>
    /// Bytecode address this statement was produced from, used to correlate output with the original method.
    /// <para>该语句对应的字节码地址，用于将输出与原方法关联。</para>
    /// </summary>
    /// <value>Zero-based offset into the method bytecode; <c>-1</c> when no source offset is mapped.
    /// <para>方法字节码中从零开始的偏移；未映射来源偏移时为 <c>-1</c>。</para></value>
    public int Offset { get; set; } = -1;

    /// <summary>
    /// Writes the statement into the builder at the given indentation level.
    /// <para>按指定缩进层级写入字符串构建器。</para>
    /// </summary>
    /// <param name="sb">
    /// Builder the rendered Java text is appended to. The statement is written without a trailing newline; the caller appends line breaks between statements.
    /// <para>承接渲染后 Java 文本的构建器。写入的语句不带结尾换行，由调用方在语句之间追加换行。</para>
    /// </param>
    /// <param name="indent">Indentation level counted in four-space blocks. <para>以四个空格为一级的缩进层级。</para></param>
    /// <param name="options">Decompiler formatting options forwarded unchanged to nested statements. <para>反编译格式化选项，原样传递给嵌套语句。</para></param>
    public abstract void WriteTo(StringBuilder sb, int indent, DecompileOptions options);

    /// <summary>
    /// Writes indentation whitespace.
    /// <para>写入缩进空白。</para>
    /// </summary>
    protected static void WriteIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++) sb.Append("    ");
    }

    /// <summary>
    /// Writes a condition expression: binary expressions omit their outer parentheses (conditional contexts such as if/while provide the parentheses themselves).
    /// <para>写入条件表达式：二元表达式省略最外层括号（if/while 等条件上下文自带括号）。</para>
    /// </summary>
    protected static void WriteCondition(StringBuilder sb, JavaExpr cond)
    {
        if (cond is BinaryExpr binary)
        {
            binary.Left.WriteTo(sb);
            sb.Append(' ').Append(binary.Op).Append(' ');
            binary.Right.WriteTo(sb);
        }
        else
        {
            cond.WriteTo(sb);
        }
    }
}

/// <summary>
/// Simple assignment statement rendered as <c>Target = Value;</c>.
/// <para>简单赋值语句，渲染为 <c>Target = Value;</c>。</para>
/// </summary>
public sealed class AssignStmt : JavaStmt
{
    /// <summary>
    /// Assignment target expression written left of the <c>=</c> operator.
    /// <para>写入 <c>=</c> 运算符左侧的赋值目标表达式。</para>
    /// </summary>
    /// <value>Always the expression supplied at construction time.
    /// <para>始终为构造时传入的表达式。</para></value>
    public JavaExpr Target { get; }

    /// <summary>
    /// Assigned expression written right of the <c>=</c> operator.
    /// <para>写入 <c>=</c> 运算符右侧的被赋值表达式。</para>
    /// </summary>
    /// <value>Always the expression supplied at construction time.
    /// <para>始终为构造时传入的表达式。</para></value>
    public JavaExpr Value { get; }

    /// <summary>
    /// Creates an assignment statement.
    /// <para>创建赋值语句。</para>
    /// </summary>
    /// <param name="target">Assignment target expression. <para>赋值目标表达式。</para></param>
    /// <param name="value">Assigned expression. <para>被赋值的表达式。</para></param>
    public AssignStmt(JavaExpr target, JavaExpr value)
    {
        Target = target;
        Value = value;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        Target.WriteTo(sb);
        sb.Append(" = ");
        Value.WriteTo(sb);
        sb.Append(';');
    }
}

/// <summary>
/// Expression statement rendering a bare expression followed by a semicolon, typically a call.
/// <para>表达式语句，将一个独立表达式与分号一起渲染，通常为方法调用。</para>
/// </summary>
public sealed class ExprStmt : JavaStmt
{
    /// <summary>
    /// Expression written as the statement body.
    /// <para>作为语句体写入的表达式。</para>
    /// </summary>
    /// <value>Always the expression supplied at construction time.
    /// <para>始终为构造时传入的表达式。</para></value>
    public JavaExpr Expr { get; }

    /// <summary>
    /// Creates an expression statement.
    /// <para>创建表达式语句。</para>
    /// </summary>
    /// <param name="expr">Expression to render followed by <c>;</c>. <para>渲染后接 <c>;</c> 的表达式。</para></param>
    public ExprStmt(JavaExpr expr) => Expr = expr;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        Expr.WriteTo(sb);
        sb.Append(';');
    }
}

/// <summary>
/// Return statement rendered as <c>return Value;</c>, or bare <c>return;</c> when no value is present.
/// <para>返回语句，渲染为 <c>return Value;</c>；无值时渲染为 <c>return;</c>。</para>
/// </summary>
public sealed class ReturnStmt : JavaStmt
{
    /// <summary>
    /// Returned expression, or <see langword="null"/> for a void return.
    /// <para>被返回的表达式；void 返回时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value><see langword="null"/> renders <c>return;</c>; any expression renders <c>return expr;</c>.
    /// <para>为 <see langword="null"/> 时渲染 <c>return;</c>；有表达式时渲染 <c>return expr;</c>。</para></value>
    public JavaExpr? Value { get; }

    /// <summary>
    /// Creates a return statement.
    /// <para>创建返回语句。</para>
    /// </summary>
    /// <param name="value">Returned expression, or <see langword="null"/> for a void return. <para>被返回的表达式；void 返回时传 <see langword="null"/>。</para></param>
    public ReturnStmt(JavaExpr? value) => Value = value;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("return");
        if (Value is not null)
        {
            sb.Append(' ');
            Value.WriteTo(sb);
        }
        sb.Append(';');
    }
}

/// <summary>
/// Throw statement rendered as <c>throw Value;</c>.
/// <para>抛出语句，渲染为 <c>throw Value;</c>。</para>
/// </summary>
public sealed class ThrowStmt : JavaStmt
{
    /// <summary>
    /// Exception expression written after <c>throw</c>.
    /// <para>写入 <c>throw</c> 之后的异常表达式。</para>
    /// </summary>
    /// <value>Always the expression supplied at construction time.
    /// <para>始终为构造时传入的表达式。</para></value>
    public JavaExpr Value { get; }

    /// <summary>
    /// Creates a throw statement.
    /// <para>创建抛出语句。</para>
    /// </summary>
    /// <param name="value">Thrown exception expression. <para>被抛出的异常表达式。</para></param>
    public ThrowStmt(JavaExpr value) => Value = value;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("throw ");
        Value.WriteTo(sb);
        sb.Append(';');
    }
}

/// <summary>
/// Line comment statement rendered as <c>// Text</c> without a terminating newline.
/// <para>行注释语句，渲染为 <c>// Text</c>，不带结尾换行。</para>
/// </summary>
public sealed class RawCommentStmt : JavaStmt
{
    /// <summary>
    /// Comment body written after the <c>//</c> marker.
    /// <para>写入 <c>//</c> 标记之后的注释正文。</para>
    /// </summary>
    /// <value>The text as supplied at construction time; an empty string renders a bare <c>//</c>.
    /// <para>构造时传入的文本；空字符串渲染为单独的 <c>//</c>。</para></value>
    public string Text { get; }

    /// <summary>
    /// Creates a comment statement.
    /// <para>创建注释语句。</para>
    /// </summary>
    /// <param name="text">Comment body appended after <c>// </c>. <para>追加在 <c>// </c> 之后的注释正文。</para></param>
    public RawCommentStmt(string text) => Text = text;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("// ").Append(Text);
    }
}

/// <summary>
/// Curly-brace block rendering each contained statement one indentation level deeper.
/// <para>花括号代码块，将其中每条语句按更深一级缩进渲染。</para>
/// </summary>
public sealed class BlockStmt : JavaStmt
{
    /// <summary>
    /// Statements enclosed by the block, rendered in list order.
    /// <para>代码块包含的语句，按列表顺序渲染。</para>
    /// </summary>
    /// <value>A mutable list starting empty; an empty list renders only the opening and closing braces with no statements between them.
    /// <para>初始为空的可变列表；为空时仅输出开闭花括号，两者之间没有语句。</para></value>
    public List<JavaStmt> Statements { get; } = [];

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.AppendLine("{");
        foreach (var stmt in Statements)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        sb.Append('}');
    }
}

/// <summary>
/// Conditional statement rendered as <c>if (Cond) { Then }</c>, with an <c>else</c> arm only when <see cref="Else"/> is non-empty.
/// <para>条件语句，渲染为 <c>if (Cond) { Then }</c>；仅当 <see cref="Else"/> 非空时才输出 <c>else</c> 分支。</para>
/// </summary>
public sealed class IfStmt : JavaStmt
{
    /// <summary>
    /// Condition tested before the then-branch runs, written between <c>if (</c> and <c>)</c>.
    /// <para>then 分支执行前测试的条件，写入 <c>if (</c> 与 <c>)</c> 之间。</para>
    /// </summary>
    /// <value>Always the expression supplied at construction time; a binary expression loses its outer parentheses here.
    /// <para>始终为构造时传入的表达式；二元表达式在此处省略最外层括号。</para></value>
    public JavaExpr Cond { get; }

    /// <summary>
    /// Statements executed when the condition holds, rendered one indentation level deeper.
    /// <para>条件成立时执行的语句，按更深一级缩进渲染。</para>
    /// </summary>
    /// <value>A mutable list starting empty.
    /// <para>初始为空的可变列表。</para></value>
    public List<JavaStmt> Then { get; } = [];

    /// <summary>
    /// Statements of the else-arm.
    /// <para>else 分支的语句。</para>
    /// </summary>
    /// <value>A mutable list starting empty; an empty list omits the <c>else</c> arm, and a lone <see cref="IfStmt"/> renders as <c>else if</c>.
    /// <para>初始为空的可变列表；为空时省略 <c>else</c> 分支，仅含一条 <see cref="IfStmt"/> 时渲染为 <c>else if</c>。</para></value>
    public List<JavaStmt> Else { get; } = [];

    /// <summary>
    /// Creates an if statement.
    /// <para>创建 if 语句。</para>
    /// </summary>
    /// <param name="cond">Condition expression tested by the statement. <para>语句测试的条件表达式。</para></param>
    public IfStmt(JavaExpr cond) => Cond = cond;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("if (");
        WriteCondition(sb, Cond);
        sb.AppendLine(") {");
        foreach (var stmt in Then)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        if (Else.Count == 0)
        {
            sb.Append('}');
        }
        else if (Else.Count == 1 && Else[0] is IfStmt nestedIf)
        {
            sb.Append("} else ");


            sb.Append("if (");
            WriteCondition(sb, nestedIf.Cond);
            sb.AppendLine(") {");
            foreach (var stmt in nestedIf.Then)
            {
                stmt.WriteTo(sb, indent + 1, options);
                sb.AppendLine();
            }
            WriteIndent(sb, indent);
            if (nestedIf.Else.Count > 0)
            {
                sb.Append("} else {");
                sb.AppendLine();
                foreach (var stmt in nestedIf.Else)
                {
                    stmt.WriteTo(sb, indent + 1, options);
                    sb.AppendLine();
                }
                WriteIndent(sb, indent);
            }
            sb.Append('}');
        }
        else
        {
            sb.Append("} else {");
            sb.AppendLine();
            foreach (var stmt in Else)
            {
                stmt.WriteTo(sb, indent + 1, options);
                sb.AppendLine();
            }
            WriteIndent(sb, indent);
            sb.Append('}');
        }
    }
}

/// <summary>
/// Pre-test loop rendered as <c>while (Cond) { Body }</c>.
/// <para>先判断的循环，渲染为 <c>while (Cond) { Body }</c>。</para>
/// </summary>
public sealed class WhileStmt : JavaStmt
{
    /// <summary>
    /// Condition re-evaluated before each iteration, written between <c>while (</c> and <c>)</c>.
    /// <para>每次迭代前重新判断的条件，写入 <c>while (</c> 与 <c>)</c> 之间。</para>
    /// </summary>
    /// <value>Always the expression supplied at construction time; a binary expression loses its outer parentheses here.
    /// <para>始终为构造时传入的表达式；二元表达式在此处省略最外层括号。</para></value>
    public JavaExpr Cond { get; }

    /// <summary>
    /// Loop body statements, rendered one indentation level deeper.
    /// <para>循环体语句，按更深一级缩进渲染。</para>
    /// </summary>
    /// <value>A mutable list starting empty.
    /// <para>初始为空的可变列表。</para></value>
    public List<JavaStmt> Body { get; } = [];

    /// <summary>
    /// Creates a while statement.
    /// <para>创建 while 语句。</para>
    /// </summary>
    /// <param name="cond">Condition expression tested before each iteration. <para>每次迭代前测试的条件表达式。</para></param>
    public WhileStmt(JavaExpr cond) => Cond = cond;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("while (");
        WriteCondition(sb, Cond);
        sb.AppendLine(") {");
        foreach (var stmt in Body)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        sb.Append('}');
    }
}

/// <summary>
/// Post-test loop rendered as <c>do { Body } while (Cond);</c>, so the body always runs once.
/// <para>后判断的循环，渲染为 <c>do { Body } while (Cond);</c>，循环体至少执行一次。</para>
/// </summary>
public sealed class DoWhileStmt : JavaStmt
{
    /// <summary>
    /// Condition evaluated after each iteration, written between <c>while (</c> and <c>)</c>.
    /// <para>每次迭代后求值的条件，写入 <c>while (</c> 与 <c>)</c> 之间。</para>
    /// </summary>
    /// <value>Always the expression supplied at construction time; a binary expression loses its outer parentheses here.
    /// <para>始终为构造时传入的表达式；二元表达式在此处省略最外层括号。</para></value>
    public JavaExpr Cond { get; }

    /// <summary>
    /// Loop body statements executed before the first condition test, rendered one indentation level deeper.
    /// <para>在首次条件判断前执行的循环体语句，按更深一级缩进渲染。</para>
    /// </summary>
    /// <value>A mutable list starting empty.
    /// <para>初始为空的可变列表。</para></value>
    public List<JavaStmt> Body { get; } = [];

    /// <summary>
    /// Creates a do-while statement.
    /// <para>创建 do-while 语句。</para>
    /// </summary>
    /// <param name="cond">Condition expression tested after each iteration. <para>每次迭代后测试的条件表达式。</para></param>
    public DoWhileStmt(JavaExpr cond) => Cond = cond;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.AppendLine("do {");
        foreach (var stmt in Body)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        sb.Append("} while (");
        WriteCondition(sb, Cond);
        sb.Append(");");
    }
}

/// <summary>
/// Endless loop rendered as <c>while (true) { Body }</c>.
/// <para>无限循环，渲染为 <c>while (true) { Body }</c>。</para>
/// </summary>
public sealed class InfiniteLoopStmt : JavaStmt
{
    /// <summary>
    /// Loop body statements, rendered one indentation level deeper.
    /// <para>循环体语句，按更深一级缩进渲染。</para>
    /// </summary>
    /// <value>A mutable list starting empty; exit relies on <see cref="BreakStmt"/> or <see cref="ThrowStmt"/> inside the body.
    /// <para>初始为空的可变列表；退出依赖体内的 <see cref="BreakStmt"/> 或 <see cref="ThrowStmt"/>。</para></value>
    public List<JavaStmt> Body { get; } = [];

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.AppendLine("while (true) {");
        foreach (var stmt in Body)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        sb.Append('}');
    }
}

/// <summary>
/// Switch statement rendered as <c>switch (Value) { case …: default: … }</c>; every case body is wrapped in its own braces and terminated with an explicit <c>break;</c>, so no fallthrough is emitted.
/// <para>switch 语句，渲染为 <c>switch (Value) { case …: default: … }</c>；每个 case 分支都用独立花括号包裹并以显式 <c>break;</c> 结束，不会输出贯穿执行。</para>
/// </summary>
public sealed class SwitchStmt : JavaStmt
{
    /// <summary>
    /// Selector expression compared against the case keys, written between <c>switch (</c> and <c>)</c>.
    /// <para>与各 case 键值比较的选择器表达式，写入 <c>switch (</c> 与 <c>)</c> 之间。</para>
    /// </summary>
    /// <value>Always the expression supplied at construction time; it is rendered in full expression form, without the paren-stripping applied to loop and branch conditions.
    /// <para>始终为构造时传入的表达式；按完整表达式形式渲染，不会像循环与分支条件那样省略外层括号。</para></value>
    public JavaExpr Value { get; }

    /// <summary>
    /// Match arms in list order, each pairing an <see cref="long"/> key with its statement body.
    /// <para>按列表顺序排列的匹配分支，每项将一个 <see cref="long"/> 键与其语句体配对。</para>
    /// </summary>
    /// <value>A mutable list starting empty; keys render in the invariant culture, and each body is written two indentation levels deeper than the switch.
    /// <para>初始为空的可变列表；键按固定区域设置渲染，每个分支体比 switch 深两级缩进。</para></value>
    public List<(long Key, List<JavaStmt> Body)> Cases { get; } = [];

    /// <summary>
    /// Statements of the default arm.
    /// <para>default 分支的语句。</para>
    /// </summary>
    /// <value>A mutable list starting empty; an empty list omits the <c>default:</c> arm entirely.
    /// <para>初始为空的可变列表；为空时完全不输出 <c>default:</c> 分支。</para></value>
    public List<JavaStmt> DefaultBody { get; } = [];

    /// <summary>
    /// Creates a switch statement.
    /// <para>创建 switch 语句。</para>
    /// </summary>
    /// <param name="value">Selector expression tested against the case keys. <para>与各 case 键值比较的选择器表达式。</para></param>
    public SwitchStmt(JavaExpr value) => Value = value;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("switch (");
        Value.WriteTo(sb);
        sb.AppendLine(") {");
        foreach (var (key, body) in Cases)
        {
            WriteIndent(sb, indent + 1);
            sb.Append("case ").Append(key.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(": {");
            foreach (var stmt in body)
            {
                stmt.WriteTo(sb, indent + 2, options);
                sb.AppendLine();
            }
            WriteIndent(sb, indent + 1);
            sb.AppendLine("break;");
            WriteIndent(sb, indent + 1);
            sb.AppendLine("}");
        }
        if (DefaultBody.Count > 0)
        {
            WriteIndent(sb, indent + 1);
            sb.AppendLine("default: {");
            foreach (var stmt in DefaultBody)
            {
                stmt.WriteTo(sb, indent + 2, options);
                sb.AppendLine();
            }
            WriteIndent(sb, indent + 1);
            sb.AppendLine("}");
        }
        WriteIndent(sb, indent);
        sb.Append('}');
    }
}

/// <summary>
/// Loop or switch exit rendered as <c>break;</c>, marking the end of an innermost enclosing body.
/// <para>循环或 switch 的退出语句，渲染为 <c>break;</c>，标记最内层代码块的结束。</para>
/// </summary>
public sealed class BreakStmt : JavaStmt
{
    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("break;");
    }
}

/// <summary>
/// Guidance comment rendered as <c>// goto Label</c>, marking a bytecode branch the decompiler did not turn into structured control flow.
/// <para>指导注释，渲染为 <c>// goto Label</c>，标记反编译器未能还原为结构化控制流的字节码分支。</para>
/// </summary>
public sealed class GotoCommentStmt : JavaStmt
{
    /// <summary>
    /// Bytecode target address named after the <c>goto</c> marker.
    /// <para>写在 <c>goto</c> 标记之后的字节码目标地址。</para>
    /// </summary>
    /// <value>The label text as supplied at construction time; the control-flow structurer generates it from the target basic block's start offset.
    /// <para>构造时传入的标签文本；控制流结构化器按目标基本块的起始偏移生成该文本。</para></value>
    public string Label { get; }

    /// <summary>
    /// Creates a goto guidance comment.
    /// <para>创建 goto 指导注释。</para>
    /// </summary>
    /// <param name="label">Text appended after <c>// goto </c> naming the branch target. <para>追加在 <c>// goto </c> 之后、标明分支目标的文本。</para></param>
    public GotoCommentStmt(string label) => Label = label;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("// goto ").Append(Label);
    }
}